using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Claret.Services
{
    /// <summary>
    /// Ymodem: an Xmodem-1K body wrapped in a named header block, so the far end knows what it is
    /// receiving and how big it will be. Single file only — the closing empty header ends the
    /// batch immediately rather than offering a next one.
    /// </summary>
    internal static class YmodemSender
    {
        private const int DataBlockSize = 1024;
        private const int HeaderBlockSize = 128;

        public static async Task SendAsync(
            TransferIo io,
            string fileName,
            byte[] data,
            IProgress<FileTransferProgress>? progress,
            CancellationToken ct)
        {
            bool useCrc = await XYModemCore.WaitForStartAsync(io, ct).ConfigureAwait(false);

            await SendHeaderAsync(io, fileName, data.Length, useCrc, ct).ConfigureAwait(false);

            // The receiver asks again before the first data block; some implementations send the
            // block straight away instead, so this wait is generous but never fatal on its own.
            await io.ReadByteAsync(2000, ct).ConfigureAwait(false);

            long total = data.Length;
            int offset = 0;
            byte blockNumber = 1;

            while (offset < data.Length)
            {
                int length = Math.Min(DataBlockSize, data.Length - offset);
                byte[] payload = XYModemCore.Pad(data, offset, length, DataBlockSize);
                byte[] block = XYModemCore.BuildBlock(XYModemCore.STX, blockNumber, payload, useCrc);

                await XYModemCore.SendBlockAsync(io, block, ct).ConfigureAwait(false);

                offset += length;
                blockNumber++;
                progress?.Report(new FileTransferProgress(offset, total, 0));
            }

            await XYModemCore.SendEotAsync(io, ct).ConfigureAwait(false);

            // A header block with an empty name closes the batch: no next file is coming.
            await XYModemCore.WaitForStartAsync(io, ct).ConfigureAwait(false);
            byte[] closingBlock = XYModemCore.BuildBlock(XYModemCore.SOH, 0, new byte[HeaderBlockSize], useCrc);
            await XYModemCore.SendBlockAsync(io, closingBlock, ct).ConfigureAwait(false);
        }

        private static async Task SendHeaderAsync(
            TransferIo io, string fileName, long fileLength, bool useCrc, CancellationToken ct)
        {
            byte[] name = Encoding.ASCII.GetBytes(fileName);
            byte[] size = Encoding.ASCII.GetBytes(fileLength.ToString(CultureInfo.InvariantCulture));

            // Two null terminators and the size have to fit too; an implausibly long name is
            // truncated rather than overflowing the fixed 128-byte block.
            int maxName = Math.Max(0, HeaderBlockSize - 2 - size.Length);
            if (name.Length > maxName)
            {
                Array.Resize(ref name, maxName);
            }

            byte[] payload = new byte[HeaderBlockSize];
            Buffer.BlockCopy(name, 0, payload, 0, name.Length);
            Buffer.BlockCopy(size, 0, payload, name.Length + 1, size.Length);

            byte[] block = XYModemCore.BuildBlock(XYModemCore.SOH, 0, payload, useCrc);
            await XYModemCore.SendBlockAsync(io, block, ct).ConfigureAwait(false);
        }
    }
}
