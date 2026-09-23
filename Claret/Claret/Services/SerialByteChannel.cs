using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Claret.Services
{
    /// <summary>
    /// Where a serial session's incoming bytes go while a file transfer owns the line: pushed here
    /// instead of into the terminal, and read back one byte at a time with a timeout — all a modem
    /// protocol's ACK/NAK dance ever needs, and simple enough to build a header scanner out of.
    /// </summary>
    internal sealed class SerialByteChannel
    {
        private readonly Channel<byte> _channel = Channel.CreateUnbounded<byte>();

        public void Push(byte[] chunk)
        {
            foreach (byte b in chunk)
            {
                _channel.Writer.TryWrite(b);
            }
        }

        /// <summary>Reads one byte, or -1 if none arrived within <paramref name="timeoutMs"/>.</summary>
        public async Task<int> ReadByteAsync(int timeoutMs, CancellationToken cancellationToken)
        {
            using var timeout = new CancellationTokenSource(timeoutMs);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            try
            {
                return await _channel.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return -1;
            }
        }

        /// <summary>Drops whatever is already sitting in the channel — terminal noise queued before capture was needed.</summary>
        public void Drain()
        {
            while (_channel.Reader.TryRead(out _))
            {
            }
        }
    }
}
