using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Claret.Models;
using Claret.Services;

namespace Claret.Controls
{
    /// <summary>
    /// Remote directory tree for the active session, rooted at the directory the login lands in.
    /// Children are fetched when a node is expanded rather than up front — walking a whole remote
    /// filesystem eagerly would be unusable.
    /// </summary>
    public sealed partial class RemoteFilesView : UserControl
    {
        private readonly Dictionary<TerminalView, RemoteFileService> _services = new();

        private TerminalView? _session;
        private CancellationTokenSource? _work;

        /// <summary>Directory the tree is currently rooted at; null means "use the login folder".</summary>
        private string? _root;

        /// <summary>Cancels the running upload; null when nothing is being transferred.</summary>
        private CancellationTokenSource? _transfer;

        public RemoteFilesView()
        {
            InitializeComponent();

            DropHint.Background = AppAccent.DropFillBrush();
            DropHint.BorderBrush = AppAccent.Brush();
        }

        /// <summary>Owner window for the file picker; set by the shell.</summary>
        public IntPtr WindowHandle { get; set; }

        /// <summary>Raised when the user explicitly asks for a command to be run in the shell.</summary>
        public event EventHandler<string>? CommandRequested;

        /// <summary>
        /// Whether files appear alongside folders. Off by default: the tree is mainly for choosing a
        /// folder, and files bury the folders. Turning it on is what makes downloads reachable.
        /// </summary>
        public bool ShowFiles { get; private set; }

        /// <summary>
        /// Folder downloads are written to without asking. Empty means "show the save dialog", which
        /// is the default.
        /// </summary>
        public string DownloadFolder { get; set; } = string.Empty;

        /// <summary>True when a file is selected, so a download has something to act on.</summary>
        public bool CanDownload => Tree.SelectedNode?.Content is RemoteEntry { IsDirectory: false, IsError: false };

        /// <summary>True while the tree is pointed at a connected session.</summary>
        public bool IsLive => _session is not null && _root is not null;

        /// <summary>True while a transfer is running; menus disable their transfer commands then.</summary>
        public bool IsTransferring => _transfer is not null;

        /// <summary>Switches files on or off and re-reads the tree, since the listing changes.</summary>
        public async Task SetShowFilesAsync(bool showFiles)
        {
            ShowFilesButton.IsChecked = showFiles;

            if (ShowFiles == showFiles)
            {
                return;
            }

            ShowFiles = showFiles;

            if (IsLive)
            {
                await LoadRootAsync();
            }
        }

        /// <summary>
        /// Raised when the header toggle changed what the tree shows, so the shell can remember it.
        /// The panel owns the switch; only the preference outlives this window.
        /// </summary>
        public event EventHandler<bool>? ShowFilesChanged;

        private async void OnShowFilesClick(object sender, RoutedEventArgs e)
        {
            bool showFiles = ShowFilesButton.IsChecked == true;

            ShowFilesChanged?.Invoke(this, showFiles);
            await SetShowFilesAsync(showFiles);
        }

        /// <summary>Menu entry point: same as the header's upload button.</summary>
        public Task PickAndUploadAsync() => UploadFromPickerAsync();

        /// <summary>Menu entry point: same as the tree's Refresh.</summary>
        public Task RefreshAsync() => LoadRootAsync();

        /// <summary>
        /// Points the tree at a session. Each session keeps its own SFTP connection, so switching
        /// back and forth does not re-authenticate.
        /// </summary>
        public async Task ShowAsync(TerminalView? session)
        {
            if (ReferenceEquals(_session, session))
            {
                return;
            }

            _session = session;
            _root = null;
            Tree.RootNodes.Clear();

            if (session is null)
            {
                ShowStatus("No open sessions", busy: false, canRetry: false);
                SetNavigationEnabled(false);
                PathText.Text = string.Empty;
                return;
            }

            await LoadRootAsync();
        }

        /// <summary>Drops a session's SFTP connection when its terminal closes.</summary>
        public void Forget(TerminalView session)
        {
            if (_services.Remove(session, out RemoteFileService? service))
            {
                service.Dispose();
            }

            if (ReferenceEquals(_session, session))
            {
                // Its SFTP connection is going away, so any transfer riding on it is dead too.
                _transfer?.Cancel();
                Transfer.Visibility = Visibility.Collapsed;
                _session = null;
                _root = null;
                Tree.RootNodes.Clear();
                ShowStatus("No open sessions", busy: false, canRetry: false);
                SetNavigationEnabled(false);
                PathText.Text = string.Empty;
            }
        }

        public void ForgetAll()
        {
            _transfer?.Cancel();

            foreach (RemoteFileService service in _services.Values)
            {
                service.Dispose();
            }

            _services.Clear();
            _session = null;
            Tree.RootNodes.Clear();
        }

