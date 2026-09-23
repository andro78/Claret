using System;
using System.Threading;
using System.Threading.Tasks;

namespace Claret.Services
{
    /// <summary>
    /// The two operations a modem protocol needs from whatever is carrying it — a write and a
    /// timed byte read — so Xmodem/Ymodem/Zmodem never touch <see cref="SerialSession"/> directly.
    /// </summary>
    internal sealed class TransferIo
    {
        private readonly Action<byte[]> _write;
        private readonly Func<int, CancellationToken, Task<int>> _readByte;

        public TransferIo(Action<byte[]> write, Func<int, CancellationToken, Task<int>> readByte)
        {
            _write = write;
            _readByte = readByte;
        }

        public void Write(byte[] data) => _write(data);

        /// <summary>Reads one byte, or -1 if none arrived within <paramref name="timeoutMs"/>.</summary>
        public Task<int> ReadByteAsync(int timeoutMs, CancellationToken cancellationToken) =>
            _readByte(timeoutMs, cancellationToken);
    }
}
