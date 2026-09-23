using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using Claret.Controls;
using Claret.Models;
using Claret.Services;

namespace Claret.Dialogs
{
    /// <summary>
    /// Picks a file, a protocol, and (optionally) a different baud rate for the send, then runs it
    /// in place — the dialog stays open through the whole transfer so the progress bar and a
    /// cancel button have somewhere to live. The caller supplies how to actually send a byte: this
    /// dialog knows nothing about <see cref="TerminalView"/> or <see cref="SerialSession"/>.
    /// </summary>
    public sealed partial class FileTransferDialog : ContentDialog
    {
        private readonly IntPtr _ownerWindowHandle;
        private readonly int _currentBaudRate;
        private readonly Func<string, FileTransferProtocol, int?, IProgress<FileTransferProgress>, CancellationToken, Task> _sendFileAsync;

        private string? _selectedPath;
        private CancellationTokenSource? _cts;
        private bool _transferring;
        private bool _finished;

        public FileTransferDialog(
            IntPtr ownerWindowHandle,
            int currentBaudRate,
            Func<string, FileTransferProtocol, int?, IProgress<FileTransferProgress>, CancellationToken, Task> sendFileAsync)
        {
            InitializeComponent();

            _ownerWindowHandle = ownerWindowHandle;
            _currentBaudRate = currentBaudRate;
            _sendFileAsync = sendFileAsync;

            var rates = new List<string> { $"Keep current ({currentBaudRate})" };
            foreach (int rate in SerialConnection.CommonBaudRates)
            {
                rates.Add(rate.ToString(CultureInfo.InvariantCulture));
            }

            BaudBox.ItemsSource = rates;
            BaudBox.SelectedIndex = 0;

            PrimaryButtonClick += OnPrimaryButtonClick;
            CloseButtonClick += (_, _) => _cts?.Cancel();
        }

        private async void OnBrowseClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerWindowHandle);

                StorageFile? file = await picker.PickSingleFileAsync();
                if (file is not null)
                {
                    _selectedPath = file.Path;
                    FilePathBox.Text = file.Path;
                }
            }
            catch (Exception ex)
            {
                ShowError($"Cannot open the file picker: {ex.Message}");
            }
        }

        private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            // This button never closes the dialog on its own — only Hide()/the close button do,
            // once the transfer this click starts has actually finished.
            args.Cancel = true;

            if (_transferring || _finished)
            {
                return;
            }

            if (_selectedPath is not { Length: > 0 } path || !File.Exists(path))
            {
                ShowError("Choose a file to send first.");
                return;
            }

            FileTransferProtocol protocol = ProtocolButtons.SelectedIndex switch
            {
                1 => FileTransferProtocol.Ymodem,
                2 => FileTransferProtocol.Zmodem,
                _ => FileTransferProtocol.Xmodem,
            };

            int? baudRate = BaudBox.SelectedIndex > 0
                ? SerialConnection.CommonBaudRates[BaudBox.SelectedIndex - 1]
                : null;

            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            _transferring = true;
            _cts = new CancellationTokenSource();

            SetInputsEnabled(false);
            ErrorBar.IsOpen = false;
            SuccessBar.IsOpen = false;
            ProgressPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            TransferProgressBar.Value = 0;
            ProgressText.Text = $"0 B / {RemoteEntry.FormatSize(new FileInfo(path).Length)}";

            var progress = new ThrottledProgress<FileTransferProgress>(UpdateProgress);

            try
            {
                await _sendFileAsync(path, protocol, baudRate, progress, _cts.Token).ConfigureAwait(true);
                SuccessBar.Message = $"Sent {Path.GetFileName(path)}.";
                SuccessBar.IsOpen = true;
                _finished = true;
                CloseButtonText = "Close";
            }
            catch (OperationCanceledException)
            {
                ErrorBar.Message = "Cancelled.";
                ErrorBar.IsOpen = true;
                _finished = true;
                CloseButtonText = "Close";
            }
            catch (Exception ex)
            {
                ErrorBar.Message = ex.Message;
                ErrorBar.IsOpen = true;
                SetInputsEnabled(true);
            }
            finally
            {
                _transferring = false;
                deferral.Complete();
            }
        }

        private void UpdateProgress(FileTransferProgress p)
        {
            TransferProgressBar.Value = p.Fraction;
            ProgressText.Text =
                $"{RemoteEntry.FormatSize(p.BytesSent)} / {RemoteEntry.FormatSize(p.TotalBytes)} ({p.Fraction:P0})";
        }

        private void SetInputsEnabled(bool enabled)
        {
            BrowseButton.IsEnabled = enabled;
            ProtocolButtons.IsEnabled = enabled;
            BaudBox.IsEnabled = enabled;
        }

        private void ShowError(string message)
        {
            ErrorBar.Message = message;
            ErrorBar.IsOpen = true;
        }
    }
}