        private async void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            // A refresh after a failure should retry the connection, so drop the cached one.
            if (_session is { } session
                && _services.TryGetValue(session, out RemoteFileService? stale)
                && !stale.IsConnected)
            {
                _services.Remove(session);
                stale.Dispose();
            }

            await LoadRootAsync();
        }

        /// <summary>Re-roots one level up. Stops at "/" — there is nothing above it.</summary>
        private async void OnUpClick(object sender, RoutedEventArgs e)
        {
            if (ParentOf(_root) is not { } parent)
            {
                return;
            }

            _root = parent;
            await LoadRootAsync();
        }

        private async void OnHomeClick(object sender, RoutedEventArgs e)
        {
            _root = null;
            await LoadRootAsync();
        }

        /// <summary>POSIX parent path, or null when already at the filesystem root.</summary>
        private static string? ParentOf(string? path)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                return null;
            }

            string trimmed = path.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            return slash switch
            {
                < 0 => null,
                0 => "/",
                _ => trimmed[..slash],
            };
        }

        private void SetNavigationEnabled(bool enabled)
        {
            RefreshButton.IsEnabled = enabled;
            HomeButton.IsEnabled = enabled;
            UpButton.IsEnabled = enabled && ParentOf(_root) is not null;
            UploadButton.IsEnabled = enabled && _transfer is null;
        }

        private async Task LoadRootAsync()
        {
            if (_session is not { } session || session.Connection is not { } connection)
            {
                ShowStatus("No open sessions", busy: false, canRetry: false);
                return;
            }

            _work?.Cancel();
            _work = new CancellationTokenSource();
            CancellationToken token = _work.Token;

            ShowStatus($"Connecting to {connection.Profile.Endpoint}…", busy: true, canRetry: false);
            Tree.RootNodes.Clear();

            RemoteFileService service;
            try
            {
                service = await GetServiceAsync(session, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                ShowStatus(Describe(ex), busy: false, canRetry: true);
                return;
            }

            string root = _root ?? service.HomeDirectory;
            _root = root;
            PathText.Text = root;
            SetNavigationEnabled(true);

            try
            {
                IReadOnlyList<RemoteEntry> entries = await service.ListAsync(root, ShowFiles, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                foreach (RemoteEntry entry in entries)
                {
                    Tree.RootNodes.Add(CreateNode(entry));
                }

                if (entries.Count == 0)
                {
                    ShowStatus($"{root} is empty", busy: false, canRetry: false);
                }
                else
                {
                    HideStatus();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ShowStatus(Describe(ex), busy: false, canRetry: true);
            }
        }

        private async Task<RemoteFileService> GetServiceAsync(TerminalView session, CancellationToken token)
        {
            if (_services.TryGetValue(session, out RemoteFileService? existing) && existing.IsConnected)
            {
                return existing;
            }

            if (existing is not null)
            {
                _services.Remove(session);
                existing.Dispose();
            }

            (ConnectionProfile Profile, string? Secret) connection =
                session.Connection ?? throw new InvalidOperationException("The session has no connection details.");

            var service = new RemoteFileService();
            try
            {
                await service.ConnectAsync(connection.Profile, connection.Secret, token);
            }
            catch
            {
                service.Dispose();
                throw;
            }

            _services[session] = service;
            return service;
        }

        private static TreeViewNode CreateNode(RemoteEntry entry) => new()
        {
            Content = entry,
            // Directories advertise children they have not fetched yet; that is what makes the
            // expand chevron appear and what triggers Expanding.
            HasUnrealizedChildren = entry.IsDirectory,
        };

        private async void OnExpanding(TreeView sender, TreeViewExpandingEventArgs args)
        {
            TreeViewNode node = args.Node;
            if (!node.HasUnrealizedChildren || node.Content is not RemoteEntry entry)
            {
                return;
            }

            // Clear the flag first: expanding again while the listing is in flight would double up.
            node.HasUnrealizedChildren = false;

            if (_session is not { } session
                || !_services.TryGetValue(session, out RemoteFileService? service)
                || _work is null)
            {
                return;
            }

            await FillChildrenAsync(node, entry, service, _work.Token);
        }

        /// <summary>Replaces a node's children with a fresh listing, reporting failures on the node itself.</summary>
        private async Task FillChildrenAsync(
            TreeViewNode node,
            RemoteEntry entry,
            RemoteFileService service,
            CancellationToken token)
        {
            try
            {
                IReadOnlyList<RemoteEntry> children =
                    await service.ListAsync(entry.FullPath, ShowFiles, token);

                node.Children.Clear();
                foreach (RemoteEntry child in children)
                {
                    node.Children.Add(CreateNode(child));
                }
            }
            catch (OperationCanceledException)
            {
                node.HasUnrealizedChildren = true;
            }
            catch (Exception ex)
            {
                // Report inline: a permission error on one folder should not blank the whole tree.
                node.Children.Clear();
                node.Children.Add(new TreeViewNode
                {
                    Content = new RemoteEntry
                    {
                        Name = Describe(ex),
                        FullPath = entry.FullPath,
                        IsError = true,
                    },
                });
            }
        }

        private void OnItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            if (args.InvokedItem is not TreeViewNode { Content: RemoteEntry entry } node)
            {
                return;
            }

            // Folders just open in place. Typing into the shell instead would move the session's
            // working directory behind the user's back, which browsing should never do — the
            // right-click menu is where changing directory lives, because that is deliberate.
            if (entry.IsDirectory)
            {
                node.IsExpanded = !node.IsExpanded;
            }
        }

        /// <summary>
        /// Right-click selects before the context flyout opens. The menu, the upload button and
        /// dropped files all act on the selection, so they must all mean the folder just clicked.
        /// </summary>
        private void OnItemRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element
                || element.DataContext is not TreeViewNode { Content: RemoteEntry entry } node)
            {
                return;
            }

            if (entry.IsError)
            {
                // An error row is not a place: nothing in the menu could act on it.
                e.Handled = true;
                return;
            }

            Tree.SelectedNode = node;

            if (element.ContextFlyout is not MenuFlyout menu)
            {
                return;
            }

            foreach (MenuFlyoutItemBase item in menu.Items)
            {
                item.Visibility = (item.Tag as string) switch
                {
                    "folder" => entry.IsDirectory ? Visibility.Visible : Visibility.Collapsed,
                    "file" => entry.IsDirectory ? Visibility.Collapsed : Visibility.Visible,
                    _ => Visibility.Visible,
                };
            }
        }

        /// <summary>Menu commands act on the row the right-click just selected.</summary>
        private RemoteEntry? MenuTarget =>
            Tree.SelectedNode?.Content is RemoteEntry { IsError: false } entry ? entry : null;

        private void OnGoToFolderClick(object sender, RoutedEventArgs e)
        {
            if (MenuTarget is { IsDirectory: true } entry)
            {
                CommandRequested?.Invoke(this, $"cd '{entry.FullPath}'");
            }
        }

        private async void OnDownloadClick(object sender, RoutedEventArgs e) =>
            await DownloadSelectedAsync();

        private async void OnUploadHereClick(object sender, RoutedEventArgs e) =>
            await UploadFromPickerAsync();

        private async void OnDownloadFolderClick(object sender, RoutedEventArgs e) =>
            await DownloadSelectedFolderAsync();

        private async void OnUploadFolderHereClick(object sender, RoutedEventArgs e) =>
            await PickAndUploadFolderAsync();

        private void OnCopyPathClick(object sender, RoutedEventArgs e)
        {
            if (MenuTarget is not { } entry)
            {
                return;
            }

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(entry.FullPath);
            Clipboard.SetContent(package);
        }

        /// <summary>
        /// Saves the selected remote file locally, into <see cref="DownloadFolder"/> when one is set
        /// and through the save dialog otherwise.
        /// </summary>
        public async Task DownloadSelectedAsync()
        {
            if (!IsLive
                || _transfer is not null
                || _session is not { } session
                || Tree.SelectedNode?.Content is not RemoteEntry { IsDirectory: false, IsError: false } entry)
            {
                return;
            }

            if (await ResolveDownloadPathAsync(entry) is not { } localPath)
            {
                return;
            }

            RemoteFileService service;
            try
            {
                service = await GetServiceAsync(session, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ShowTransfer(Describe(ex), null);
                return;
            }

            var cts = new CancellationTokenSource();
            _transfer = cts;
            UploadButton.IsEnabled = false;

            string? failure = null;
            try
            {
                ShowTransfer($"Downloading {entry.Name}", 0);

                var progress = new ThrottledProgress<ulong>(bytes => ShowTransfer(
                    $"Downloading {entry.Name} — {RemoteEntry.FormatSize((long)bytes)} of {entry.SizeText}",
                    Fraction((long)bytes, entry.Length)));

                await service.DownloadAsync(entry.FullPath, localPath, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                failure = "Download cancelled.";
            }
            catch (Exception ex)
            {
                failure = Describe(ex);
            }
            finally
            {
                _transfer = null;
                cts.Dispose();
                SetNavigationEnabled(IsLive);
            }

            ShowTransfer(failure ?? $"Saved to {localPath}", failure is null ? 1 : null);
        }

        /// <summary>
        /// Decides where a download is written, or null when the user backs out. A configured folder
        /// skips the dialog; anything wrong with that folder falls back to asking rather than failing.
        /// </summary>
        private async Task<string?> ResolveDownloadPathAsync(RemoteEntry entry)
        {
            if (DownloadFolder.Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(DownloadFolder);
                    string path = Path.Combine(DownloadFolder, entry.Name);

                    if (File.Exists(path)
                        && !await ConfirmAsync(
                            "File already exists",
                            $"{entry.Name} already exists in {DownloadFolder}. Overwrite it?",
                            "Overwrite"))
                    {
                        return null;
                    }

                    return path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A folder that moved or turned read-only should not block the download.
                    ShowTransfer($"Cannot use {DownloadFolder}: {ex.Message}", null);
                }
            }

            try
            {
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.Downloads,
                    SuggestedFileName = entry.Name,
                };

                // A remote file may have any extension or none; offer a wildcard so the picker
                // accepts the name as-is rather than appending one.
                picker.FileTypeChoices.Add("File", new List<string> { "." });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

                // The dialog also handles the overwrite prompt, so there is nothing to confirm here.
                StorageFile? target = await picker.PickSaveFileAsync();
                return target?.Path;
            }
            catch (Exception ex)
            {
                ShowTransfer($"Cannot open the save dialog: {ex.Message}", null);
                return null;
            }
        }

        private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = message,
                PrimaryButtonText = confirmText,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            try
            {
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            }
            catch (Exception)
            {
                // Another dialog owns the root; the safe answer is to leave the file alone.
                return false;
            }
        }

        /// <summary>Where an upload lands: the picked folder, a picked file's folder, or the tree root.</summary>
        private string TargetDirectory(TreeViewNode? node)
        {
            string fallback = _root ?? "/";
            if (node?.Content is not RemoteEntry entry)
            {
                return fallback;
            }

            return entry.IsDirectory ? entry.FullPath : ParentOf(entry.FullPath) ?? fallback;
        }

        private async void OnUploadClick(object sender, RoutedEventArgs e) =>
            await UploadFromPickerAsync();

        private async Task UploadFromPickerAsync()
        {
            if (!IsLive || _transfer is not null)
            {
                return;
            }

            var paths = new List<string>();
            try
            {
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.List,
                    SuggestedStartLocation = PickerLocationId.ComputerFolder,
                };

                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

                foreach (StorageFile file in await picker.PickMultipleFilesAsync())
                {
                    paths.Add(file.Path);
                }
            }
            catch (Exception ex)
            {
                ShowTransfer($"Cannot open the file picker: {ex.Message}", null);
                return;
            }

            if (paths.Count == 0)
            {
                return;
            }

            await UploadAsync(paths, TargetDirectory(Tree.SelectedNode));
        }

        private void OnCancelTransferClick(object sender, RoutedEventArgs e)
        {
            // While a transfer runs this aborts it; afterwards it just clears the finished message.
            if (_transfer is { } cts)
            {
                cts.Cancel();
                return;
            }

            Transfer.Visibility = Visibility.Collapsed;
        }

        private bool CanAcceptDrop(DragEventArgs e) =>
            _session is not null
            && _root is not null
            && _transfer is null
            && e.DataView.Contains(StandardDataFormats.StorageItems);

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (!CanAcceptDrop(e))
            {
                e.AcceptedOperation = DataPackageOperation.None;
                DropHint.Visibility = Visibility.Collapsed;
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            DropHint.Visibility = Visibility.Visible;

            if (e.DragUIOverride is { } overlay)
            {
                overlay.Caption = $"Upload to {TargetDirectory(Tree.SelectedNode)}";
                overlay.IsCaptionVisible = true;
            }
        }

        private void OnDragLeave(object sender, DragEventArgs e) =>
            DropHint.Visibility = Visibility.Collapsed;

        private async void OnDrop(object sender, DragEventArgs e)
        {
            DropHint.Visibility = Visibility.Collapsed;

            if (!CanAcceptDrop(e))
            {
                return;
            }

            // The selected folder decides where this lands, not wherever the file happened to be
            // released. Dropping is a coarse gesture; the selection is the one the user made on
            // purpose, and the drag caption showed it before the release.
            string target = TargetDirectory(Tree.SelectedNode);

            var paths = new List<string>();
            var folders = new List<string>();

            // The data view stops being readable once the handler returns, hence the deferral.
            var deferral = e.GetDeferral();
            try
            {
                foreach (IStorageItem item in await e.DataView.GetStorageItemsAsync())
                {
                    if (item is StorageFile file)
                    {
                        paths.Add(file.Path);
                    }
                    else if (item is StorageFolder folder)
                    {
                        folders.Add(folder.Path);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowTransfer($"Cannot read the dropped items: {ex.Message}", null);
                return;
            }
            finally
            {
                deferral.Complete();
            }

            if (paths.Count == 0 && folders.Count == 0)
            {
                ShowTransfer("Nothing to upload.", null);
                return;
            }

            if (paths.Count > 0)
            {
                await UploadAsync(paths, target);
            }

            // One folder at a time: each is measured, may ask about replacing, and owns the progress
            // bar while it runs. Dropping three of them queues them rather than interleaving them.
            foreach (string folder in folders)
            {
                await UploadFolderAsync(folder, target);
            }
        }

        private async Task UploadAsync(IReadOnlyList<string> localPaths, string targetDirectory)
        {
            if (_session is not { } session)
            {
                return;
            }

            RemoteFileService service;
            try
            {
                service = await GetServiceAsync(session, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ShowTransfer(Describe(ex), null);
                return;
            }

            var queue = new List<(string Path, string Name, long Length)>();
            long total = 0;
            foreach (string path in localPaths)
            {
                try
                {
                    var info = new FileInfo(path);
                    queue.Add((path, info.Name, info.Length));
                    total += info.Length;
                }
                catch (Exception)
                {
                    // Vanished or unreadable between the drop and here: leave it out of the queue.
                }
            }

            if (queue.Count == 0)
            {
                ShowTransfer("None of the selected files could be read.", null);
                return;
            }

            var cts = new CancellationTokenSource();
            _transfer = cts;
            UploadButton.IsEnabled = false;

            long sent = 0;
            int done = 0;
            int skipped = 0;
            bool overwriteAll = false;
            string? failure = null;

            try
            {
                foreach ((string path, string name, long length) in queue)
                {
                    string remote = Combine(targetDirectory, name);
                    string label = $"{name} — {done + skipped + 1}/{queue.Count}";

                    bool overwrite = overwriteAll;
                    if (!overwrite && await service.ExistsAsync(remote, cts.Token))
                    {
                        OverwriteChoice choice = await AskOverwriteAsync(name);
                        if (choice == OverwriteChoice.Skip)
                        {
                            skipped++;
                            sent += length;
                            continue;
                        }

                        overwriteAll = choice == OverwriteChoice.All;
                        overwrite = true;
                    }

                    long baseline = sent;
                    ShowTransfer($"Uploading {label}", Fraction(baseline, total));

                    var progress = new ThrottledProgress<ulong>(bytes =>
                        ShowTransfer($"Uploading {label}", Fraction(baseline + (long)bytes, total)));

                    await service.UploadAsync(path, remote, overwrite, progress, cts.Token);

                    sent = baseline + length;
                    done++;
                }
            }
            catch (OperationCanceledException)
            {
                failure = $"Upload cancelled after {done} of {queue.Count} files.";
            }
            catch (Exception ex)
            {
                failure = Describe(ex);
            }
            finally
            {
                _transfer = null;
                cts.Dispose();
                SetNavigationEnabled(_root is not null && _session is not null);
            }

            ShowTransfer(
                failure ?? $"Uploaded {done} file{(done == 1 ? string.Empty : "s")} to {targetDirectory}"
                    + (skipped > 0 ? $" ({skipped} skipped)" : string.Empty),
                failure is null && done > 0 ? 1 : null);

            if (done > 0)
            {
                await RefreshDirectoryAsync(targetDirectory);
            }
        }

        // ---------- folders, recursively ----------

        /// <summary>Whether the selection is a folder this could take a whole copy of.</summary>
        public bool CanDownloadFolder =>
            Tree.SelectedNode?.Content is RemoteEntry { IsDirectory: true, IsError: false };

        /// <summary>Picks a local folder and copies all of it into the selected remote directory.</summary>
        public async Task PickAndUploadFolderAsync()
        {
            if (!IsLive || _transfer is not null)
            {
                return;
            }

            string? local;
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

                StorageFolder? folder = await picker.PickSingleFolderAsync();
                local = folder?.Path;
            }
            catch (Exception ex)
            {
                ShowTransfer($"Cannot open the folder picker: {ex.Message}", null);
                return;
            }

            if (string.IsNullOrEmpty(local))
            {
                return;
            }

            await UploadFolderAsync(local, TargetDirectory(Tree.SelectedNode));
        }

        /// <summary>
        /// Copies a local folder and everything under it to the far end, keeping the shape of the
        /// tree. The whole thing is measured first: a progress bar that cannot say how much is left
        /// is no better than a spinner, and the count is also how the transfer is refused when the
        /// folder turns out to be enormous.
        /// </summary>
        public async Task UploadFolderAsync(string localRoot, string targetDirectory)
        {
            if (_session is not { } session || _transfer is not null)
            {
                return;
            }

            string name = new DirectoryInfo(localRoot).Name;
            if (name.Length == 0)
            {
                ShowTransfer("A whole drive cannot be uploaded — pick a folder inside it.", null);
                return;
            }

            RemoteFileService service;
            try
            {
                service = await GetServiceAsync(session, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ShowTransfer(Describe(ex), null);
                return;
            }

            ShowTransfer($"Reading {name}…", null);
            ScanResult scan = await Task.Run(() => FolderScan.Local(localRoot));

            if (scan.Truncated)
            {
                ShowTransfer(
                    $"{name} holds more than {FolderScan.MaxItems} items — upload a smaller folder.",
                    null);
                return;
            }

            if (scan.FileCount == 0 && scan.DirectoryCount == 0)
            {
                ShowTransfer($"{name} is empty.", null);
                return;
            }

            string remoteRoot = Combine(targetDirectory, name);

            OverwriteChoice policy = OverwriteChoice.All;
            if (await service.ExistsAsync(remoteRoot, CancellationToken.None))
            {
                if (await AskFolderOverwriteAsync(name, remoteRoot) is not { } chosen)
                {
                    return;
                }

                policy = chosen;
            }

            var cts = new CancellationTokenSource();
            _transfer = cts;
            UploadButton.IsEnabled = false;

            long sent = 0;
            int done = 0;
            int skipped = 0;
            int failed = 0;
            string? failure = null;

            try
            {
                await service.EnsureDirectoryAsync(remoteRoot, cts.Token);

                foreach (ScanItem item in scan.Items)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    string remote = remoteRoot + "/" + item.Relative;

                    if (item.IsDirectory)
                    {
                        await service.EnsureDirectoryAsync(remote, cts.Token);
                        continue;
                    }

                    if (policy == OverwriteChoice.Skip && await service.ExistsAsync(remote, cts.Token))
                    {
                        skipped++;
                        sent += item.Length;
                        continue;
                    }

                    long baseline = sent;
                    string label = $"{done + skipped + 1}/{scan.FileCount} — {item.Relative}";
                    ShowTransfer($"Uploading {label}", Fraction(baseline, scan.TotalBytes));

                    var progress = new ThrottledProgress<ulong>(bytes => ShowTransfer(
                        $"Uploading {label}",
                        Fraction(baseline + (long)bytes, scan.TotalBytes)));

                    try
                    {
                        await service.UploadAsync(item.FullPath, remote, overwrite: true, progress, cts.Token);
                        done++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // One unreadable file should not throw away the rest of the tree.
                        failed++;
                    }

                    sent = baseline + item.Length;
                }
            }
            catch (OperationCanceledException)
            {
                failure = $"Upload cancelled after {done} of {scan.FileCount} files.";
            }
            catch (Exception ex)
            {
                failure = Describe(ex);
            }
            finally
            {
                _transfer = null;
                cts.Dispose();
                SetNavigationEnabled(IsLive);
            }

            ShowTransfer(
                failure ?? $"Uploaded {name} to {targetDirectory} — {Tally(done, skipped, failed, scan)}",
                failure is null && done > 0 ? 1 : null);

            if (done > 0)
            {
                await RefreshDirectoryAsync(targetDirectory);
            }
        }

        /// <summary>Copies the selected remote folder, and everything under it, to a local folder.</summary>
        public async Task DownloadSelectedFolderAsync()
        {
            if (!IsLive
                || _transfer is not null
                || _session is not { } session
                || Tree.SelectedNode?.Content is not RemoteEntry { IsDirectory: true, IsError: false } entry)
            {
                return;
            }

            if (await ResolveFolderDestinationAsync() is not { } destination)
            {
                return;
            }

            RemoteFileService service;
            try
            {
                service = await GetServiceAsync(session, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ShowTransfer(Describe(ex), null);
                return;
            }

            ShowTransfer($"Reading {entry.Name}…", null);

            ScanResult scan;
            try
            {
                scan = await service.WalkAsync(entry.FullPath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ShowTransfer(Describe(ex), null);
                return;
            }

            if (scan.Truncated)
            {
                ShowTransfer(
                    $"{entry.Name} holds more than {FolderScan.MaxItems} items — download a subfolder instead.",
                    null);
                return;
            }

            if (scan.FileCount == 0 && scan.DirectoryCount == 0)
            {
                ShowTransfer($"{entry.Name} is empty.", null);
                return;
            }

            string localRoot = Path.Combine(destination, entry.Name);

            OverwriteChoice policy = OverwriteChoice.All;
            if (Directory.Exists(localRoot))
            {
                if (await AskFolderOverwriteAsync(entry.Name, localRoot) is not { } chosen)
                {
                    return;
                }

                policy = chosen;
            }

            var cts = new CancellationTokenSource();
            _transfer = cts;
            UploadButton.IsEnabled = false;

            long received = 0;
            int done = 0;
            int skipped = 0;
            int failed = 0;
            string? failure = null;

            try
            {
                Directory.CreateDirectory(localRoot);

                foreach (ScanItem item in scan.Items)
                {
                    cts.Token.ThrowIfCancellationRequested();

                    // The relative path is POSIX; on this side it has to become a Windows one.
                    string local = Path.Combine(localRoot, item.Relative.Replace('/', Path.DirectorySeparatorChar));

                    if (item.IsDirectory)
                    {
                        Directory.CreateDirectory(local);
                        continue;
                    }

                    if (policy == OverwriteChoice.Skip && File.Exists(local))
                    {
                        skipped++;
                        received += item.Length;
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(local) ?? localRoot);

                    long baseline = received;
                    string label = $"{done + skipped + 1}/{scan.FileCount} — {item.Relative}";
                    ShowTransfer($"Downloading {label}", Fraction(baseline, scan.TotalBytes));

                    var progress = new ThrottledProgress<ulong>(bytes => ShowTransfer(
                        $"Downloading {label}",
                        Fraction(baseline + (long)bytes, scan.TotalBytes)));

                    try
                    {
                        await service.DownloadAsync(item.FullPath, local, progress, cts.Token);
                        done++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A file the far end will not hand over, or a name this filesystem refuses.
                        failed++;
                    }

                    received = baseline + item.Length;
                }
            }
            catch (OperationCanceledException)
            {
                failure = $"Download cancelled after {done} of {scan.FileCount} files.";
            }
            catch (Exception ex)
            {
                failure = Describe(ex);
            }
            finally
            {
                _transfer = null;
                cts.Dispose();
                SetNavigationEnabled(IsLive);
            }

            ShowTransfer(
                failure ?? $"Saved to {localRoot} — {Tally(done, skipped, failed, scan)}",
                failure is null && done > 0 ? 1 : null);
        }

        /// <summary>
        /// Where a folder download is written. The configured download folder is used as the parent
        /// when there is one, since that is what it means; otherwise the user picks a parent.
        /// </summary>
        private async Task<string?> ResolveFolderDestinationAsync()
        {
            if (DownloadFolder.Length > 0)
            {
                try
                {
                    Directory.CreateDirectory(DownloadFolder);
                    return DownloadFolder;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ShowTransfer($"Cannot use {DownloadFolder}: {ex.Message} — pick another folder.", null);
                }
            }

            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);

                StorageFolder? folder = await picker.PickSingleFolderAsync();
                return folder?.Path;
            }
            catch (Exception ex)
            {
                ShowTransfer($"Cannot open the folder picker: {ex.Message}", null);
                return null;
            }
        }

        /// <summary>
        /// One decision for the whole folder. Asking per file is unusable at a few hundred of them,
        /// and answering "overwrite" three hundred times is not consent, it is fatigue.
        /// Null means the transfer was called off.
        /// </summary>
        private async Task<OverwriteChoice?> AskFolderOverwriteAsync(string name, string destination)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Folder already exists",
                Content = $"{destination} is already there.\n\n"
                    + $"Files inside it with the same names as those in {name} can be replaced, "
                    + "or left as they are. Anything else in it is untouched either way.",
                PrimaryButtonText = "Replace files",
                SecondaryButtonText = "Keep existing",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            try
            {
                return await dialog.ShowAsync() switch
                {
                    ContentDialogResult.Primary => OverwriteChoice.All,
                    ContentDialogResult.Secondary => OverwriteChoice.Skip,
                    _ => null,
                };
            }
            catch (Exception)
            {
                // Another dialog owns the root; leaving the folder alone is the safe answer.
                return null;
            }
        }

        /// <summary>The count at the end of a folder transfer, mentioning only what happened.</summary>
        private static string Tally(int done, int skipped, int failed, ScanResult scan)
        {
            var parts = new List<string> { $"{done} file{(done == 1 ? string.Empty : "s")}" };

            if (skipped > 0)
            {
                parts.Add($"{skipped} kept");
            }

            if (failed > 0)
            {
                parts.Add($"{failed} failed");
            }

            if (scan.SkippedLinks > 0)
            {
                parts.Add($"{scan.SkippedLinks} link{(scan.SkippedLinks == 1 ? string.Empty : "s")} skipped");
            }

            return string.Join(", ", parts);
        }

        private static double Fraction(long sent, long total) =>
            total <= 0 ? 1 : Math.Clamp((double)sent / total, 0, 1);

        /// <summary>Joins a POSIX directory and a file name without the Windows separator.</summary>
        private static string Combine(string directory, string name)
        {
            string trimmed = directory.TrimEnd('/');
            return trimmed.Length == 0 ? "/" + name : trimmed + "/" + name;
        }

        private enum OverwriteChoice
        {
            Overwrite,
            All,
            Skip,
        }

        private async Task<OverwriteChoice> AskOverwriteAsync(string name)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "File already exists",
                Content = $"{name} already exists in the target folder.",
                PrimaryButtonText = "Overwrite",
                SecondaryButtonText = "Overwrite all",
                CloseButtonText = "Skip",
                DefaultButton = ContentDialogButton.Primary,
            };

            try
            {
                return await dialog.ShowAsync() switch
                {
                    ContentDialogResult.Primary => OverwriteChoice.Overwrite,
                    ContentDialogResult.Secondary => OverwriteChoice.All,
                    _ => OverwriteChoice.Skip,
                };
            }
            catch (Exception)
            {
                // Another dialog already owns the root; the safe answer is to leave the file alone.
                return OverwriteChoice.Skip;
            }
        }

        /// <summary>Re-lists a folder after a transfer, so new files show up without a full reload.</summary>
        private async Task RefreshDirectoryAsync(string path)
        {
            if (string.Equals(path, _root, StringComparison.Ordinal))
            {
                await LoadRootAsync();
                return;
            }

            if (_session is not { } session
                || !_services.TryGetValue(session, out RemoteFileService? service)
                || _work is null
                || FindNode(Tree.RootNodes, path) is not { Content: RemoteEntry entry } node
                // Never expanded: the listing it will fetch on first expand is already current.
                || node.HasUnrealizedChildren)
            {
                return;
            }

            await FillChildrenAsync(node, entry, service, _work.Token);
        }

        private static TreeViewNode? FindNode(IEnumerable<TreeViewNode> nodes, string fullPath)
        {
            foreach (TreeViewNode node in nodes)
            {
                if (node.Content is RemoteEntry entry
                    && string.Equals(entry.FullPath, fullPath, StringComparison.Ordinal))
                {
                    return node;
                }

                if (FindNode(node.Children, fullPath) is { } match)
                {
                    return match;
                }
            }

            return null;
        }

        private void ShowTransfer(string message, double? progress)
        {
            Transfer.Visibility = Visibility.Visible;
            TransferBar.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;

            if (progress is { } value)
            {
                string percent = $"{value * 100:F2}%";
                TransferText.Text = $"{message} ({percent})";
                TransferBar.Value = value;
                ToolTipService.SetToolTip(TransferBar, percent);
            }
            else
            {
                TransferText.Text = message;
            }

            ToolTipService.SetToolTip(TransferCancelButton, _transfer is null ? "Dismiss" : "Cancel upload");
        }

        private void ShowStatus(string message, bool busy, bool canRetry)
        {
            Status.Visibility = Visibility.Visible;
            StatusText.Text = message;
            BusyRing.IsActive = busy;
            StatusRetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HideStatus()
        {
            Status.Visibility = Visibility.Collapsed;
            BusyRing.IsActive = false;
        }

        private static string Describe(Exception ex) => ex switch
        {
            HostKeyMismatchException => ex.Message,
            Renci.SshNet.Common.SshAuthenticationException =>
                "SFTP authentication failed. The server may not allow this account to use SFTP.",
            Renci.SshNet.Common.SshException when ex.Message.Contains("subsystem", StringComparison.OrdinalIgnoreCase) =>
                "The server does not offer the SFTP subsystem.",
            _ => ex.Message,
        };
    }

    /// <summary>
    /// Wraps a progress callback so only a few updates a second reach the UI thread. SSH.NET
    /// reports upload/download progress once per write chunk — for a large file over a fast link
    /// that is hundreds of callbacks a second, each normally marshaled onto the UI thread via
    /// <see cref="Progress{T}"/>. That is enough to starve terminal rendering and keyboard input
    /// for the whole window, so the throttle is checked on the reporting thread, before anything
    /// is posted.
    /// </summary>
    internal sealed class ThrottledProgress<T> : IProgress<T>
    {
        private readonly IProgress<T> _inner;
        private readonly long _intervalMs;
        private long _lastReportMs;
        private bool _reported;

        public ThrottledProgress(Action<T> report, long intervalMs = 100)
        {
            _inner = new Progress<T>(report);
            _intervalMs = intervalMs;
        }

        public void Report(T value)
        {
            long now = Environment.TickCount64;
            if (_reported && now - _lastReportMs < _intervalMs)
            {
                return;
            }

            _reported = true;
            _lastReportMs = now;
            _inner.Report(value);
        }
    }
}
