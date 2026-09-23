using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using Claret.Models;

namespace Claret.Services
{
    /// <summary>
    /// The port is held by something else. Kept distinct so it is never retried in a loop: waiting
    /// for another program to let go is the user's call, not the app's.
    /// </summary>
    internal sealed class PortInUseException : Exception
    {
        public PortInUseException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// A serial console: the same byte pump as an SSH shell, over a COM port. Differences that
    /// matter are all here — there is no PTY to resize, nothing to identify the far end with, and
    /// an unplugged adapter reports itself by throwing on read rather than closing politely.
    /// </summary>
    internal sealed class SerialSession : ITerminalLink
    {
        private const int ReadBufferSize = 16 * 1024;

        private readonly SerialConnection _settings;
        private readonly object _gate = new();

        private SerialPort? _port;
        private Task? _readerTask;
        private long _bytesReceived;
        private long _bytesSent;
        private int _closedRaised;
        private bool _disposed;
        private SerialByteChannel? _captureChannel;

        public SerialSession(SerialConnection settings) => _settings = settings.Clone();

        public event EventHandler<byte[]>? OutputReceived;

        public event EventHandler<string?>? Closed;

        public event EventHandler<string>? Notice;

        public bool IsConnected
        {
            get
            {
                lock (_gate)
                {
                    return _port is { IsOpen: true };
                }
            }
        }

        public long BytesReceived => Interlocked.Read(ref _bytesReceived);

        public long BytesSent => Interlocked.Read(ref _bytesSent);

        public Task ConnectAsync(uint columns, uint rows, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            var port = new SerialPort(_settings.PortName, _settings.BaudRate, _settings.Parity, _settings.DataBits, _settings.StopBits)
            {
                Handshake = _settings.Handshake,

                // A console cable often leaves these lines unwired; asserting them is what makes
                // the far side talk at all, and costs nothing when they are absent.
                DtrEnable = true,
                RtsEnable = _settings.Handshake is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff),

                // Reads block until at least one byte arrives; the pump wants that, not a timeout.
                ReadTimeout = SerialPort.InfiniteTimeout,
                WriteTimeout = 2000,
                ReadBufferSize = ReadBufferSize,
            };

            try
            {
                port.Open();
            }
            catch (UnauthorizedAccessException)
            {
                // The usual cause by far, and the platform message ("Access to the path 'COM3' is
                // denied") says nothing about why. Name the real reason instead.
                port.Dispose();
                throw new PortInUseException(
                    $"{_settings.PortName} is already open in another program. "
                    + "Close the other terminal, or unplug and replug the adapter if nothing seems to hold it.");
            }
            catch (Exception)
            {
                port.Dispose();
                throw;
            }

            lock (_gate)
            {
                _port = port;
            }

            Notice?.Invoke(this, $"[{_settings.Summary} opened]\n");

            _readerTask = Task.Factory.StartNew(
                ReadLoop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Nothing to ask: a serial line carries no identity, and probing one by writing to it
        /// would be typing into someone else's console.
        /// </summary>
        public Task<RemotePlatform> DetectPlatformAsync(CancellationToken cancellationToken) =>
            Task.FromResult(RemotePlatform.Unknown);

        public void SendText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            Send(System.Text.Encoding.UTF8.GetBytes(text));
        }

        public void Send(byte[] data)
        {
            if (data.Length == 0)
            {
                return;
            }

            SerialPort? port;
            lock (_gate)
            {
                port = _port;
            }

            if (port is null || !port.IsOpen)
            {
                return;
            }

            try
            {
                port.Write(data, 0, data.Length);
                Interlocked.Add(ref _bytesSent, data.Length);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException
                                          or UnauthorizedAccessException)
            {
                RaiseClosed($"write failed: {ex.Message}");
            }
        }

        /// <summary>A serial line has no window size to report.</summary>
        public void Resize(uint columns, uint rows)
        {
        }

        public bool SupportsBreak => true;

