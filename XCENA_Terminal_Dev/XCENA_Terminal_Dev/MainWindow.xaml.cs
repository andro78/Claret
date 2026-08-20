using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using XCENA_Terminal_Dev.Controls;
using XCENA_Terminal_Dev.Dialogs;
using XCENA_Terminal_Dev.Models;
using XCENA_Terminal_Dev.Services;

namespace XCENA_Terminal_Dev
{
    /// <summary>Which card the sidebar rail is showing.</summary>
    internal enum SidebarTab
    {
        Sessions,
        Files,
        Tools,
    }

    /// <summary>
    /// Shell of the app: a saved-profile sidebar and a <see cref="SessionSurface"/>. The surface
    /// owns every terminal; each of its panes carries its own tab strip, so the window itself has
    /// no tab bar.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly ProfileStore _profileStore = new();
        private readonly RecentStore _recentStore = new();
        private readonly AppearanceStore _appearanceStore = new();
        private readonly LayoutStore _layoutStore = new();
        private readonly HighlightStore _highlightStore = new();
        private readonly SessionSurface _surface = new();
        private readonly IntPtr _windowHandle;

        private SidebarSplitter? _sidebarSplitter;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _imeTimer;
        private ImeMode _imeMode = ImeMode.Unavailable;

        private bool _sidebarVisible = true;
        private bool _dialogOpen;

        public MainWindow()
        {
            _profileStore.Load();
            _recentStore.Load();
            _appearanceStore.Load();
            _layoutStore.Load();
            _highlightStore.Load();

            InitializeComponent();

            _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // The caption buttons overlay the right edge, so pad the bar to keep Panel clear of them.
            AppTitleBar.SizeChanged += (_, _) => ApplyTitleBarInset();
            ApplyTitleBarInset();

            // The title bar is custom, so the taskbar button is the only place the icon shows.
            // An unpackaged window does not pick up the exe icon on its own.
            try
            {
                AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
            }
            catch (Exception)
            {
                // A missing icon is not worth failing startup over; Windows falls back to the default.
            }

            AppWindow.Resize(new SizeInt32(1180, 760));
            if (AppWindow.Title.Length == 0)
            {
                AppWindow.Title = "XCENA Terminal";
            }

            _surface.WindowHandle = _windowHandle;
            _surface.ApplyAppearance(_appearanceStore.Current);
            SurfaceHost.Children.Add(_surface);
            Files.WindowHandle = _windowHandle;
            Files.DownloadFolder = _layoutStore.Current.DownloadFolder;
            // Nothing is connected yet, so this only records the preference; the first listing reads it.
            _ = Files.SetShowFilesAsync(_layoutStore.Current.ShowRemoteFiles);
            _surface.SessionClosing += (_, view) => Files.Forget(view);
            Files.CommandRequested += (_, command) => _surface.ActiveView?.SendInput(command + "\n");
            Tools.Initialize(_highlightStore.Rules);
            Tools.RulesChanged += (_, _) =>
            {
                _highlightStore.Save();
                _surface.ApplyHighlights(_highlightStore.Rules);
            };
            _surface.ApplyHighlights(_highlightStore.Rules);
            _surface.ApplyCopyOnSelect(_layoutStore.Current.CopyOnSelect);

            _surface.WindowCommandRequested += OnSurfaceWindowCommand;
            _surface.NewSessionRequested += (_, _) => ShowNewSessionMenu(_surface.ActiveAddAnchor);
            _surface.DuplicateRequested += (_, view) => DuplicateSession(view);
            _surface.ActiveSessionChanged += (_, _) => OnActiveSessionChanged();
            _surface.Emptied += (_, _) =>
            {
                UpdateEmptyState();
                UpdateLayoutChrome();
            };

            SetUpSidebar();
            SetUpStatusBar();
            SelectSidebarTab(SidebarTab.Sessions);
            RegisterAccelerators();
            UpdateProfileEmptyState();
            UpdateEmptyState();
            UpdateLayoutChrome();

            _profileStore.Profiles.CollectionChanged += (_, _) => UpdateProfileEmptyState();
            Closed += OnWindowClosed;

            if (_profileStore.LoadError is not null)
            {
                AppTitleText.Text = $"XCENA Terminal — cannot read profiles: {_profileStore.LoadError}";
            }
        }

