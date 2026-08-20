using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Services
{
    /// <summary>
    /// The server presented a host key that differs from the pinned one. Kept distinct from other
    /// connection failures so callers never auto-retry into a possible man-in-the-middle.
    /// </summary>
    internal sealed class HostKeyMismatchException : Exception
    {
        public HostKeyMismatchException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// One interactive SSH shell: connection, PTY, and the byte pump between the socket and the UI.
    /// All events are raised on background threads — callers must marshal to the UI thread themselves.
    /// </summary>
    internal sealed class SshSession : IDisposable
    {
        private const int ReadBufferSize = 32 * 1024;

        private readonly object _gate = new();

        private SshClient? _client;
        private ShellStream? _shell;
        private Task? _readerTask;
        private int _closedRaised;
        private bool _disposed;

        /// <summary>Raw bytes received from the shell. The array is owned by the handler.</summary>
        public event EventHandler<byte[]>? OutputReceived;

        /// <summary>Raised exactly once. The argument is null for a clean exit, otherwise the failure reason.</summary>
        public event EventHandler<string?>? Closed;

        /// <summary>Informational lines (host key pinning, banners) to echo into the terminal.</summary>
        public event EventHandler<string>? Notice;

        /// <summary>
        /// The SSH identification string the server sent, e.g. "SSH-2.0-OpenSSH_8.9p1 Ubuntu-3".
        /// Available from the moment the handshake completes, even on hosts that run no commands.
        /// </summary>
        public string? ServerVersion { get; private set; }

        public bool IsConnected
        {
            get
            {
                lock (_gate)
                {
                    return _client is { IsConnected: true } && _shell is not null;
                }
            }
        }

        /// <summary>
        /// Opens the connection and starts the read pump. Throws on authentication, host key,
        /// or network failure; the caller is expected to surface the message.
        /// </summary>
        public async Task ConnectAsync(
            ConnectionProfile profile,
            string? secret,
            uint columns,
            uint rows,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            ConnectionInfo connectionInfo = SshConnectionFactory.Build(profile, secret);
            var client = new SshClient(connectionInfo);
            HostKeyGate hostKey = SshConnectionFactory.GuardHostKey(
                client, profile, line => Notice?.Invoke(this, line));

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SshConnectionException) when (hostKey.Failure is not null)
            {
                client.Dispose();
                throw new HostKeyMismatchException(hostKey.Failure);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            if (hostKey.Failure is not null)
            {
                client.Dispose();
                throw new HostKeyMismatchException(hostKey.Failure);
            }

            ServerVersion = connectionInfo.ServerVersion;

            try
            {
                ShellStream shell = client.CreateShellStream(
                    profile.TerminalType,
                    Math.Max(columns, 20u),
                    Math.Max(rows, 5u),
                    width: 0,
                    height: 0,
                    ReadBufferSize);

                lock (_gate)
                {
                    _client = client;
                    _shell = shell;
                }

                client.ErrorOccurred += OnClientError;
                _readerTask = Task.Factory.StartNew(
                    ReadLoop,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
            catch
            {
                client.Dispose();
                lock (_gate)
                {
                    _client = null;
                    _shell = null;
                }

                throw;
            }
        }

        /// <summary>
        /// Works out what the far end is running, for the tab icon. Asks the host first, on its own
        /// exec channel so nothing lands in the interactive shell, and falls back to the SSH banner
        /// when that is refused — a BMC or a forced-command account will refuse it. Best-effort by
        /// design: an unknown platform is a normal answer, never an error.
        /// </summary>
        public async Task<RemotePlatform> DetectPlatformAsync(CancellationToken cancellationToken)
        {
            SshClient? client;
            lock (_gate)
            {
                client = _client;
            }

            if (client is null || !client.IsConnected)
            {
                return RemotePlatform.Unknown;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));

                // Both, in one channel: os-release names the distribution, uname covers the hosts
                // that have no os-release at all (a BSD, a busybox appliance).
                using SshCommand command = client.CreateCommand(
                    "cat /etc/os-release 2>/dev/null; uname -sr 2>/dev/null");

                await command.ExecuteAsync(timeout.Token).ConfigureAwait(false);

                RemotePlatform probed = RemotePlatform.FromProbe(command.Result);
                if (probed.IsKnown)
                {
                    return probed;
                }
            }
            catch (Exception)
            {
                // No exec channel, no shell, or it took too long. The banner is the fallback.
            }

            return RemotePlatform.FromServerVersion(ServerVersion);
        }

        public void SendText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            Send(Encoding.UTF8.GetBytes(text));
        }

        public void Send(byte[] data)
        {
            if (data.Length == 0)
            {
                return;
            }

            ShellStream? shell;
            lock (_gate)
            {
                shell = _shell;
            }

            if (shell is null)
            {
                return;
            }

            try
            {
                shell.Write(data, 0, data.Length);
                shell.Flush();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or SshException)
            {
                RaiseClosed(ex is ObjectDisposedException ? null : ex.Message);
            }
        }

        /// <summary>Sends a window-change request so the remote PTY matches the visible grid.</summary>
        public void Resize(uint columns, uint rows)
        {
            if (columns == 0 || rows == 0)
            {
                return;
            }

            ShellStream? shell;
            lock (_gate)
            {
                shell = _shell;
            }

            if (shell is null)
            {
                return;
            }

            try
            {
                shell.ChangeWindowSize(columns, rows, 0, 0);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or IOException or SshException)
            {
                // A resize racing a disconnect is not worth reporting; the read loop reports the close.
            }
        }

        private void ReadLoop()
        {
            byte[] buffer = new byte[ReadBufferSize];
            string? reason = null;

            try
            {
                while (true)
                {
                    ShellStream? shell;
                    lock (_gate)
                    {
                        shell = _shell;
                    }

                    if (shell is null)
                    {
                        break;
                    }

                    int read = shell.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    byte[] chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                    OutputReceived?.Invoke(this, chunk);
                }
            }
            catch (ObjectDisposedException)
            {
                // Local shutdown.
            }
            catch (Exception ex)
            {
                reason = ex.Message;
            }

            RaiseClosed(reason);
        }

        private void OnClientError(object? sender, ExceptionEventArgs e)
        {
            RaiseClosed(e.Exception.Message);
        }

        private void RaiseClosed(string? reason)
        {
            if (Interlocked.Exchange(ref _closedRaised, 1) != 0)
            {
                return;
            }

            Closed?.Invoke(this, reason);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            ShellStream? shell;
            SshClient? client;
            lock (_gate)
            {
                shell = _shell;
                client = _client;
                _shell = null;
                _client = null;
            }

            if (client is not null)
            {
                client.ErrorOccurred -= OnClientError;
            }

            try
            {
                shell?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or SshException or ObjectDisposedException)
            {
            }

            try
            {
                client?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or SshException or ObjectDisposedException)
            {
            }

            RaiseClosed(null);
        }
    }
}
