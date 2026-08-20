using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Renci.SshNet.Common;
using Windows.ApplicationModel.DataTransfer;
using XCENA_Terminal_Dev.Models;
using XCENA_Terminal_Dev.Services;

namespace XCENA_Terminal_Dev.Controls
{
    public enum TerminalState
    {
        Idle,
        Connecting,
        Connected,

        /// <summary>Link dropped; an automatic retry is counting down.</summary>
        Reconnecting,

        Disconnected,
        Failed,
    }

    /// <summary>
    /// Window-level actions requested from inside the terminal. The WebView holds keyboard focus,
    /// so XAML accelerators cannot see these chords; the page forwards them instead.
    /// </summary>
    public enum TerminalCommand
    {
        NewTab,
        ToggleSidebar,

        /// <summary>Show every open session left to right (or return to single view).</summary>
        TileSideBySide,

        /// <summary>Show every open session top to bottom (or return to single view).</summary>
        TileStacked,

        /// <summary>Close this session and its tab.</summary>
        CloseSession,

        /// <summary>The user clicked into this pane, so it should become the active one.</summary>
        PaneClicked,

        /// <summary>Ctrl+Tab — activate the next session.</summary>
        NextSession,

        /// <summary>Ctrl+Shift+Tab — activate the previous session.</summary>
        PreviousSession,
    }

    /// <summary>
    /// Hosts xterm.js inside a WebView2 and pumps bytes between it and an <see cref="SshSession"/>.
    /// The WebView owns all VT parsing and rendering; this class is only the bridge.
    /// </summary>
    public sealed partial class TerminalView : UserControl
    {
        private const string VirtualHost = "xcena-terminal.invalid";
        private const string PageUrl = "https://" + VirtualHost + "/terminal.html";

        /// <summary>Seconds between automatic reconnect attempts after the link drops.</summary>
        private const int ReconnectDelaySeconds = 5;

        private readonly DispatcherQueue _dispatcher;
        private readonly object _outputGate = new();
        private readonly List<byte[]> _pendingOutput = new();

        private TaskCompletionSource<bool>? _readySource;
        private SshSession? _session;
        private ConnectionProfile? _profile;
        private string? _secret;
        private CancellationTokenSource? _connectCts;

        private TerminalAppearance? _appearance;
        private List<HighlightRule>? _highlights;
        private DispatcherQueueTimer? _retryTimer;
        private int _retrySecondsLeft;
        private int _retryAttempt;
        private string _retryReason = string.Empty;
        private bool _autoReconnect = true;
        private bool _shuttingDown;

        private bool _coreConfigured;
        private bool _webViewReady;
        private bool _flushQueued;
        private uint _columns = 80;
        private uint _rows = 24;
        private int _fontSize = 14;
        private bool _copyOnSelect = true;
        private bool _autoApprove;

        public TerminalView()
        {
            InitializeComponent();
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            Web.DefaultBackgroundColor = Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0C, 0x0C);

            // Do NOT tear down on Unloaded: TabView drops the unselected tab's content out of the
            // visual tree, so every tab switch would kill the live session. Shutdown() is driven
            // explicitly by MainWindow when a tab or the window closes.
            Loaded += OnLoaded;
        }