        public ObservableCollection<ConnectionProfile> Profiles => _profileStore.Profiles;

        /// <summary>
        /// Keeps the right-hand title bar controls clear of the system caption buttons, which
        /// overlay the extended title bar. RightInset is physical pixels, so it needs scaling down.
        /// </summary>
        private void ApplyTitleBarInset()
        {
            double scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
            double inset = scale > 0 ? AppWindow.TitleBar.RightInset / scale : 0;

            // Zero means the platform has not reported the buttons yet; the fallback covers all three.
            if (inset <= 0)
            {
                inset = 144;
            }

            var padding = new Thickness(12, 0, inset + 4, 0);
            if (!padding.Equals(AppTitleBar.Padding))
            {
                AppTitleBar.Padding = padding;
            }
        }

        private void RegisterAccelerators()
        {
            AddAccelerator(VirtualKey.T, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
                () => ShowNewSessionMenu(_surface.ActiveAddAnchor));
            AddAccelerator(VirtualKey.W, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, CloseActiveSession);
            AddAccelerator(VirtualKey.B, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, ToggleSidebar);

            // VK_OEM_PLUS / VK_OEM_MINUS; the terminal forwards the same chords when it holds focus.
            AddAccelerator((VirtualKey)187, VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift,
                () => SplitActive(Orientation.Horizontal));
            AddAccelerator((VirtualKey)189, VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift,
                () => SplitActive(Orientation.Vertical));

            AddAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control, () => _surface.CycleActive(1));
            AddAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
                () => _surface.CycleActive(-1));
        }

        private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                action();
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }

        // ---------- profile sidebar ----------

        private void OnToggleSidebarClick(object sender, RoutedEventArgs e) => ToggleSidebar();

        private void ToggleSidebar()
        {
            _sidebarVisible = !_sidebarVisible;
            ApplySidebarLayout();
        }

        /// <summary>Builds the resize divider between the panel and the sessions.</summary>
        private void SetUpSidebar()
        {
            _sidebarSplitter = new SidebarSplitter(
                SidebarColumn,
                () => _layoutStore.Current.SidebarOnRight,
                width =>
                {
                    _layoutStore.Current.SidebarWidth = width;
                    _layoutStore.Save();
                });

            Grid.SetColumn(_sidebarSplitter, 1);
            BodyGrid.Children.Add(_sidebarSplitter);

            ApplySidebarLayout();
        }

        /// <summary>The grid column the sidebar currently occupies.</summary>
        private ColumnDefinition SidebarColumn =>
            _layoutStore.Current.SidebarOnRight ? RightColumn : LeftColumn;

        private ColumnDefinition SessionColumn =>
            _layoutStore.Current.SidebarOnRight ? LeftColumn : RightColumn;

        private void ApplySidebarLayout()
        {
            bool onRight = _layoutStore.Current.SidebarOnRight;
            double width = _layoutStore.Current.SidebarWidth;

            Grid.SetColumn(Sidebar, onRight ? 2 : 0);
            Grid.SetColumn(SessionArea, onRight ? 0 : 2);
            Sidebar.Margin = onRight ? new Thickness(4, 6, 8, 8) : new Thickness(8, 6, 4, 8);

            Sidebar.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;

            if (_sidebarSplitter is not null)
            {
                _sidebarSplitter.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
            }

            SplitterColumn.Width = new GridLength(_sidebarVisible ? SidebarSplitter.Thickness : 0);
            SidebarColumn.Width = new GridLength(_sidebarVisible ? width : 0);
            SessionColumn.Width = new GridLength(1, GridUnitType.Star);

            PanelShowItem.IsChecked = _sidebarVisible;
            PanelDockLeftItem.IsChecked = !onRight;
            PanelDockRightItem.IsChecked = onRight;
        }

        private void OnDockLeftClick(object sender, RoutedEventArgs e) => DockSidebar(onRight: false);

        private void OnDockRightClick(object sender, RoutedEventArgs e) => DockSidebar(onRight: true);

        private void DockSidebar(bool onRight)
        {
            if (_layoutStore.Current.SidebarOnRight == onRight && _sidebarVisible)
            {
                return;
            }

            _layoutStore.Current.SidebarOnRight = onRight;
            _layoutStore.Save();
            _sidebarVisible = true;
            ApplySidebarLayout();
        }

        private void UpdateProfileEmptyState()
        {
            NoProfilesText.Visibility = _profileStore.Profiles.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnProfileDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (ProfileList.SelectedItem is ConnectionProfile profile)
            {
                _ = ConnectProfileAsync(profile);
            }
        }

        private void OnConnectProfileClick(object sender, RoutedEventArgs e)
        {
            if (ResolveProfile(sender) is ConnectionProfile profile)
            {
                _ = ConnectProfileAsync(profile);
            }
        }

        private void OnEditProfileClick(object sender, RoutedEventArgs e)
        {
            if (ResolveProfile(sender) is ConnectionProfile profile)
            {
                _ = EditProfileAsync(profile);
            }
        }

        private void OnForgetHostKeyClick(object sender, RoutedEventArgs e)
        {
            if (ResolveProfile(sender) is ConnectionProfile profile)
            {
                KnownHostsStore.Instance.Forget(profile.Host, profile.Port);
            }
        }

        private async void OnDeleteProfileClick(object sender, RoutedEventArgs e)
        {
            if (ResolveProfile(sender) is not ConnectionProfile profile)
            {
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Delete profile",
                Content = $"Delete the profile '{profile.DisplayName}'?",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await ShowDialogAsync(confirm) == ContentDialogResult.Primary)
            {
                _profileStore.Remove(profile);
            }
        }

        /// <summary>Menu flyout items carry the right-clicked item in DataContext; fall back to the selection.</summary>
        private ConnectionProfile? ResolveProfile(object sender)
        {
            if (sender is FrameworkElement { DataContext: ConnectionProfile fromContext })
            {
                return fromContext;
            }

            return ProfileList.SelectedItem as ConnectionProfile;
        }

        // ---------- connecting ----------

        private void OnNewConnectionClick(object sender, RoutedEventArgs e) =>
            ShowNewSessionMenu(sender as FrameworkElement);

        private void NewConnection() => _ = NewConnectionAsync();

        /// <summary>
        /// The "+" button offers the saved profiles first — that is what a new session usually is —
        /// with the full connection dialog as the last entry.
        /// </summary>
        private void ShowNewSessionMenu(FrameworkElement? anchor)
        {
            if (anchor is null)
            {
                NewConnection();
                return;
            }

            var menu = new MenuFlyout { XamlRoot = RootGrid.XamlRoot };

            // Recent first: reconnecting to what you just used is the common case.
            IReadOnlyList<RecentConnection> recent = _recentStore.Items;
            if (recent.Count > 0)
            {
                menu.Items.Add(SectionLabel("Recent"));

                foreach (RecentConnection entry in recent.Take(8))
                {
                    RecentConnection captured = entry;
                    var item = new MenuFlyoutItem
                    {
                        Text = captured.DisplayName,
                        // Shown right-aligned in grey, which is exactly where the endpoint belongs.
                        KeyboardAcceleratorTextOverride = captured.Endpoint,
                    };
                    item.Click += (_, _) => _ = ConnectRecentAsync(captured);
                    menu.Items.Add(item);
                }

                var clear = new MenuFlyoutItem { Text = "Clear history" };
                clear.Click += (_, _) => _recentStore.Clear();
                menu.Items.Add(clear);
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            if (_profileStore.Profiles.Count > 0)
            {
                menu.Items.Add(SectionLabel("Saved connections"));

                foreach (ConnectionProfile profile in _profileStore.Profiles)
                {
                    ConnectionProfile captured = profile;
                    var item = new MenuFlyoutItem
                    {
                        Text = captured.DisplayName,
                        KeyboardAcceleratorTextOverride = captured.Endpoint,
                    };
                    item.Click += (_, _) => _ = ConnectProfileAsync(captured);
                    menu.Items.Add(item);
                }

                menu.Items.Add(new MenuFlyoutSeparator());
            }

            var custom = new MenuFlyoutItem { Text = "New connection…" };
            custom.Click += (_, _) => NewConnection();
            menu.Items.Add(custom);

            menu.ShowAt(anchor);
        }

        /// <summary>MenuFlyout has no header item, so a disabled entry stands in for one.</summary>
        private static MenuFlyoutItem SectionLabel(string text) => new()
        {
            Text = text,
            IsEnabled = false,
        };

        private async Task ConnectRecentAsync(RecentConnection entry)
        {
            // Prefer the saved profile so any DPAPI-stored secret still applies.
            ConnectionProfile? profile =
                _profileStore.Profiles.FirstOrDefault(p => p.Id == entry.ProfileId)
                ?? _profileStore.Profiles.FirstOrDefault(p =>
                    string.Equals(p.Host, entry.Host, StringComparison.OrdinalIgnoreCase)
                    && p.Port == entry.Port
                    && string.Equals(p.Username, entry.Username, StringComparison.Ordinal));

            await ConnectProfileAsync(profile ?? entry.ToProfile());
        }

        private async void OnAppearanceClick(object sender, RoutedEventArgs e)
        {
            var dialog = new AppearanceDialog(_appearanceStore.Current);
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }

            _appearanceStore.Save(dialog.Result);
            _surface.ApplyAppearance(_appearanceStore.Current);
        }

        private void DuplicateSession(TerminalView view)
        {
            if (view.Connection is { } connection)
            {
                _ = OpenSessionAsync(connection.Profile, connection.Secret);
            }
        }

        private async Task NewConnectionAsync()
        {
            var dialog = new ConnectionDialog(_windowHandle);
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }

            if (dialog.ShouldSaveProfile)
            {
                _profileStore.AddOrUpdate(dialog.Profile);
            }

            await OpenSessionAsync(dialog.Profile, dialog.Secret);
        }

        private async Task ConnectProfileAsync(ConnectionProfile profile)
        {
            string? secret = profile.RememberSecret
                ? SecretProtector.Unprotect(profile.ProtectedSecret)
                : null;

            // Without a stored secret, reopen the dialog pre-filled so the user only types the password.
            if (secret is null && profile.AuthMode == SshAuthMode.Password)
            {
                var dialog = new ConnectionDialog(_windowHandle, profile);
                if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
                {
                    return;
                }

                if (dialog.ShouldSaveProfile)
                {
                    _profileStore.AddOrUpdate(dialog.Profile);
                }

                await OpenSessionAsync(dialog.Profile, dialog.Secret);
                return;
            }

            await OpenSessionAsync(profile, secret);
        }

        private async Task EditProfileAsync(ConnectionProfile profile)
        {
            var dialog = new ConnectionDialog(_windowHandle, profile, editOnly: true);
            if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            {
                _profileStore.AddOrUpdate(dialog.Profile);
            }
        }

        /// <summary>WinUI allows only one ContentDialog at a time; swallow overlapping requests.</summary>
        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
        {
            if (_dialogOpen)
            {
                return ContentDialogResult.None;
            }

            _dialogOpen = true;
            try
            {
                dialog.XamlRoot = RootGrid.XamlRoot;
                return await dialog.ShowAsync();
            }
            finally
            {
                _dialogOpen = false;
            }
        }

        private async Task OpenSessionAsync(ConnectionProfile profile, string? secret)
        {
            ConnectionProfile snapshot = profile.Clone();
            TerminalView view = _surface.AddSession(snapshot);

            UpdateEmptyState();
            UpdateLayoutChrome();
            OnActiveSessionChanged();

            await _surface.StartAsync(view, snapshot, secret);

            // Only successful connections earn a history entry; failed attempts would be noise.
            if (view.State == TerminalState.Connected)
            {
                _recentStore.Record(snapshot);
            }
        }

        private void CloseActiveSession()
        {
            if (_surface.ActiveView is { } view)
            {
                _surface.CloseSession(view);
            }
        }

        // ---------- layout ----------

        private void SplitActive(Orientation orientation)
        {
            _surface.SplitActive(orientation);
            UpdateLayoutChrome();
        }

        private void OnLayoutSingleClick(object sender, RoutedEventArgs e)
        {
            _surface.MergeAll();
            UpdateLayoutChrome();
        }

        private void OnLayoutSideClick(object sender, RoutedEventArgs e)
        {
            _surface.SpreadAll(Orientation.Horizontal);
            UpdateLayoutChrome();
        }

        private void OnLayoutStackClick(object sender, RoutedEventArgs e)
        {
            _surface.SpreadAll(Orientation.Vertical);
            UpdateLayoutChrome();
        }

        private void OnSurfaceWindowCommand(object? sender, TerminalCommand command)
        {
            if (command == TerminalCommand.ToggleSidebar)
            {
                ToggleSidebar();
            }
        }

        private void OnActiveSessionChanged()
        {
            UpdateWindowTitle();
            UpdateStatusSession();
            UpdateLayoutChrome();
            SyncFilesTab();
        }

        // ---------- status bar ----------

        /// <summary>
        /// Starts the IME poll. Windows raises no event when Han/Yeong is pressed, and the focused
        /// control lives in the WebView2 process, so the mode has to be asked for on a timer.
        /// </summary>
        private void SetUpStatusBar()
        {
            _imeTimer = DispatcherQueue.CreateTimer();
            _imeTimer.Interval = TimeSpan.FromMilliseconds(250);
            _imeTimer.Tick += (_, _) =>
            {
                UpdateImeStatus();
                UpdateStatusSession();
                UpdateStatusFont();
            };
            _imeTimer.Start();

            UpdateImeStatus();
            UpdateStatusSession();
            UpdateStatusFont();
        }

        /// <summary>
        /// Names the font the terminal is drawing with. Automatic is resolved to the family it
        /// actually landed on, because "Automatic" alone does not answer the question.
        /// </summary>
        private void UpdateStatusFont()
        {
            string chosen = _appearanceStore.Current.FontFamily;

            int size = _appearanceStore.Current.SafeFontSize;

            string text = chosen.Length > 0
                ? $"{chosen} · {size}"
                : $"{FontProbe.ResolveAutomatic(TerminalFont.AutomaticOrder) ?? "Cascadia Mono"} (auto) · {size}";

            if (StatusFont.Text != text)
            {
                StatusFont.Text = text;
            }
        }

        private void UpdateImeStatus()
        {
            ImeMode mode = ImeStatus.Read(_windowHandle);

            // Unavailable means another app is in front and owns the IME; the last reading is still
            // the best thing to show, so leave the pill alone rather than claiming Latin.
            if (mode == ImeMode.Unavailable || mode == _imeMode)
            {
                return;
            }

            _imeMode = mode;
            bool hangul = mode == ImeMode.Hangul;
            StatusIme.Text = hangul ? "Kor" : "Eng";
            StatusImePill.Background = hangul
                ? AppAccent.Brush()
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            StatusIme.Foreground = hangul
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }

        /// <summary>
        /// Called from the same tick as the IME poll rather than wired to a dozen events; the text
        /// is only assigned when it actually changes.
        /// </summary>
        private void UpdateStatusSession()
        {
            TerminalView? view = _surface.ActiveView;

            string text;
            if (view?.Profile is not { } profile)
            {
                text = "No open sessions";
            }
            else
            {
                string platform = view.Platform.IsKnown
                    ? view.Platform.Name.Length > 0
                        ? view.Platform.Name
                        : RemotePlatform.Describe(view.Platform.Os)
                    : string.Empty;

                text = platform.Length > 0 ? $"{profile.Endpoint}  ·  {platform}" : profile.Endpoint;
            }

            if (StatusSession.Text != text)
            {
                StatusSession.Text = text;
            }
        }

        private void OnCopyOnSelectClick(object sender, RoutedEventArgs e)
        {
            _layoutStore.Current.CopyOnSelect = CopyOnSelectItem.IsChecked;
            _layoutStore.Save();
            _surface.ApplyCopyOnSelect(_layoutStore.Current.CopyOnSelect);
        }

        /// <summary>Font family and size in one dialog, with a preview that shows CJK alignment.</summary>
        private async void OnFontClick(object sender, RoutedEventArgs e)
        {
            var dialog = new FontDialog(_appearanceStore.Current);
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }

            TerminalAppearance next = _appearanceStore.Current.Clone();
            next.FontFamily = dialog.Family;
            next.FontSize = dialog.Size;

            _appearanceStore.Save(next);
            _surface.ApplyAppearance(_appearanceStore.Current);
        }

        // ---------- sidebar tabs ----------

        private void OnSessionsTabClick(object sender, RoutedEventArgs e) => SelectSidebarTab(SidebarTab.Sessions);

        private void OnFilesTabClick(object sender, RoutedEventArgs e) => SelectSidebarTab(SidebarTab.Files);

        private void OnToolsTabClick(object sender, RoutedEventArgs e) => SelectSidebarTab(SidebarTab.Tools);

        private void SelectSidebarTab(SidebarTab tab)
        {
            // "New connection" lives inside the profile card, so hiding the card hides it too.
            ProfileCard.Visibility = Show(tab == SidebarTab.Sessions);
            FilesCard.Visibility = Show(tab == SidebarTab.Files);
            ToolsCard.Visibility = Show(tab == SidebarTab.Tools);

            var accent = AppAccent.Brush();
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var dim = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            var bright = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

            SessionsTabMarker.Background = tab == SidebarTab.Sessions ? accent : clear;
            FilesTabMarker.Background = tab == SidebarTab.Files ? accent : clear;
            ToolsTabMarker.Background = tab == SidebarTab.Tools ? accent : clear;

            SessionsTabIcon.Foreground = tab == SidebarTab.Sessions ? bright : dim;
            FilesTabIcon.Foreground = tab == SidebarTab.Files ? bright : dim;
            ToolsTabIcon.Foreground = tab == SidebarTab.Tools ? bright : dim;

            if (tab == SidebarTab.Files)
            {
                SyncFilesTab();
            }
        }

        private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Points the file tree at whichever session is active, when that tab is showing.</summary>
        private void SyncFilesTab()
        {
            if (FilesCard.Visibility != Visibility.Visible)
            {
                return;
            }

            _ = Files.ShowAsync(_surface.ActiveView);
        }

        private void UpdateWindowTitle()
        {
            TerminalView? view = _surface.ActiveView;
            if (view is null)
            {
                AppTitleText.Text = "XCENA Terminal";
                return;
            }

            string label = view.RemoteTitle ?? view.Profile?.Endpoint ?? string.Empty;
            AppTitleText.Text = label.Length == 0
                ? "XCENA Terminal"
                : $"XCENA Terminal — {label}";
        }

        /// <summary>
        /// The SFTP commands only make sense against a connected tree, and only one transfer runs at
        /// a time, so the state is resolved when the menu opens rather than tracked continuously.
        /// </summary>
        private void OnOptionsOpening(object sender, object e)
        {
            CopyOnSelectItem.IsChecked = _layoutStore.Current.CopyOnSelect;
            SftpShowFilesItem.IsChecked = Files.ShowFiles;

            bool live = Files.IsLive;
            bool idle = live && !Files.IsTransferring;

            SftpUploadItem.IsEnabled = idle;
            SftpDownloadItem.IsEnabled = idle && Files.CanDownload;
            SftpRefreshItem.IsEnabled = live;

            SftpDownloadItem.Text = Files.ShowFiles
                ? "Download selected file…"
                : "Download selected file… (turn on Show files)";

            string folder = _layoutStore.Current.DownloadFolder;
            SftpDownloadFolderItem.Text = folder.Length > 0
                ? $"Download folder: {folder}"
                : "Download folder: ask each time";
            SftpAskEachTimeItem.IsEnabled = folder.Length > 0;
        }

        /// <summary>
        /// Picks the folder downloads go to. Setting one skips the save dialog from then on, which is
        /// the point: repeated downloads from the same server should not need a dialog each time.
        /// </summary>
        private async void OnSftpDownloadFolderClick(object sender, RoutedEventArgs e)
        {
            string? path;
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };

                // A folder picker with no filter returns nothing on some Windows builds.
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);

                StorageFolder? folder = await picker.PickSingleFolderAsync();
                path = folder?.Path;
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(new ContentDialog
                {
                    Title = "Download folder",
                    Content = $"Cannot open the folder picker: {ex.Message}",
                    CloseButtonText = "Close",
                });

                return;
            }

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            _layoutStore.Current.DownloadFolder = path;
            _layoutStore.Save();
            Files.DownloadFolder = path;
        }

        private void OnSftpAskEachTimeClick(object sender, RoutedEventArgs e)
        {
            _layoutStore.Current.DownloadFolder = string.Empty;
            _layoutStore.Save();
            Files.DownloadFolder = string.Empty;
        }

        private async void OnSftpShowFilesClick(object sender, RoutedEventArgs e)
        {
            bool showFiles = SftpShowFilesItem.IsChecked;
            _layoutStore.Current.ShowRemoteFiles = showFiles;
            _layoutStore.Save();

            SelectSidebarTab(SidebarTab.Files);
            await Files.SetShowFilesAsync(showFiles);
        }

        private async void OnSftpUploadClick(object sender, RoutedEventArgs e)
        {
            SelectSidebarTab(SidebarTab.Files);
            await Files.PickAndUploadAsync();
        }

        private async void OnSftpDownloadClick(object sender, RoutedEventArgs e)
        {
            SelectSidebarTab(SidebarTab.Files);
            await Files.DownloadSelectedAsync();
        }

        private async void OnSftpRefreshClick(object sender, RoutedEventArgs e)
        {
            SelectSidebarTab(SidebarTab.Files);
            await Files.RefreshAsync();
        }

        /// <summary>Keeps the title-bar button and its radio items in step with the surface.</summary>
        private void UpdateLayoutChrome()
        {
            int panes = _surface.PaneCount;
            bool split = panes > 1;

            // Panes nest, so there is no single orientation to report — the count is what matters.
            // The unsplit label matches its menu item word for word, so the button reads as the
            // menu it opens rather than as a shortened version of it.
            LayoutLabel.Text = split ? $"Split into {panes} panes" : "Single pane (merge all)";

            LayoutSingleItem.IsChecked = !split;
            LayoutSideItem.IsChecked = false;
            LayoutStackItem.IsChecked = false;

            bool canSpread = _surface.SessionCount > 1;
            LayoutSideItem.IsEnabled = canSpread;
            LayoutStackItem.IsEnabled = canSpread;
            LayoutSingleItem.IsEnabled = split;
            LayoutButton.IsEnabled = _surface.SessionCount > 0;
        }

        private void UpdateEmptyState()
        {
            bool hasSessions = _surface.SessionCount > 0;
            EmptyState.Visibility = hasSessions ? Visibility.Collapsed : Visibility.Visible;
            _surface.Visibility = hasSessions ? Visibility.Visible : Visibility.Collapsed;

            if (!hasSessions)
            {
                UpdateWindowTitle();
            }
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _imeTimer?.Stop();
            Files.ForgetAll();
            _surface.ShutdownAll();
        }
    }
}
