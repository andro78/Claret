using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Claret.Models;
using Claret.Services;

namespace Claret.Controls
{
    /// <summary>
    /// Sidebar "Settings" card. Only shows and asks: the shell owns the stores, applies each change,
    /// and calls <see cref="Show"/> back with what is now in effect — the same split as the Serial
    /// panel, so the card never holds a second copy of a setting that can drift.
    /// </summary>
    public sealed partial class SettingsView : UserControl
    {
        private bool _loading;

        public SettingsView()
        {
            InitializeComponent();
        }

        public event EventHandler<bool>? CopyOnSelectChanged;

        public event EventHandler<int>? ScrollbackChanged;

        public event EventHandler<bool>? SerialTimestampsChanged;

        /// <summary>The user wants to pick the font new tabs start with.</summary>
        public event EventHandler? FontRequested;

        /// <summary>The user wants to pick the colours new tabs start with.</summary>
        public event EventHandler? ColorsRequested;

        public event EventHandler? ChooseDownloadFolderRequested;

        public event EventHandler? AskEachTimeRequested;

        public event EventHandler? CheckUpdatesRequested;

        /// <summary>Fills the card from what is in effect. Raises nothing.</summary>
        public void Show(WorkspaceLayout layout, TerminalAppearance defaults, string version)
        {
            _loading = true;

            CopyOnSelectSwitch.IsOn = layout.CopyOnSelect;
            ScrollbackBox.Value = layout.ScrollbackLines;
            SerialTimestampsSwitch.IsOn = layout.SerialTimestamps;

            FontText.Text = $"{(defaults.FontFamily.Length > 0 ? defaults.FontFamily : "Automatic")} · {defaults.SafeFontSize}";
            ToolTipService.SetToolTip(FontText, FontText.Text);

            ColorSwatch.Background = new SolidColorBrush(defaults.BackgroundColor);
            ColorSwatchText.Foreground = new SolidColorBrush(defaults.ForegroundColor);
            ColorText.Text = defaults.SchemeName.Length > 0 ? defaults.SchemeName : "Custom";

            bool asks = layout.DownloadFolder.Length == 0;
            DownloadFolderText.Text = asks ? "Ask where to save each time" : layout.DownloadFolder;
            AskEachTimeButton.Visibility = asks ? Visibility.Collapsed : Visibility.Visible;

            VersionText.Text = $"Version {version}";

            _loading = false;
        }

        private void OnCopyOnSelectToggled(object sender, RoutedEventArgs e)
        {
            if (!_loading)
            {
                CopyOnSelectChanged?.Invoke(this, CopyOnSelectSwitch.IsOn);
            }
        }

        private void OnScrollbackChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading || double.IsNaN(args.NewValue))
            {
                return;
            }

            int lines = (int)Math.Clamp(
                Math.Round(args.NewValue),
                WorkspaceLayout.MinScrollbackLines,
                WorkspaceLayout.MaxScrollbackLines);

            ScrollbackChanged?.Invoke(this, lines);
        }

        private void OnSerialTimestampsToggled(object sender, RoutedEventArgs e)
        {
            if (!_loading)
            {
                SerialTimestampsChanged?.Invoke(this, SerialTimestampsSwitch.IsOn);
            }
        }

        private void OnFontClick(object sender, RoutedEventArgs e) => FontRequested?.Invoke(this, EventArgs.Empty);

        private void OnColorsClick(object sender, RoutedEventArgs e) => ColorsRequested?.Invoke(this, EventArgs.Empty);

        private void OnChooseFolderClick(object sender, RoutedEventArgs e) =>
            ChooseDownloadFolderRequested?.Invoke(this, EventArgs.Empty);

        private void OnAskEachTimeClick(object sender, RoutedEventArgs e) =>
            AskEachTimeRequested?.Invoke(this, EventArgs.Empty);

        private void OnCheckUpdatesClick(object sender, RoutedEventArgs e) =>
            CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
    }
}
