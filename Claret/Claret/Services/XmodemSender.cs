using System;
using System.Threading;
using System.Threading.Tasks;

namespace Claret.Services
{
    /// <summary>Classic Xmodem send: 128-byte blocks, checksum or CRC-16 depending on what the receiver asks for.</summary>
    internal static class XmodemSender
    {
        private const int BlockSize = 128;

        public static async Task SendAsync(
            TransferIo io, byte[] data, IProgress<FileTransferProgress>? progress, CancellationToken ct)
        {
            bool useCrc = await XYModemCore.WaitForStartAsync(io, ct).ConfigureAwait(false);

            long total = data.Length;
            int offset = 0;
            byte blockNumber = 1;

            // Even a zero-byte file still sends one (fully padded) block — nothing at all reads
            // to most receivers as a transfer that never started.
            do
            {
                int length = Math.Min(BlockSize, data.Length - offset);
                byte[] payload = XYModemCore.Pad(data, offset, length, BlockSize);
                byte[] block = XYModemCore.BuildBlock(XYModemCore.SOH, blockNumber, payload, useCrc);

                await XYModemCore.SendBlockAsync(io, block, ct).ConfigureAwait(false);

                offset += length;
                blockNumber++;
                progress?.Report(new FileTransferProgress(offset, total, 0));
            }
            while (offset < data.Length);

            await XYModemCore.SendEotAsync(io, ct).ConfigureAwait(false);
        }
    }
}
