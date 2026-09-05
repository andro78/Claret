using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;
using Renci.SshNet.Common;
using Windows.Foundation;
using Claret.Dialogs;
using Claret.Models;
using Claret.Services;

namespace Claret.Controls
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
        private const string VirtualHost = "claret.invalid";
        private const string PageUrl = "https://" + VirtualHost + "/terminal.html";

        /// <summary>Seconds between automatic reconnect attempts after the link drops.</summary>
        private const int ReconnectDelaySeconds = 5;

        private readonly DispatcherQueue _dispatcher;
        private readonly object _outputGate = new();
        private readonly List<byte[]> _pendingOutput = new();

        private TaskCompletionSource<bool>? _readySource;
        private TaskCompletionSource<string>? _bufferCapture;
        private ITerminalLink? _session;
        private SessionLog? _log;
        private ConnectionProfile? _profile;
        private SerialConnection? _serial;

        // Carries "the last byte was CR" across reads, for ExpandBareLineFeeds.
        private bool _lastByteWasCr;

        // Serial line timestamps: the preference, and whether the next byte begins a line.
        private bool _serialTimestamps;
        private bool _atLineStart = true;
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
        private int _scrollbackLines = WorkspaceLayout.DefaultScrollbackLines;
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

        /// <summary>The serial settings of this pane, when it is a serial console rather than SSH.</summary>
        public SerialConnection? Serial => _serial;

        /// <summary>
        /// What to call this session in the chrome: the endpoint for SSH, the port and line
        /// settings for a serial console. Empty before the first connect.
        /// </summary>
        public string SessionLabel =>
            _profile?.Endpoint ?? _serial?.Summary ?? string.Empty;

        /// <summary>
        /// Whether there is a host on the other side to measure a round trip to. False for serial,
        /// where the cable is the whole network.
        /// </summary>
        public bool HasNetwork => _profile is not null;

        /// <summary>Whether a break can be sent on this link — serial only.</summary>
        public bool SupportsBreak => _session?.SupportsBreak == true;

        /// <summary>The file this session is being recorded to, or null when it is not.</summary>
        public string? LogPath => _log?.Path;

        /// <summary>
        /// Sends a break. Console-only in practice: on a board this is what drops into the boot
        /// loader, so it is never bound to a key — the menu is the only way to ask for it.
        /// </summary>
        public void SendBreak() => _session?.SendBreak();

        /// <summary>
        /// Starts recording what the session prints. Output already on screen is not in the file:
        /// a log begins when you ask for one, and saying otherwise would be a lie about the record.
        /// </summary>
        public void StartLogging(string path, string header)
        {
            StopLogging();
            _log = SessionLog.Open(path, header);
            PostNotice($"[logging to {path}]\n");
        }

        public void StopLogging()
        {
            if (_log is null)
            {
                return;
            }

            string path = _log.Path;
            _log.Dispose();
            _log = null;

            PostNotice($"[logging stopped: {path}]\n");
        }

        /// <summary>
        /// Everything currently in the pane's buffer — on-screen text plus scrollback — as one
        /// plain-text snapshot, for a one-shot "save what's here" rather than <see cref="StartLogging"/>'s
        /// continuous recording, which (as its own doc says) never has what arrived before it was
        /// turned on. Answered by the page itself, since only it still holds the buffer once a
        /// session has ended. Empty if the page never answers within a few seconds.
        /// </summary>
        public async Task<string> CaptureBufferAsync()
        {
            if (!_webViewReady)
            {
                return string.Empty;
            }

            var capture = new TaskCompletionSource<string>();
            _bufferCapture = capture;
            Post("b");

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using CancellationTokenRegistration registration =
                timeout.Token.Register(() => capture.TrySetCanceled());

            try
            {
                return await capture.Task.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }
            finally
            {
                _bufferCapture = null;
            }
        }

        /// <summary>Last title reported by the remote shell via OSC 0/2, if any.</summary>
        public string? RemoteTitle { get; private set; }

        /// <summary>
        /// Profile and in-memory secret of this pane, so a split can open a second shell on the
        /// same host without prompting again. Null before the first connect attempt.
        /// </summary>
        internal (ConnectionProfile Profile, string? Secret)? Connection =>
            _profile is null ? null : (_profile, _secret);

        /// <summary>
        /// Boots the WebView (if needed) and opens an SSH session. Failures are shown in the
        /// overlay rather than thrown, so a bad profile cannot tear down the tab.
        /// </summary>
        public Task ConnectAsync(ConnectionProfile profile, string? secret)
        {
            _profile = profile;
            _secret = secret;
            _serial = null;

            return OpenAsync(profile.Endpoint, () => new SshSession(profile, secret));
        }

        /// <summary>
        /// Opens a serial console instead. Same terminal, same byte pump; only the link differs.
        /// </summary>
        public Task ConnectSerialAsync(SerialConnection settings)
        {
            _profile = null;
            _secret = null;
            _serial = settings.Clone();

            return OpenAsync(_serial.Summary, () => new SerialSession(_serial));
        }

        /// <summary>
        /// The part both links share: bring the page up, open the link, and put the terminal into
        /// the right state — including the retry policy, which only differs in what it reconnects.
        /// </summary>
        private async Task OpenAsync(string label, Func<ITerminalLink> open)
        {
            StopRetryCountdown();
            SetState(TerminalState.Connecting);
            ShowBusy(_retryAttempt > 0 ? $"Reconnecting… (attempt {_retryAttempt})" : "Connecting…", label);

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

            ITerminalLink session = open();
            _lastByteWasCr = false;
            _atLineStart = true;
            session.OutputReceived += OnSessionOutput;
            session.Closed += OnSessionClosed;
            session.Notice += OnSessionNotice;
            _session = session;

            _connectCts?.Dispose();
            _connectCts = new CancellationTokenSource();

            try
            {
                await session.ConnectAsync(_columns, _rows, _connectCts.Token).ConfigureAwait(true);
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

                // A server that is still rebooting refuses connections, and an adapter can be
                // plugged back in; keep trying. Bad credentials or a changed host key never fix
                // themselves, so those stop here.
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
                PostNotice($"[Reconnected to {label} after {_retryAttempt} attempt(s)]\n");
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
        private async Task IdentifyPlatformAsync(ITerminalLink session)
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

        /// <summary>
        /// Ends the link without closing the tab or touching the scrollback. Deliberately does not
        /// call <see cref="DetachSession"/> — that unsubscribes <c>Closed</c> before disposing, which
        /// is right for a tab that is going away (<see cref="Shutdown"/>) but would leave this pane
        /// with no state change and no way back. Left subscribed, disposing raises <c>Closed</c>
        /// with a null reason exactly as a clean remote exit would, which lands on the same "Session
        /// ended" overlay with Reconnect — nothing here has to duplicate that.
        /// </summary>
        public void Disconnect()
        {
            _autoReconnect = false;
            StopRetryCountdown();
            _connectCts?.Cancel();

            if (_session is { } session)
            {
                SshTeardown.DisposeInBackground(session);
            }
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
        /// loading yet still gets them once it reports ready. Font is per-pane (see
        /// <see cref="ApplyFont"/>) and edited separately, so whatever this pane is already using
        /// carries through untouched — a colour change broadcast to every open pane must not reset
        /// the font any one of them was individually given.
        /// </summary>
        public void ApplyAppearance(TerminalAppearance appearance)
        {
            string family = _appearance?.FontFamily ?? appearance.FontFamily;
            int size = _appearance?.FontSize ?? appearance.FontSize;

            _appearance = appearance.Clone();
            _appearance.FontFamily = family;
            _appearance.FontSize = size;

            Windows.UI.Color background = _appearance.BackgroundColor;
            Web.DefaultBackgroundColor = background;
            TerminalBackground.Color = background;

            PostAppearance();
        }

        // Named distinctly from Control.FontFamily/FontSize, which style this control's own XAML
        // chrome (nothing here uses them) — these describe the terminal's rendered text instead.

        /// <summary>This pane's own terminal font family, independent of every other open pane.</summary>
        public string TerminalFontFamily => _appearance?.FontFamily ?? string.Empty;

        /// <summary>This pane's own terminal font size, independent of every other open pane.</summary>
        public int TerminalFontSize => _fontSize;

        /// <summary>This pane's own background colour, independent of every other open pane — for
        /// syncing the surrounding pane frame to whichever tab is actually showing.</summary>
        public Windows.UI.Color TerminalBackgroundColor => (_appearance ?? new TerminalAppearance()).BackgroundColor;

        /// <summary>
        /// A snapshot of this pane's own colours and font, both independent of every other open
        /// pane — safe to hand to a dialog (or another pane) without either side mutating the
        /// original. A pane that has never had an appearance applied yet reports the defaults.
        /// </summary>
        public TerminalAppearance CurrentAppearance => (_appearance ?? new TerminalAppearance()).Clone();

        /// <summary>Changes only this pane's font — family and size — leaving its colours, and
        /// every other pane's font, untouched.</summary>
        public void ApplyFont(string family, int size)
        {
            if (_appearance is not null)
            {
                _appearance.FontFamily = family;
                _appearance.FontSize = size;
            }

            Post("y" + family);
            _fontSize = Math.Clamp(size, TerminalAppearance.MinFontSize, TerminalAppearance.MaxFontSize);
            Post("s" + _fontSize.ToString());
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

        /// <summary>
        /// Whether each line of serial output is prefixed with the time it arrived. Ignored by an
        /// SSH pane, which has a shell on the far end that can date its own output.
        /// </summary>
        public void ApplySerialTimestamps(bool enabled)
        {
            _serialTimestamps = enabled;
        }

        /// <summary>How many lines of scrollback the page keeps. Applied live — xterm.js resizes its
        /// buffer in place, dropping the oldest lines if it shrinks.</summary>
        public void ApplyScrollback(int lines)
        {
            _scrollbackLines = Math.Clamp(
                lines,
                WorkspaceLayout.MinScrollbackLines,
                WorkspaceLayout.MaxScrollbackLines);
            PostScrollback();
        }

        private void PostScrollback()
        {
            if (_webViewReady)
            {
                Post("l" + _scrollbackLines.ToString());
            }
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

        /// <summary>
        /// Whether an AI CLI approval prompt is answered "Yes" without asking, in this session
        /// only. Deliberately per session and never persisted: arming it is a decision about the
        /// task in front of you, not a setting the app should remember on your behalf.
        /// </summary>
        public bool AutoApprove => _autoApprove;

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
            if (_appearance is not null)
            {
                _appearance.FontSize = _fontSize;
            }

            Post("s" + _fontSize.ToString());
        }

        public void ResetFontSize()
        {
            _fontSize = 14;
            if (_appearance is not null)
            {
                _appearance.FontSize = _fontSize;
            }

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
            PostScrollback();
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
                        _ = CopyToClipboardAsync(Encoding.UTF8.GetString(selectionBytes));
                    }

                    break;

                case 'v': // paste request
                    PasteFromClipboard();
                    break;

                case 'n': // an AI prompt was answered on our behalf; the shell shows the rest
                    AutoApproved?.Invoke(this, body);
                    break;

                case 'e': // the page reporting its own trouble
                    // The page has posted these since it was written and nothing was listening,
                    // so a WebGL addon that failed to load or a theme patch that would not parse
                    // left no trace anywhere. They are rare and each one explains something the
                    // user can otherwise only guess at, so they are shown rather than logged.
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        PostNotice(body);
                    }

                    break;

                case 'k': // window-level chord
                    RaiseCommand(body);
                    break;

                case 'b': // the buffer as base64 UTF-8 text, answering CaptureBufferAsync
                    if (TryDecodeBase64(body, out byte[] bufferBytes))
                    {
                        _bufferCapture?.TrySetResult(Encoding.UTF8.GetString(bufferBytes));
                    }

                    break;

                case 'u': // right-click at "hasSelection,x,y" (WebView-local coordinates)
                    ShowTerminalContextMenu(body);
                    break;
            }
        }

        private void RaiseCommand(string name)
        {
            // The one command the view answers itself, because it acts on this view's WebView
            // rather than on the window. DevTools are enabled in a debug build but there is no
            // way in without this: F12 belongs to AreBrowserAcceleratorKeysEnabled, which is off
            // so the browser cannot steal terminal keys, and right-click is taken by paste.
            if (name == "devtools")
            {
#if DEBUG
                Web.CoreWebView2?.OpenDevToolsWindow();
#endif
                return;
            }

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

        /// <summary>
        /// Puts the selection on the Windows clipboard, via the plain Win32 API rather than
        /// <see cref="Windows.ApplicationModel.DataTransfer.Clipboard"/> — see
        /// <see cref="Win32Clipboard"/> for why. Windows allows one clipboard owner at a time, so
        /// another process holding it open makes a single attempt fail for no lasting reason —
        /// that one is retried. A failure that survives the retry is said out loud: a copy that
        /// quietly does not happen reads as a broken terminal.
        /// </summary>
        private async Task CopyToClipboardAsync(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (Win32Clipboard.TrySetText(text))
                {
                    return;
                }

                if (attempt == 0)
                {
                    await Task.Delay(80).ConfigureAwait(true);
                    continue;
                }

                PostNotice("Copy failed: could not open the clipboard.");
            }
        }

        private void PasteFromClipboard()
        {
            string? text = Win32Clipboard.TryGetText();
            if (string.IsNullOrEmpty(text) || !_webViewReady)
            {
                return;
            }

            // Hand it to xterm so bracketed-paste mode is honoured.
            Post("p" + text.Replace("\r\n", "\r"));
        }

        /// <summary>
        /// Right-click's own menu — native WinUI rather than an HTML one, so it looks like every
        /// other menu in the app. <paramref name="body"/> is "hasSelection,x,y" in WebView-local
        /// coordinates, which the page sends because the host cannot see clicks landing inside it.
        /// </summary>
        private void ShowTerminalContextMenu(string body)
        {
            string[] parts = body.Split(',');
            if (parts.Length != 3
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                return;
            }

            bool hasSelection = parts[0] == "1";

            var menu = new MenuFlyout();

            var copy = new MenuFlyoutItem
            {
                Text = "Copy",
                IsEnabled = hasSelection,
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
            };
            copy.Click += (_, _) => Post("q");

            var paste = new MenuFlyoutItem
            {
                Text = "Paste",
                KeyboardAcceleratorTextOverride = "Ctrl+Shift+V",
            };
            paste.Click += (_, _) => PasteFromClipboard();

            var selectAll = new MenuFlyoutItem { Text = "Select All" };
            selectAll.Click += (_, _) => Post("d");

            var find = new MenuFlyoutItem { Text = "Find…" };
            find.Click += (_, _) => _ = ShowFindDialogAsync();

            var clear = new MenuFlyoutItem { Text = "Clear" };
            clear.Click += (_, _) => ClearScreen();

            menu.Items.Add(copy);
            menu.Items.Add(paste);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(selectAll);
            menu.Items.Add(find);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(clear);

            menu.ShowAt(Web, new FlyoutShowOptions { Position = new Point(x, y) });
        }

        /// <summary>
        /// Jumps to the first line containing <paramref name="query"/>, searching the same buffer
        /// <see cref="CaptureBufferAsync"/> saves — scrollback included, top down. A plain substring
        /// search: Find is for jumping to something you remember seeing, not a regex tool, which the
        /// highlight rules already cover for anyone colouring a pattern permanently.
        /// </summary>
        private async Task ShowFindDialogAsync()
        {
            var dialog = new FindDialog();

            try
            {
                dialog.XamlRoot = XamlRoot;
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // Another dialog already owns the XamlRoot; asking again once it closes is on the
                // user, not something worth queuing behind.
                PostNotice("[Find: another dialog is already open]\n");
                return;
            }

            string query = dialog.Query;
            if (string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            string text = await CaptureBufferAsync();
            string[] lines = text.Length == 0 ? Array.Empty<string>() : text.Split('\n');

            int match = Array.FindIndex(lines, line => line.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (match < 0)
            {
                PostNotice($"[Find: no match for \"{query}\"]\n");
                return;
            }

            Post("j" + match.ToString(CultureInfo.InvariantCulture));
            PostNotice($"[Find: \"{query}\" — line {match + 1}]\n");
        }

        /// <summary>
        /// Turns a lone LF into CRLF, for serial sessions only.
        ///
        /// SSH gets this for free: the remote PTY's onlcr does it before a byte ever reaches the
        /// wire. A COM port has no line discipline behind it, so a board that writes "\n" moves the
        /// cursor down one row and leaves it in the column it was already in. Every line then
        /// starts further right than the one above and the output walks off the screen.
        ///
        /// Only an LF with no CR in front of it is touched, so a device that already sends CRLF
        /// comes through untouched and nothing is ever doubled. That CR can be the last byte of the
        /// previous read, which is why the flag outlives the chunk.
        /// </summary>
        private byte[] ExpandBareLineFeeds(byte[] chunk)
        {
            const byte Cr = 0x0D;
            const byte Lf = 0x0A;

            bool afterCr = _lastByteWasCr;
            int bare = 0;
            foreach (byte value in chunk)
            {
                if (value == Lf && !afterCr)
                {
                    bare++;
                }

                afterCr = value == Cr;
            }

            if (bare == 0)
            {
                _lastByteWasCr = afterCr;
                return chunk;
            }

            byte[] expanded = new byte[chunk.Length + bare];
            int at = 0;
            afterCr = _lastByteWasCr;
            foreach (byte value in chunk)
            {
                if (value == Lf && !afterCr)
                {
                    expanded[at++] = Cr;
                }

                expanded[at++] = value;
                afterCr = value == Cr;
            }

            _lastByteWasCr = afterCr;
            return expanded;
        }

        /// <summary>
        /// Puts the arrival time in front of every line the port sends. A board console has no
        /// shell to pipe through `ts`, and a boot log is usually read for when things happened —
        /// which stage took the four seconds — so the terminal is the only place that can say.
        ///
        /// The time is when the bytes reached this machine, not when the device wrote them. Over a
        /// slow line, or behind a device that buffers, those differ.
        ///
        /// A bare CR does not start a new line here. Progress bars return to column 0 and redraw
        /// the same row over and over; stamping those would bury the output in timestamps. Nor is
        /// an empty line stamped: the mark goes in front of the first byte that is really content.
        ///
        /// Cursor-addressing output — a TUI over the serial line — will be disturbed by this, since
        /// text lands where the stamp has already pushed it. That is the cost of the setting, and
        /// why it is off unless asked for.
        /// </summary>
        private byte[] StampLines(byte[] chunk)
        {
            const byte Cr = 0x0D;
            const byte Lf = 0x0A;

            byte[] stamp = Encoding.ASCII.GetBytes(DateTime.Now.ToString("[HH:mm:ss.fff] "));

            bool lineStart = _atLineStart;
            int stamps = 0;
            foreach (byte value in chunk)
            {
                if (value == Lf)
                {
                    lineStart = true;
                }
                else if (value != Cr && lineStart)
                {
                    stamps++;
                    lineStart = false;
                }
            }

            if (stamps == 0)
            {
                _atLineStart = lineStart;
                return chunk;
            }

            byte[] stamped = new byte[chunk.Length + (stamps * stamp.Length)];
            int at = 0;
            lineStart = _atLineStart;
            foreach (byte value in chunk)
            {
                if (lineStart && value != Cr && value != Lf)
                {
                    Buffer.BlockCopy(stamp, 0, stamped, at, stamp.Length);
                    at += stamp.Length;
                    lineStart = false;
                }

                stamped[at++] = value;
                if (value == Lf)
                {
                    lineStart = true;
                }
            }

            _atLineStart = lineStart;
            return stamped;
        }

        private void OnSessionOutput(object? sender, byte[] chunk)
        {
            // Tee straight from the wire: what the log holds is what arrived, not what survived
            // the terminal's own redraws.
            _log?.Write(chunk);

            // Display only, and after the log, so the file still holds the bytes as they came.
            if (_serial is not null)
            {
                chunk = ExpandBareLineFeeds(chunk);

                if (_serialTimestamps)
                {
                    chunk = StampLines(chunk);
                }
            }

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
                ShowError(
                    "Session ended",
                    reason ?? "The session has ended.",
                    canRetry: true,
                    coverTerminal: false);
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
            if (_shuttingDown)
            {
                return;
            }

            DetachSession();
            await ReopenAsync();
        }

        /// <summary>Opens whichever link this pane was using, SSH or serial.</summary>
        private Task ReopenAsync() => (_profile, _serial) switch
        {
            ({ } profile, _) => ConnectAsync(profile, _secret),
            (_, { } serial) => ConnectSerialAsync(serial),
            _ => Task.CompletedTask,
        };

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
            ShowError(
                "Session ended",
                _retryReason.Length == 0 ? "The session has ended." : _retryReason,
                canRetry: true,
                coverTerminal: false);
        }

        private void ShowCountdown(string title)
        {
            EndedBanner.Visibility = Visibility.Collapsed;
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
            if (_profile is null && _serial is null)
            {
                return;
            }

            // An explicit retry re-arms auto-reconnect: the user clearly wants this session back.
            _autoReconnect = true;
            StopRetryCountdown();
            DetachSession();
            await ReopenAsync();
        }

        private void DetachSession()
        {
            ITerminalLink? session = _session;
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

            // Close the file with the session, so the log ends where the session did.
            _log?.Dispose();
            _log = null;

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
            EndedBanner.Visibility = Visibility.Collapsed;
            Overlay.Visibility = Visibility.Visible;
            BusyRing.IsActive = true;
            OverlayIcon.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Collapsed;
            StopRetryButton.Visibility = Visibility.Collapsed;
            OverlayTitle.Text = title;
            OverlayDetail.Text = detail;
        }

        /// <summary>
        /// Reports a failure. <paramref name="coverTerminal"/> is false only for a session that was
        /// live and then ended: the buffer it left behind is worth reading and selecting, so that
        /// case uses <see cref="EndedBanner"/> — a strip at the bottom — instead of blacking out the
        /// whole pane the way a failure with nothing behind it yet (the WebView never started, or
        /// the very first connect attempt failed) still should.
        /// </summary>
        private void ShowError(string title, string detail, bool canRetry, bool coverTerminal = true)
        {
            if (!coverTerminal)
            {
                Overlay.Visibility = Visibility.Collapsed;
                EndedBanner.Visibility = Visibility.Visible;
                EndedTitle.Text = title;
                EndedDetail.Text = detail;
                EndedRetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            EndedBanner.Visibility = Visibility.Collapsed;
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
            EndedBanner.Visibility = Visibility.Collapsed;
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
