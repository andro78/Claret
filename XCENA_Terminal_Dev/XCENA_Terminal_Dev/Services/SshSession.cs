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

            ConnectionInfo connectionInfo = BuildConnectionInfo(profile, secret);
            var client = new SshClient(connectionInfo);
            string? hostKeyFailure = null;

            client.HostKeyReceived += (_, e) =>
            {
                HostKeyVerdict verdict = KnownHostsStore.Instance.Verify(
                    profile.Host, profile.Port, e.HostKeyName, e.FingerPrintSHA256);

                switch (verdict)
                {
                    case HostKeyVerdict.Pinned:
                        e.CanTrust = true;
                        Notice?.Invoke(this, $"Pinned a new host key.\n  {e.HostKeyName} SHA256:{e.FingerPrintSHA256}\n");
                        break;

                    case HostKeyVerdict.Trusted:
                        e.CanTrust = true;
                        break;

                    default:
                        e.CanTrust = false;
                        hostKeyFailure =
                            $"The host key differs from the pinned one ({e.HostKeyName} SHA256:{e.FingerPrintSHA256}). " +
                            "The server may have been reinstalled, or this may be a man-in-the-middle attack. Verify it, then use \"Forget host key\" on the profile and reconnect.";
                        break;
                }
            };

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SshConnectionException) when (hostKeyFailure is not null)
            {
                client.Dispose();
                throw new HostKeyMismatchException(hostKeyFailure);
            }
            catch
            {
                client.Dispose();
                throw;
            }

            if (hostKeyFailure is not null)
            {
                client.Dispose();
                throw new HostKeyMismatchException(hostKeyFailure);
            }

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

        private static ConnectionInfo BuildConnectionInfo(ConnectionProfile profile, string? secret)
        {
            if (string.IsNullOrWhiteSpace(profile.Host))
            {
                throw new ArgumentException("Enter a host.", nameof(profile));
            }

            if (string.IsNullOrWhiteSpace(profile.Username))
            {
                throw new ArgumentException("Enter a user name.", nameof(profile));
            }

            var methods = new List<AuthenticationMethod>();

            if (profile.AuthMode == SshAuthMode.PrivateKey)
            {
                if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
                {
                    throw new ArgumentException("Enter the private key file path.", nameof(profile));
                }

                if (!File.Exists(profile.PrivateKeyPath))
                {
                    throw new FileNotFoundException($"Private key file not found: {profile.PrivateKeyPath}");
                }

                PrivateKeyFile keyFile = string.IsNullOrEmpty(secret)
                    ? new PrivateKeyFile(profile.PrivateKeyPath)
                    : new PrivateKeyFile(profile.PrivateKeyPath, secret);

                methods.Add(new PrivateKeyAuthenticationMethod(profile.Username, keyFile));
            }
            else
            {
                string password = secret ?? string.Empty;
                methods.Add(new PasswordAuthenticationMethod(profile.Username, password));

                // Many sshd configurations only offer keyboard-interactive for passwords.
                var interactive = new KeyboardInteractiveAuthenticationMethod(profile.Username);
                interactive.AuthenticationPrompt += (_, e) =>
                {
                    foreach (AuthenticationPrompt prompt in e.Prompts)
                    {
                        // "암호" catches Korean-localised sshd prompts. This matches text sent by
                        // the remote server, not app UI, so it stays.
                        if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                            prompt.Request.Contains("암호", StringComparison.Ordinal))
                        {
                            prompt.Response = password;
                        }
                    }
                };
                methods.Add(interactive);
            }

            return new ConnectionInfo(profile.Host, profile.Port, profile.Username, methods.ToArray())
            {
                Timeout = TimeSpan.FromSeconds(20),
                Encoding = Encoding.UTF8,
            };
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