        /// <summary>Re-fits and refocuses after the tab is brought back into the visual tree.</summary>
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_webViewReady)
            {
                return;
            }

            Post("z");
            FocusTerminal();
        }

        /// <summary>Raised when the remote shell reports a new title (OSC 0/2).</summary>
        public event EventHandler<string>? TitleChanged;

        /// <summary>Raised whenever <see cref="State"/> changes.</summary>
        public event EventHandler<TerminalState>? StateChanged;

        /// <summary>Raised once the remote platform is known, so the tab can show its icon.</summary>
        public event EventHandler<RemotePlatform>? PlatformDetected;

        /// <summary>Raised when an AI CLI prompt was answered automatically. Carries the option taken.</summary>
        public event EventHandler<string>? AutoApproved;

        /// <summary>Raised when the user presses a window-level chord inside the terminal.</summary>
        public event EventHandler<TerminalCommand>? CommandRequested;

        public TerminalState State { get; private set; } = TerminalState.Idle;

        /// <summary>What the far end runs, once known. <see cref="RemotePlatform.Unknown"/> until then.</summary>
        public RemotePlatform Platform { get; private set; } = RemotePlatform.Unknown;

        /// <summary>Shell bytes moved on this session so far, for the status bar throughput readout.</summary>
        public (long Received, long Sent) Traffic =>
            _session is { } session ? (session.BytesReceived, session.BytesSent) : (0, 0);

        public ConnectionProfile? Profile => _profile;

        /// <summary>Last title reported by the remote shell via OSC 0/2, if any.</summary>
        public string? RemoteTitle { get; private set; }

        /// <summary>
        /// Profile and in-memory secret of this pane, so a split can open a second shell on the
        /// same host without prompting again. Null before the first connect attempt.
        /// </summary>
        internal (ConnectionProfile Profile, string? Secret)? Connection =>
            _profile is null ? null : (_profile, _secret);

        /// <summary>
        /// Boots the WebView (if needed) and opens the SSH session. Failures are shown in the
        /// overlay rather than thrown, so a bad profile cannot tear down the tab.
        /// </summary>
        public async Task ConnectAsync(ConnectionProfile profile, string? secret)
        {
            _profile = profile;
            _secret = secret;

            StopRetryCountdown();
            SetState(TerminalState.Connecting);
            ShowBusy(_retryAttempt > 0 ? $"Reconnecting… (attempt {_retryAttempt})" : "Connecting…", profile.Endpoint);

            try
            {
                await EnsureWebViewAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ShowError("Cannot start the terminal", DescribeWebViewFailure(ex), canRetry: true);
                SetState(TerminalState.Failed);
                return;
            }

            var session = new SshSession();
            session.OutputReceived += OnSessionOutput;
            session.Closed += OnSessionClosed;
            session.Notice += OnSessionNotice;
            _session = session;

            _connectCts?.Dispose();
            _connectCts = new CancellationTokenSource();

            try
            {
                await session.ConnectAsync(profile, secret, _columns, _rows, _connectCts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                DetachSession();
                SetState(TerminalState.Idle);
                return;
            }
            catch (Exception ex)
            {
                DetachSession();

                // A server that is still rebooting refuses connections; keep trying. Bad credentials
                // or a changed host key never fix themselves, so those stop here.
                if (_autoReconnect && IsRetryable(ex))
                {
                    ScheduleReconnect("Connection failed", ex.Message);
                }
                else
                {
                    ShowError("Connection failed", ex.Message, canRetry: true);
                    SetState(TerminalState.Failed);
                }

                return;
            }

            if (_retryAttempt > 0)
            {
                PostNotice($"[Reconnected to {profile.Endpoint} after {_retryAttempt} attempt(s)]\n");
            }

            _retryAttempt = 0;
            HideOverlay();
            SetState(TerminalState.Connected);

            // The PTY was created with the pre-layout grid size; re-assert the real one.
            session.Resize(_columns, _rows);
            FocusTerminal();

            // Deliberately not awaited: the probe runs on its own channel and only decides an icon,
            // so the session must not wait on it.
            _ = IdentifyPlatformAsync(session);
        }

        /// <summary>
        /// Asks the session what it is talking to and publishes the answer. Silent on failure —
        /// the tab simply keeps the generic icon.
        /// </summary>
        private async Task IdentifyPlatformAsync(SshSession session)
        {
            CancellationToken token = _connectCts?.Token ?? CancellationToken.None;

            RemotePlatform platform;
            try
            {
                platform = await session.DetectPlatformAsync(token).ConfigureAwait(true);
            }
            catch (Exception)
            {
                return;
            }

            // A reconnect or a close may have replaced the session while the probe was in flight.
            if (!ReferenceEquals(_session, session) || !platform.IsKnown)
            {
                return;
            }

            Platform = platform;
            PlatformDetected?.Invoke(this, platform);
        }

        public void Disconnect()
        {
            _autoReconnect = false;
            StopRetryCountdown();
            _connectCts?.Cancel();
            DetachSession();
        }

        public void FocusTerminal()
        {
            if (_webViewReady)
            {
                Post("f");
            }

            Web.Focus(FocusState.Programmatic);
        }

        public void ClearScreen() => Post("x");

        /// <summary>Types text into the shell, as if the user had entered it at the prompt.</summary>
        public void SendInput(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            _session?.SendText(text);
            FocusTerminal();
        }

        /// <summary>
        /// Applies user colours to this terminal. Remembered so a page that has not finished
        /// loading yet still gets them once it reports ready.
        /// </summary>
        public void ApplyAppearance(TerminalAppearance appearance)
        {
            _appearance = appearance.Clone();

            Windows.UI.Color background = _appearance.BackgroundColor;
            Web.DefaultBackgroundColor = background;
            TerminalBackground.Color = background;

            PostAppearance();
        }

        /// <summary>
        /// Replaces the "colour this text" rules. Matching happens in the page, so nothing is
        /// injected into the shell's byte stream and a TUI's own colours are left alone.
        /// </summary>
        public void ApplyHighlights(IReadOnlyList<HighlightRule> rules)
        {
            _highlights = rules.Where(rule => rule.IsUsable).Select(rule => rule.Clone()).ToList();
            PostHighlights();
        }

        private void PostHighlights()
        {
            if (_highlights is null || !_webViewReady)
            {
                return;
            }

            // Only the fields the page needs; Enabled has already been applied by filtering.
            string json = JsonSerializer.Serialize(_highlights.Select(rule => new Dictionary<string, object>
            {
                ["pattern"] = rule.Pattern,
                ["textOnly"] = rule.TextOnly,
                ["ignoreCase"] = rule.IgnoreCase,
                ["color"] = rule.EffectiveColor,
            }));

            Post("g" + json);
        }

        private void PostAppearance()
        {
            if (_appearance is null || !_webViewReady)
            {
                return;
            }

            var theme = new Dictionary<string, string>
            {
                ["background"] = _appearance.Background,
                ["foreground"] = _appearance.Foreground,
                ["cursor"] = _appearance.Cursor,
                // The block cursor draws the glyph under it in this colour, so it follows the
                // background or the character vanishes on a light scheme.
                ["cursorAccent"] = _appearance.Background,
                ["selectionBackground"] = _appearance.Selection,
            };

            // The sixteen ANSI slots the shell paints with; the page merges whatever it is sent.
            IReadOnlyList<string> ansi = _appearance.AnsiColors;
            for (int i = 0; i < TerminalScheme.AnsiNames.Length && i < ansi.Count; i++)
            {
                theme[TerminalScheme.AnsiNames[i]] = ansi[i];
            }

            Post("h" + JsonSerializer.Serialize(theme));

            // Empty means "choose one that lines Hangul up with two cells"; the page decides,
            // because only it can measure what is actually installed.
            Post("y" + _appearance.FontFamily);

            // Ctrl+/- still adjusts this session afterwards; the stored size is the starting point.
            _fontSize = _appearance.SafeFontSize;
            Post("s" + _fontSize.ToString());
        }

        /// <summary>Whether a mouse selection goes straight to the clipboard.</summary>
        public void ApplyCopyOnSelect(bool enabled)
        {
            _copyOnSelect = enabled;
            PostCopyOnSelect();
        }

        private void PostCopyOnSelect()
        {
            if (_webViewReady)
            {
                Post(_copyOnSelect ? "w1" : "w0");
            }
        }

        /// <summary>Whether an AI CLI approval prompt is answered "Yes" without asking the user.</summary>
        public void ApplyAutoApprove(bool enabled)
        {
            _autoApprove = enabled;
            PostAutoApprove();
        }

        private void PostAutoApprove()
        {
            if (_webViewReady)
            {
                Post(_autoApprove ? "a1" : "a0");
            }
        }

        public void ChangeFontSize(int delta)
        {
            int next = Math.Clamp(_fontSize + delta, 8, 28);
            if (next == _fontSize)
            {
                return;
            }

            _fontSize = next;
            Post("s" + _fontSize.ToString());
        }

        public void ResetFontSize()
        {
            _fontSize = 14;
            Post("s14");
        }

        private async Task EnsureWebViewAsync()
        {
            if (_webViewReady)
            {
                return;
            }

            _readySource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await Web.EnsureCoreWebView2Async();

            CoreWebView2 core = Web.CoreWebView2;

            // A retry after a ready-handshake timeout re-enters this method, so everything below
            // the guard must only ever be wired up once per CoreWebView2 instance.
            if (!_coreConfigured)
            {
                CoreWebView2Settings settings = core.Settings;
                settings.AreDefaultContextMenusEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                settings.IsGeneralAutofillEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;
                settings.IsSwipeNavigationEnabled = false;
                settings.IsPinchZoomEnabled = false;
#if DEBUG
                settings.AreDevToolsEnabled = true;
#else
                settings.AreDevToolsEnabled = false;
#endif

                string assetFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "xterm");
                if (!File.Exists(Path.Combine(assetFolder, "terminal.html")))
                {
                    throw new FileNotFoundException($"Terminal assets not found: {assetFolder}");
                }

                core.SetVirtualHostNameToFolderMapping(
                    VirtualHost, assetFolder, CoreWebView2HostResourceAccessKind.Allow);

                core.WebMessageReceived += OnWebMessageReceived;
                _coreConfigured = true;
            }

            core.Navigate(PageUrl);

            // The page posts "ready" once xterm has been laid out and reported its grid size.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using (timeout.Token.Register(
                () => _readySource?.TrySetException(new TimeoutException("The terminal page took too long to load."))))
            {
                await _readySource.Task.ConfigureAwait(true);
            }

            _webViewReady = true;
            PostAppearance();
            PostHighlights();
            PostCopyOnSelect();
            PostAutoApprove();
        }

        private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string message;
            try
            {
                message = args.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return;
            }

            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            if (message == "ready")
            {
                _readySource?.TrySetResult(true);
                return;
            }

            char tag = message[0];
            string body = message.Substring(1);

            switch (tag)
            {
                case 'i': // keyboard / paste input, base64 UTF-8
                    if (TryDecodeBase64(body, out byte[] input))
                    {
                        _session?.Send(input);
                    }

                    break;

                case 'r': // grid size, "cols,rows"
                    ApplyResize(body);
                    break;

                case 't': // OSC title
                    if (TryDecodeBase64(body, out byte[] titleBytes))
                    {
                        string title = Encoding.UTF8.GetString(titleBytes);
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            RemoteTitle = title;
                            TitleChanged?.Invoke(this, title);
                        }
                    }

                    break;

                case 'c': // copy request
                    if (TryDecodeBase64(body, out byte[] selectionBytes))
                    {
                        CopyToClipboard(Encoding.UTF8.GetString(selectionBytes));
                    }

                    break;

                case 'v': // paste request
                    _ = PasteFromClipboardAsync();
                    break;

                case 'n': // an AI prompt was answered on our behalf; the shell shows the rest
                    AutoApproved?.Invoke(this, body);
                    break;

                case 'k': // window-level chord
                    RaiseCommand(body);
                    break;
            }
        }

        private void RaiseCommand(string name)
        {
            TerminalCommand? command = name switch
            {
                "newtab" => TerminalCommand.NewTab,
                "sidebar" => TerminalCommand.ToggleSidebar,
                "tileside" => TerminalCommand.TileSideBySide,
                "tilestack" => TerminalCommand.TileStacked,
                "closesession" => TerminalCommand.CloseSession,
                "focus" => TerminalCommand.PaneClicked,
                "nexttab" => TerminalCommand.NextSession,
                "prevtab" => TerminalCommand.PreviousSession,
                _ => null,
            };

            if (command is not null)
            {
                CommandRequested?.Invoke(this, command.Value);
            }
        }

        private void ApplyResize(string body)
        {
            int comma = body.IndexOf(',');
            if (comma <= 0)
            {
                return;
            }

            if (!uint.TryParse(body.AsSpan(0, comma), out uint cols) ||
                !uint.TryParse(body.AsSpan(comma + 1), out uint rows) ||
                cols == 0 || rows == 0)
            {
                return;
            }

            if (cols == _columns && rows == _rows)
            {
                return;
            }

            _columns = cols;
            _rows = rows;
            _session?.Resize(cols, rows);
        }

        private static void CopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
        }

        private async Task PasteFromClipboardAsync()
        {
            string? text = null;
            try
            {
                DataPackageView view = Clipboard.GetContent();
                if (view.Contains(StandardDataFormats.Text))
                {
                    text = await view.GetTextAsync();
                }
            }
            catch (Exception)
            {
                // Another process may hold the clipboard open; silently skip the paste.
                return;
            }

            if (string.IsNullOrEmpty(text) || !_webViewReady)
            {
                return;
            }

            // Hand it to xterm so bracketed-paste mode is honoured.
            Post("p" + text.Replace("\r\n", "\r"));
        }

        private void OnSessionOutput(object? sender, byte[] chunk)
        {
            lock (_outputGate)
            {
                _pendingOutput.Add(chunk);
                if (_flushQueued)
                {
                    return;
                }

                _flushQueued = true;
            }

            if (!_dispatcher.TryEnqueue(FlushOutput))
            {
                lock (_outputGate)
                {
                    _flushQueued = false;
                }
            }
        }

        /// <summary>
        /// Merges everything buffered since the last UI tick into one message. Without this,
        /// bulk output (a large <c>cat</c>) would post thousands of tiny web messages.
        /// </summary>
        private void FlushOutput()
        {
            byte[][] chunks;
            int total = 0;

            lock (_outputGate)
            {
                chunks = _pendingOutput.ToArray();
                _pendingOutput.Clear();
                _flushQueued = false;
            }

            if (chunks.Length == 0)
            {
                return;
            }

            foreach (byte[] chunk in chunks)
            {
                total += chunk.Length;
            }

            byte[] merged = new byte[total];
            int offset = 0;
            foreach (byte[] chunk in chunks)
            {
                Buffer.BlockCopy(chunk, 0, merged, offset, chunk.Length);
                offset += chunk.Length;
            }

            Post("o" + Convert.ToBase64String(merged));
        }

        private const string DimStart = "\u001b[90m";
        private const string DimEnd = "\u001b[0m";


        private void OnSessionNotice(object? sender, string notice)
        {
            _dispatcher.TryEnqueue(() => PostNotice(notice));
        }

        private void OnSessionClosed(object? sender, string? reason)
        {
            _dispatcher.TryEnqueue(() =>
            {
                if (_shuttingDown || State is TerminalState.Disconnected or TerminalState.Failed
                    or TerminalState.Reconnecting)
                {
                    return;
                }

                FlushOutput();

                // A null reason means a clean EOF — the user typed `exit` or the shell ended on its
                // own. Reconnecting then would fight the user, so only errors trigger a retry.
                if (reason is not null && _autoReconnect)
                {
                    ScheduleReconnect("Disconnected", reason);
                    return;
                }

                SetState(TerminalState.Disconnected);
                ShowError("Session ended", reason ?? "The session has ended.", canRetry: true);
            });
        }

        // ---------- automatic reconnect ----------

        /// <summary>
        /// Failures that a retry could plausibly fix. Authentication and host key problems are
        /// excluded: hammering a server with bad credentials achieves nothing and can trip
        /// lockout or fail2ban, and a changed host key must be looked at by a human.
        /// </summary>
        private static bool IsRetryable(Exception ex) => ex is not (
            HostKeyMismatchException or
            SshAuthenticationException or
            SshPassPhraseNullOrEmptyException or
            FileNotFoundException or
            ArgumentException);

        private void ScheduleReconnect(string title, string reason)
        {
            _retryAttempt++;
            _retryReason = reason;
            _retrySecondsLeft = ReconnectDelaySeconds;

            SetState(TerminalState.Reconnecting);

            if (_retryAttempt == 1)
            {
                PostNotice($"\n[{title}: {reason}]\n");
            }

            ShowCountdown(title);

            _retryTimer ??= CreateRetryTimer();
            _retryTimer.Start();
        }

        private DispatcherQueueTimer CreateRetryTimer()
        {
            DispatcherQueueTimer timer = _dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.Tick += OnRetryTick;
            return timer;
        }

        private void OnRetryTick(DispatcherQueueTimer sender, object args)
        {
            _retrySecondsLeft--;
            if (_retrySecondsLeft > 0)
            {
                ShowCountdown("Disconnected");
                return;
            }

            sender.Stop();
            _ = ReconnectAsync();
        }

        private async Task ReconnectAsync()
        {
            if (_profile is null || _shuttingDown)
            {
                return;
            }

            DetachSession();
            await ConnectAsync(_profile, _secret);
        }

        private void StopRetryCountdown()
        {
            _retryTimer?.Stop();
        }

        /// <summary>Cancels auto-reconnect for this pane; the user can still retry by hand.</summary>
        private void OnStopRetryClick(object sender, RoutedEventArgs e)
        {
            _autoReconnect = false;
            StopRetryCountdown();
            SetState(TerminalState.Disconnected);
            ShowError("Session ended", _retryReason.Length == 0 ? "The session has ended." : _retryReason, canRetry: true);
        }

        private void ShowCountdown(string title)
        {
            Overlay.Visibility = Visibility.Visible;
            BusyRing.IsActive = true;
            OverlayIcon.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Visible;
            StopRetryButton.Visibility = Visibility.Visible;
            OverlayTitle.Text = title;
            OverlayDetail.Text =
                $"{_retryReason}\n\nReconnecting automatically in {_retrySecondsLeft}s (attempt {_retryAttempt})";
        }

        private void PostNotice(string text) => Post("m" + DimStart + text + DimEnd);

        private void Post(string message)
        {
            if (!_webViewReady || Web.CoreWebView2 is null)
            {
                return;
            }

            try
            {
                Web.CoreWebView2.PostWebMessageAsString(message);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // WebView torn down between the check and the post.
            }
        }

        private async void OnRetryClick(object sender, RoutedEventArgs e)
        {
            if (_profile is null)
            {
                return;
            }

            // An explicit retry re-arms auto-reconnect: the user clearly wants this session back.
            _autoReconnect = true;
            StopRetryCountdown();
            DetachSession();
            await ConnectAsync(_profile, _secret);
        }

        private void DetachSession()
        {
            SshSession? session = _session;
            _session = null;

            if (session is null)
            {
                return;
            }

            session.OutputReceived -= OnSessionOutput;
            session.Closed -= OnSessionClosed;
            session.Notice -= OnSessionNotice;

            // Never dispose on the UI thread: SSH.NET's disconnect blocks until the remote answers
            // or its timeout expires, which would freeze the window on close and on reconnect.
            SshTeardown.DisposeInBackground(session);
        }

        /// <summary>Tears down the SSH session and the WebView. Called when the tab or window closes.</summary>
        public void Shutdown()
        {
            _shuttingDown = true;
            _autoReconnect = false;
            StopRetryCountdown();
            _connectCts?.Cancel();
            DetachSession();

            if (Web.CoreWebView2 is not null)
            {
                Web.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }

            _coreConfigured = false;
            _webViewReady = false;
            Web.Close();
        }

        private void SetState(TerminalState state)
        {
            if (State == state)
            {
                return;
            }

            State = state;
            StateChanged?.Invoke(this, state);
        }

        private void ShowBusy(string title, string detail)
        {
            Overlay.Visibility = Visibility.Visible;
            BusyRing.IsActive = true;
            OverlayIcon.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Collapsed;
            StopRetryButton.Visibility = Visibility.Collapsed;
            OverlayTitle.Text = title;
            OverlayDetail.Text = detail;
        }

        private void ShowError(string title, string detail, bool canRetry)
        {
            Overlay.Visibility = Visibility.Visible;
            BusyRing.IsActive = false;
            OverlayIcon.Visibility = Visibility.Visible;
            RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
            StopRetryButton.Visibility = Visibility.Collapsed;
            OverlayTitle.Text = title;
            OverlayDetail.Text = detail;
        }

        private void HideOverlay()
        {
            Overlay.Visibility = Visibility.Collapsed;
            BusyRing.IsActive = false;
        }

        private static string DescribeWebViewFailure(Exception ex)
        {
            if (ex is FileNotFoundException)
            {
                return ex.Message;
            }

            return ex.Message +
                "\n\nCheck that the WebView2 runtime is installed (https://developer.microsoft.com/microsoft-edge/webview2).";
        }

        private static bool TryDecodeBase64(string value, out byte[] bytes)
        {
            try
            {
                bytes = Convert.FromBase64String(value);
                return true;
            }
            catch (FormatException)
            {
                bytes = Array.Empty<byte>();
                return false;
            }
        }
    }
}