        /// <summary>
        /// Holds the line low for a quarter second — long enough for the far end to see a break,
        /// short enough not to look like a disconnect.
        /// </summary>
        public void SendBreak()
        {
            SerialPort? port;
            lock (_gate)
            {
                port = _port;
            }

            if (port is null || !port.IsOpen)
            {
                return;
            }

            try
            {
                port.BreakState = true;
                Thread.Sleep(250);
                port.BreakState = false;
                Notice?.Invoke(this, "[break sent]\n");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException
                                          or UnauthorizedAccessException or NotSupportedException)
            {
                Notice?.Invoke(this, $"[break failed: {ex.Message}]\n");
            }
        }

        public bool SupportsFileTransfer => true;

        /// <summary>
        /// Reads the whole file into memory (boards get flashed in megabytes, not gigabytes),
        /// diverts the read loop's output away from the terminal for the duration, and optionally
        /// runs the transfer at a different baud than the console is open at — restored afterward
        /// either way, including when the transfer throws or is cancelled.
        /// </summary>
        public async Task SendFileAsync(
            string path,
            FileTransferProtocol protocol,
            int? baudRate,
            IProgress<FileTransferProgress> progress,
            CancellationToken cancellationToken)
        {
            SerialPort? port;
            lock (_gate)
            {
                port = _port;
            }

            if (port is null || !port.IsOpen)
            {
                throw new InvalidOperationException("The port is not open.");
            }

            byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            string fileName = Path.GetFileName(path);

            int originalBaud = port.BaudRate;
            bool changedBaud = baudRate is { } requested && requested != originalBaud;

            var capture = new SerialByteChannel();
            lock (_gate)
            {
                _captureChannel = capture;
            }

            try
            {
                if (changedBaud)
                {
                    port.BaudRate = baudRate!.Value;
                }

                capture.Drain();
                var io = new TransferIo(Send, capture.ReadByteAsync);

                switch (protocol)
                {
                    case FileTransferProtocol.Xmodem:
                        await XmodemSender.SendAsync(io, data, progress, cancellationToken).ConfigureAwait(false);
                        break;
                    case FileTransferProtocol.Ymodem:
                        await YmodemSender.SendAsync(io, fileName, data, progress, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case FileTransferProtocol.Zmodem:
                        await ZmodemSender.SendAsync(io, fileName, data, progress, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _captureChannel = null;
                }

                if (changedBaud)
                {
                    try
                    {
                        port.BaudRate = originalBaud;
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException
                                                  or UnauthorizedAccessException)
                    {
                        // The port is already in trouble if this throws; the transfer's own
                        // exception (or success) is what matters to the caller.
                    }
                }
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
                    SerialPort? port;
                    lock (_gate)
                    {
                        port = _port;
                    }

                    if (port is null || !port.IsOpen)
                    {
                        break;
                    }

                    int read = port.BaseStream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    Interlocked.Add(ref _bytesReceived, read);

                    byte[] chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);

                    SerialByteChannel? capture;
                    lock (_gate)
                    {
                        capture = _captureChannel;
                    }

                    if (capture is not null)
                    {
                        // A file transfer owns the line: its bytes are protocol data, not
                        // something the terminal should ever try to render.
                        capture.Push(chunk);
                    }
                    else
                    {
                        OutputReceived?.Invoke(this, chunk);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException
                                          or UnauthorizedAccessException or ObjectDisposedException)
            {
                // Pulling the adapter out lands here rather than in a clean close.
                reason = _disposed ? null : $"{_settings.PortName} disconnected: {ex.Message}";
            }

            RaiseClosed(reason);
        }

        private void RaiseClosed(string? reason)
        {
            if (Interlocked.Exchange(ref _closedRaised, 1) == 0)
            {
                Closed?.Invoke(this, reason);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            SerialPort? port;
            lock (_gate)
            {
                port = _port;
                _port = null;
            }

            try
            {
                port?.Dispose();
            }
            catch (Exception)
            {
                // A port whose device vanished throws on close; nothing left to do about it.
            }

            // The read loop is blocked on a stream that is now dead; it unwinds on its own.
            _readerTask = null;
            RaiseClosed(null);
        }
    }
}
