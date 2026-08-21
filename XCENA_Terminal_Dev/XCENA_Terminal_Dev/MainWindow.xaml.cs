using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
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
        Serial,
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
        private readonly SerialProfileStore _serialProfileStore = new();
        private readonly SessionSurface _surface = new();
        private readonly IntPtr _windowHandle;

        /// <summary>Width of the icon rail, which is all that is left when the panel is collapsed.</summary>
        private const double RailWidth = 34;

        /// <summary>How far the pointer must travel on the rail before it counts as a dock drag.</summary>
        private const double DockDragThreshold = 6;

        /// <summary>How often the host is pinged for the status bar. Latency is not worth more.</summary>
        private const long PingIntervalMilliseconds = 4000;

        /// <summary>Round trip under this is a green lamp; over the second one it is red.</summary>
        private const long GoodLatencyMilliseconds = 60;

        private const long PoorLatencyMilliseconds = 200;

        private readonly Stopwatch _statusClock = Stopwatch.StartNew();

        private TerminalView? _trafficView;
        private (long At, long Received, long Sent)? _trafficMark;
        private string _lastAutoApproved = string.Empty;
        private NetworkHealth _networkHealth = NetworkHealth.None;
        private string _networkTooltip = string.Empty;
        private string? _pingHost;
        private long _pingMilliseconds = -1;
        private long _pingTakenAt = -PingIntervalMilliseconds;
        private bool _pingInFlight;

        private SidebarSplitter? _sidebarSplitter;
        private Border? _dockHint;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _imeTimer;
        private ImeMode _imeMode = ImeMode.Unavailable;

        private SidebarTab _sidebarTab = SidebarTab.Sessions;
        private Point _dockDragStart;
        private bool _dockDragPending;
        private bool _dockDragging;
        private bool _dockDragHandled;
        private bool _dockHintOnRight;

        private bool _sidebarVisible = true;
        private bool _dialogOpen;

        public MainWindow()
        {
            _profileStore.Load();
            _recentStore.Load();
            _appearanceStore.Load();
            _layoutStore.Load();
            _highlightStore.Load();
            _serialProfileStore.Load();

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

            Serial.BindPinned(_serialProfileStore.Profiles);
            Serial.Initialize(_layoutStore.Current.Serial);
            Serial.OpenRequested += (_, settings) => _ = OpenSerialAsync(settings);
            Serial.SettingsChanged += (_, settings) =>
            {
                _layoutStore.Current.Serial = settings;
                _layoutStore.Save();
            };
            Serial.PinRequested += (_, settings) => _ = PinSerialAsync(settings);
            Serial.RenameRequested += (_, profile) => _ = RenameSerialAsync(profile);
            Serial.UnpinRequested += (_, profile) => _serialProfileStore.Remove(profile);
            // Nothing to arm at startup: the AI auto-answer is per session and starts off.
            _surface.AutoApproved += OnSurfaceAutoApproved;
            _surface.LogRequested += (_, view) => _ = ToggleSessionLogAsync(view);

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
                AppTitleText.Text = $"Cannot read profiles: {_profileStore.LoadError}";
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

            // The press is watched on the rail, the movement on the whole window: a quick flick
            // leaves the rail before the second pointer event arrives, and the rail alone would
            // never see it. handledEventsToo because the rail icons handle their own presses, and
            // a drag has to be able to start on one of them.
            SidebarRail.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(OnRailPointerPressed), handledEventsToo: true);
            RootGrid.AddHandler(UIElement.PointerMovedEvent,
                new PointerEventHandler(OnRootPointerMoved), handledEventsToo: true);
            RootGrid.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler(OnRootPointerReleased), handledEventsToo: true);

            // The pointer can end up over the terminal, which is another process and swallows it.
            // Losing capture mid-drag is therefore a normal ending: dock where the hint last was.
            RootGrid.PointerCaptureLost += (_, _) =>
            {
                if (_dockDragging)
                {
                    EndDockDrag(dock: true, onRight: _dockHintOnRight);
                }
            };

            ApplySidebarLayout();
        }

        // ---------- dragging the panel to the other side ----------

        private void OnRailPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _dockDragStart = e.GetCurrentPoint(RootGrid).Position;
            _dockDragPending = true;
            _dockDragging = false;
        }

        private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dockDragPending && !_dockDragging)
            {
                return;
            }

            Point here = e.GetCurrentPoint(RootGrid).Position;

            if (!_dockDragging)
            {
                if (Math.Abs(here.X - _dockDragStart.X) < DockDragThreshold
                    && Math.Abs(here.Y - _dockDragStart.Y) < DockDragThreshold)
                {
                    return;
                }

                // Past the threshold this is a drag, not a click: take the pointer so the moves
                // keep coming wherever it goes.
                _dockDragging = RootGrid.CapturePointer(e.Pointer);
                _dockDragPending = false;

                if (!_dockDragging)
                {
                    return;
                }
            }

            ShowDockHint(here.X > RootGrid.ActualWidth / 2);
        }

        private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dockDragging)
            {
                _dockDragPending = false;
                return;
            }

            bool onRight = e.GetCurrentPoint(RootGrid).Position.X > RootGrid.ActualWidth / 2;

            _dockDragging = false;
            RootGrid.ReleasePointerCapture(e.Pointer);
            EndDockDrag(dock: true, onRight);
        }

        private void EndDockDrag(bool dock, bool onRight)
        {
            _dockDragPending = false;
            _dockDragging = false;
            HideDockHint();

            if (!dock)
            {
                return;
            }

            // The icon under the pointer would otherwise also fire its Click and toggle the panel.
            _dockDragHandled = true;
            DockSidebar(onRight);
        }

        /// <summary>Half-window preview of where the panel would land.</summary>
        private void ShowDockHint(bool onRight)
        {
            _dockHint ??= new Border
            {
                Background = AppAccent.DropFillBrush(),
                BorderBrush = AppAccent.Brush(),
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false,
                Width = 220,
            };

            if (!RootGrid.Children.Contains(_dockHint))
            {
                Grid.SetRow(_dockHint, 1);
                RootGrid.Children.Add(_dockHint);
            }

            _dockHintOnRight = onRight;
            _dockHint.HorizontalAlignment = onRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _dockHint.VerticalAlignment = VerticalAlignment.Stretch;
            _dockHint.Visibility = Visibility.Visible;
        }

        private void HideDockHint()
        {
            if (_dockHint is not null)
            {
                _dockHint.Visibility = Visibility.Collapsed;
                RootGrid.Children.Remove(_dockHint);
            }
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
            Thickness margin = onRight ? new Thickness(4, 6, 8, 8) : new Thickness(8, 6, 4, 8);
            Sidebar.Margin = margin;

            // The rail belongs on the window edge: left of the cards when docked left, right of
            // them when docked right. The Auto column travels with it.
            Grid.SetColumn(SidebarRail, onRight ? 1 : 0);
            Grid.SetColumn(SidebarCards, onRight ? 0 : 1);
            SidebarInnerLeft.Width = onRight ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            SidebarInnerRight.Width = onRight ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

            // The selected-tab bar sits on the outer edge, so it flips with the rail.
            HorizontalAlignment markerEdge = onRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            SessionsTabMarker.HorizontalAlignment = markerEdge;
            FilesTabMarker.HorizontalAlignment = markerEdge;
            ToolsTabMarker.HorizontalAlignment = markerEdge;

            // Collapsing hides the cards but keeps the icon rail: clicking an icon is how the
            // panel comes back, so the way in must stay on screen.
            SidebarCards.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;

            if (_sidebarSplitter is not null)
            {
                _sidebarSplitter.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
            }

            double railWidth = RailWidth + margin.Left + margin.Right;

            SplitterColumn.Width = new GridLength(_sidebarVisible ? SidebarSplitter.Thickness : 0);
            SidebarColumn.Width = new GridLength(_sidebarVisible ? width : railWidth);
            SessionColumn.Width = new GridLength(1, GridUnitType.Star);
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

        /// <summary>
        /// Opens a serial console in a new tab. No profile and no secret: a port needs neither, so
        /// nothing about it is stored beyond the line settings the panel already remembers.
        /// </summary>
        private async Task OpenSerialAsync(SerialConnection settings)
        {
            SerialConnection snapshot = settings.Clone();

            // A port opens once. Catching our own tab here beats letting the platform answer with
            // "access denied", and it can offer the thing the user probably wanted: that tab.
            if (_surface.FindSerialSession(snapshot.PortName) is { } existing)
            {
                var already = new ContentDialog
                {
                    Title = $"{snapshot.PortName} is already open",
                    Content = $"This window already has {snapshot.PortName} open as "
                        + $"\"{existing.SessionLabel}\". A serial port can only be held by one session.",
                    PrimaryButtonText = "Go to it",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                };

                if (await ShowDialogAsync(already) == ContentDialogResult.Primary)
                {
                    _surface.Activate(existing);
                }

                return;
            }

            TerminalView view = _surface.AddSerialSession(snapshot);

            UpdateEmptyState();
            UpdateLayoutChrome();
            OnActiveSessionChanged();

            await _surface.StartSerialAsync(view, snapshot);
        }

        /// <summary>
        /// Pins the port and settings under a name. The port description is offered as the default,
        /// because "CP210x USB to UART" is a better first guess than "COM7" at what the board is.
        /// </summary>
        private async Task PinSerialAsync(SerialConnection settings)
        {
            string? name = await AskForNameAsync(
                "Pin this console",
                $"{settings.Summary}",
                settings.DisplayName);

            if (name is null)
            {
                return;
            }

            _serialProfileStore.Add(new SerialProfile
            {
                Name = name,
                Settings = settings.Clone(),
            });
        }

        private async Task RenameSerialAsync(SerialProfile profile)
        {
            string? name = await AskForNameAsync("Rename", profile.Detail, profile.DisplayName);
            if (name is not null)
            {
                _serialProfileStore.Rename(profile, name);
            }
        }

        /// <summary>One text box in a dialog: too small for its own file, too common to inline twice.</summary>
        private async Task<string?> AskForNameAsync(string title, string detail, string suggested)
        {
            var box = new TextBox { Text = suggested, SelectionStart = suggested.Length };

            var body = new StackPanel { Spacing = 8, Width = 320 };
            body.Children.Add(new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            body.Children.Add(box);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = body,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return null;
            }

            string name = box.Text.Trim();
            return name.Length > 0 ? name : suggested;
        }

        /// <summary>
        /// Starts or stops recording a session. Stopping needs no dialog; starting asks where the
        /// file goes, with a name that already says which session and when.
        /// </summary>
        private async Task ToggleSessionLogAsync(TerminalView view)
        {
            if (view.LogPath is not null)
            {
                view.StopLogging();
                UpdateStatusLog();
                return;
            }

            string suggested = $"{Sanitise(view.SessionLabel)}-{DateTime.Now:yyyyMMdd-HHmm}";

            string? path;
            try
            {
                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = suggested,
                };

                picker.FileTypeChoices.Add("Log file", new List<string> { ".log" });
                picker.FileTypeChoices.Add("Text file", new List<string> { ".txt" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);

                StorageFile? file = await picker.PickSaveFileAsync();
                path = file?.Path;
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(new ContentDialog
                {
                    Title = "Log to file",
                    Content = $"Cannot open the save dialog: {ex.Message}",
                    CloseButtonText = "Close",
                });

                return;
            }

            if (path is null)
            {
                return;
            }

            try
            {
                view.StartLogging(path, view.SessionLabel);
            }
            catch (Exception ex)
            {
                await ShowDialogAsync(new ContentDialog
                {
                    Title = "Log to file",
                    Content = $"Cannot write to {path}: {ex.Message}",
                    CloseButtonText = "Close",
                });
            }

            UpdateStatusLog();
        }

        /// <summary>Keeps a session label usable as a file name.</summary>
        private static string Sanitise(string label)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string cleaned = new(label.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray());

            return cleaned.Trim('-').Length > 0 ? cleaned.Trim('-') : "session";
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
                UpdateStatusNetwork();
                UpdateStatusFont();
                UpdateStatusLog();
            };
            _imeTimer.Start();

            UpdateImeStatus();
            UpdateStatusSession();
            UpdateStatusFont();
            UpdateStatusAutoApprove();
        }

        /// <summary>
        /// Round trip to the host, and what the session is moving. Latency comes from ICMP, which
        /// plenty of hosts drop — a missing figure is normal, so it is simply left out rather than
        /// reported as an error. Throughput is counted on the shell stream itself, so it always works.
        /// </summary>
        private void UpdateStatusNetwork()
        {
            TerminalView? view = _surface.ActiveView;

            if (view is null || view.SessionLabel.Length == 0)
            {
                _trafficMark = null;
                ShowNetwork(NetworkHealth.None, string.Empty, "No session");
                return;
            }

            if (view.State != TerminalState.Connected)
            {
                _trafficMark = null;

                (NetworkHealth health, string label) = view.State switch
                {
                    TerminalState.Connecting => (NetworkHealth.Warn, "connecting"),
                    TerminalState.Reconnecting => (NetworkHealth.Warn, "reconnecting"),
                    TerminalState.Failed or TerminalState.Disconnected => (NetworkHealth.Bad, "offline"),
                    _ => (NetworkHealth.None, string.Empty),
                };

                ShowNetwork(health, string.Empty, label);
                return;
            }

            (long received, long sent) = view.Traffic;
            long elapsed = _statusClock.ElapsedMilliseconds;

            string rates = "↓ — ↑ —";
            if (_trafficMark is { } mark && ReferenceEquals(_trafficView, view) && elapsed > mark.At)
            {
                double seconds = (elapsed - mark.At) / 1000.0;
                rates = $"↓ {Rate((received - mark.Received) / seconds)}  ↑ {Rate((sent - mark.Sent) / seconds)}";
            }

            _trafficView = view;
            _trafficMark = (elapsed, received, sent);

            // A serial console has no host to ping: the lamp reports that the port is open, and
            // the throughput in the tooltip is the only traffic there is to see.
            if (view.Profile is not { } profile)
            {
                ShowNetwork(NetworkHealth.Good, string.Empty, $"{view.SessionLabel}\n{rates}");
                return;
            }

            // The ping is much slower than this tick, so it runs on its own interval and the last
            // answer is reused in between.
            StartPing(profile.Host, elapsed);

            bool haveLatency = _pingHost == profile.Host && _pingMilliseconds >= 0;

            // Green while the round trip is short, amber once it drags — and amber too when the
            // host drops ICMP, because "connected but unmeasured" is not the same as "good".
            NetworkHealth quality = !haveLatency
                ? NetworkHealth.Warn
                : _pingMilliseconds < GoodLatencyMilliseconds
                    ? NetworkHealth.Good
                    : _pingMilliseconds < PoorLatencyMilliseconds
                        ? NetworkHealth.Warn
                        : NetworkHealth.Bad;

            string figures = haveLatency ? $"{_pingMilliseconds} ms" : string.Empty;
            string tooltip = haveLatency
                ? $"{profile.Host} · {_pingMilliseconds} ms\n{rates}"
                : $"{profile.Host} · no ping reply\n{rates}";

            ShowNetwork(quality, figures, tooltip);
        }

        /// <summary>How the link is doing, as the lamp shows it.</summary>
        private enum NetworkHealth
        {
            None,
            Good,
            Warn,
            Bad,
        }

        private void ShowNetwork(NetworkHealth health, string figures, string tooltip)
        {
            if (StatusNetwork.Text != figures)
            {
                StatusNetwork.Text = figures;
            }

            if (_networkHealth != health)
            {
                _networkHealth = health;

                StatusNetworkLight.Visibility = health == NetworkHealth.None
                    ? Visibility.Collapsed
                    : Visibility.Visible;

                StatusNetworkLight.Fill = new SolidColorBrush(health switch
                {
                    NetworkHealth.Good => Windows.UI.Color.FromArgb(0xFF, 0x35, 0xC7, 0x59),
                    NetworkHealth.Warn => Windows.UI.Color.FromArgb(0xFF, 0xE8, 0xA5, 0x1C),
                    NetworkHealth.Bad => Windows.UI.Color.FromArgb(0xFF, 0xE0, 0x4F, 0x44),
                    _ => Microsoft.UI.Colors.Transparent,
                });
            }

            if (_networkTooltip != tooltip)
            {
                _networkTooltip = tooltip;
                ToolTipService.SetToolTip(StatusNetworkPanel, tooltip);
            }
        }

        /// <summary>One ICMP probe every few seconds, never more than one in flight.</summary>
        private void StartPing(string host, long now)
        {
            if (_pingInFlight || now - _pingTakenAt < PingIntervalMilliseconds)
            {
                return;
            }

            _pingInFlight = true;
            _pingTakenAt = now;

            _ = MeasureLatencyAsync(host);
        }

        private async Task MeasureLatencyAsync(string host)
        {
            long milliseconds = -1;

            try
            {
                using var ping = new Ping();
                PingReply reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(1)).ConfigureAwait(true);

                if (reply.Status == IPStatus.Success)
                {
                    milliseconds = reply.RoundtripTime;
                }
            }
            catch (Exception)
            {
                // No ICMP, no name resolution, no network. The readout just omits the latency.
            }
            finally
            {
                _pingHost = host;
                _pingMilliseconds = milliseconds;
                _pingInFlight = false;
            }
        }

        /// <summary>Bytes per second in the shortest form that stays readable.</summary>
        private static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond < 0 || double.IsNaN(bytesPerSecond))
            {
                bytesPerSecond = 0;
            }

            return bytesPerSecond switch
            {
                < 1024 => $"{bytesPerSecond:0} B/s",
                < 1024 * 1024 => $"{bytesPerSecond / 1024:0.#} kB/s",
                _ => $"{bytesPerSecond / (1024 * 1024):0.#} MB/s",
            };
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

        // ---------- AI CLI installers ----------

        /// <summary>
        /// Built on Opening: the entries run against the live session, so what they can do depends
        /// on whether one is connected.
        /// </summary>
        private void OnAiMenuOpening(object sender, object e)
        {
            AiMenu.Items.Clear();

            TerminalView? view = _surface.ActiveView;
            bool connected = view?.State == TerminalState.Connected;

            // Where the script would run. A serial console is a shell too, so the port name serves
            // as the destination just as an endpoint does.
            string host = view?.SessionLabel ?? string.Empty;

            foreach (AiTool tool in AiTool.All)
            {
                AiTool captured = tool;

                var item = new MenuFlyoutItem
                {
                    Text = $"Install {tool.Name}",
                    // The endpoint belongs on the item: this installs on the far end, not here.
                    KeyboardAcceleratorTextOverride = connected ? host : "no session",
                    IsEnabled = connected,
                };

                item.Click += (_, _) => _ = InstallAiToolAsync(captured);
                AiMenu.Items.Add(item);
            }

            AiMenu.Items.Add(new MenuFlyoutSeparator());

            foreach (AiTool tool in AiTool.All)
            {
                AiTool captured = tool;

                var copy = new MenuFlyoutItem { Text = $"Copy {tool.Name} command" };
                copy.Click += (_, _) => CopyText(captured.Script);
                AiMenu.Items.Add(copy);
            }

            AiMenu.Items.Add(new MenuFlyoutSeparator());

            // Says what it does in full: this answers a prompt whose whole purpose was to ask.
            // Per session, because arming it is a decision about the task in front of you.
            // Only an SSH host can be on the block list; a serial console has no host to name.
            bool blocked = view?.Profile is { } current && IsAutoApproveBlocked(current.Host);

            var auto = new ToggleMenuFlyoutItem
            {
                Text = "Answer \"Yes, proceed\" automatically — this session",
                IsChecked = view?.AutoApprove == true,
                IsEnabled = connected && !blocked,
                KeyboardAcceleratorTextOverride = blocked ? "blocked on this host" : string.Empty,
            };
            auto.Click += OnAutoApproveClick;
            AiMenu.Items.Add(auto);

            if (view?.Profile is { } target)
            {
                var block = new ToggleMenuFlyoutItem
                {
                    Text = $"Never auto-answer on {target.Host}",
                    IsChecked = IsAutoApproveBlocked(target.Host),
                };
                block.Click += (_, _) => ToggleAutoApproveBlock(target.Host);
                AiMenu.Items.Add(block);
            }
        }

        private bool IsAutoApproveBlocked(string host) =>
            _layoutStore.Current.AutoApproveBlockedHosts
                .Any(entry => string.Equals(entry, host, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Blocking a host is remembered; it also disarms whatever is already running, because a
        /// rule that only applies to future sessions would not be a block.
        /// </summary>
        private void ToggleAutoApproveBlock(string host)
        {
            List<string> blocked = _layoutStore.Current.AutoApproveBlockedHosts;

            if (IsAutoApproveBlocked(host))
            {
                blocked.RemoveAll(entry => string.Equals(entry, host, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                blocked.Add(host);
                _surface.DisarmAutoApprove();
            }

            _layoutStore.Save();
            UpdateStatusAutoApprove();
        }

        /// <summary>
        /// Turns the auto-answer on or off. Only the plain "Yes" is ever chosen — never the
        /// "and do not ask again" variant, which would surrender every later prompt as well.
        /// </summary>
        private void OnAutoApproveClick(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleMenuFlyoutItem item || _surface.ActiveView is not { } view)
            {
                return;
            }

            // A blocked host wins over the switch, whichever way it was just flipped.
            bool arm = item.IsChecked
                && view.Profile is { } profile
                && !IsAutoApproveBlocked(profile.Host);

            view.ApplyAutoApprove(arm);
            _lastAutoApproved = string.Empty;
            UpdateStatusAutoApprove();
        }

        /// <summary>
        /// Records what was answered, so the status bar can name the last prompt taken rather than
        /// only that the automation is armed.
        /// </summary>
        private void OnSurfaceAutoApproved(object? sender, string option)
        {
            int colon = option.IndexOf(':');
            _lastAutoApproved = colon >= 0 && colon + 1 < option.Length
                ? option[(colon + 1)..].Trim()
                : option.Trim();

            UpdateStatusAutoApprove();
        }

        /// <summary>
        /// The status bar carries the state of the session in front of you: an automation this
        /// quiet has to be visible, and it is now per session, so the readout follows the tab.
        /// </summary>
        /// <summary>
        /// A session being recorded says so, with the file name: a log running unnoticed is how you
        /// end up with a console transcript you did not know you were keeping.
        /// </summary>
        private void UpdateStatusLog()
        {
            string? path = _surface.ActiveView?.LogPath;

            StatusLog.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;

            string text = path is null ? string.Empty : $"log → {Path.GetFileName(path)}";
            if (StatusLog.Text != text)
            {
                StatusLog.Text = text;
            }
        }

        private void UpdateStatusAutoApprove()
        {
            bool on = _surface.ActiveView?.AutoApprove == true;

            StatusAutoApprove.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            StatusAutoApprove.Text = on && _lastAutoApproved.Length > 0
                ? $"auto-yes · {_lastAutoApproved}"
                : "auto-yes";
        }

        /// <summary>
        /// Confirms first: this types into a live shell on someone else's machine, and the command
        /// is worth reading before it runs.
        /// </summary>
        private async Task InstallAiToolAsync(AiTool tool)
        {
            TerminalView? view = _surface.ActiveView;
            if (view?.State != TerminalState.Connected || view.SessionLabel.Length == 0)
            {
                return;
            }

            // Say where it lands: a host and account for SSH, the port for a serial console.
            string where = view.Profile is { } profile
                ? $"Runs on {profile.Endpoint} as {profile.Username}:"
                : $"Runs on whatever is attached to {view.SessionLabel}:";

            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(new TextBlock
            {
                Text = where,
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(new TextBlock
            {
                Text = tool.Script,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

            var confirm = new ContentDialog
            {
                Title = $"Install {tool.Name}",
                Content = body,
                PrimaryButtonText = "Run",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await ShowDialogAsync(confirm) != ContentDialogResult.Primary)
            {
                return;
            }

            // Re-check: the dialog was open for a while and the session may have dropped.
            if (_surface.ActiveView is { State: TerminalState.Connected } target)
            {
                target.SendInput(tool.Script + "\n");
            }
        }

        private static void CopyText(string text)
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
        }

        /// <summary>
        /// Called from the same tick as the IME poll rather than wired to a dozen events; the text
        /// is only assigned when it actually changes.
        /// </summary>
        private void UpdateStatusSession()
        {
            TerminalView? view = _surface.ActiveView;

            string text;
            if (view is null || view.SessionLabel.Length == 0)
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

                text = platform.Length > 0
                    ? $"{view.SessionLabel}  ·  {platform}"
                    : view.SessionLabel;
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

        private void OnSessionsTabClick(object sender, RoutedEventArgs e) => ClickSidebarTab(SidebarTab.Sessions);

        private void OnFilesTabClick(object sender, RoutedEventArgs e) => ClickSidebarTab(SidebarTab.Files);

        private void OnSerialTabClick(object sender, RoutedEventArgs e) => ClickSidebarTab(SidebarTab.Serial);

        private void OnToolsTabClick(object sender, RoutedEventArgs e) => ClickSidebarTab(SidebarTab.Tools);

        /// <summary>
        /// The rail icons are the panel switch. A different tab shows it, the tab already showing
        /// hides it — so one icon both opens and closes, and there is no separate panel menu.
        /// </summary>
        private void ClickSidebarTab(SidebarTab tab)
        {
            // A drag that ended on an icon docked the panel; it must not also toggle it.
            if (_dockDragHandled)
            {
                _dockDragHandled = false;
                return;
            }

            if (_sidebarVisible && tab == _sidebarTab)
            {
                _sidebarVisible = false;
                ApplySidebarLayout();
                return;
            }

            if (!_sidebarVisible)
            {
                _sidebarVisible = true;
                ApplySidebarLayout();
            }

            SelectSidebarTab(tab);
        }

        private void SelectSidebarTab(SidebarTab tab)
        {
            _sidebarTab = tab;

            // "New connection" lives inside the profile card, so hiding the card hides it too.
            ProfileCard.Visibility = Show(tab == SidebarTab.Sessions);
            FilesCard.Visibility = Show(tab == SidebarTab.Files);
            SerialCard.Visibility = Show(tab == SidebarTab.Serial);
            ToolsCard.Visibility = Show(tab == SidebarTab.Tools);

            var accent = AppAccent.Brush();
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var dim = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            var bright = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

            SessionsTabMarker.Background = tab == SidebarTab.Sessions ? accent : clear;
            FilesTabMarker.Background = tab == SidebarTab.Files ? accent : clear;
            SerialTabMarker.Background = tab == SidebarTab.Serial ? accent : clear;
            ToolsTabMarker.Background = tab == SidebarTab.Tools ? accent : clear;

            SessionsTabIcon.Foreground = tab == SidebarTab.Sessions ? bright : dim;
            FilesTabIcon.Foreground = tab == SidebarTab.Files ? bright : dim;
            SerialTabIcon.Foreground = tab == SidebarTab.Serial ? bright : dim;
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

        /// <summary>
        /// The centre of the title bar carries whatever the active session calls itself — the
        /// remote title if the shell set one (OSC 0/2), otherwise the endpoint. Nothing when there
        /// is no session: an app name in its own title bar tells the user nothing.
        /// </summary>
        private void UpdateWindowTitle()
        {
            TerminalView? view = _surface.ActiveView;
            AppTitleText.Text = view is null
                ? string.Empty
                : view.RemoteTitle ?? view.SessionLabel;
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
