using System;
using System.Threading;
using System.Threading.Tasks;

namespace Claret.Services
{
    /// <summary>
    /// The block mechanics Xmodem and Ymodem share — Ymodem is Xmodem underneath, plus a named
    /// header block in front and a bigger block size. Kept here once rather than duplicated.
    /// </summary>
    internal static class XYModemCore
    {
        public const byte SOH = 0x01;
        public const byte STX = 0x02;
        public const byte EOT = 0x04;
        public const byte ACK = 0x06;
        public const byte NAK = 0x15;
        public const byte CAN = 0x18;
        public const byte CrcMode = (byte)'C';
        public const byte Sub = 0x1A;

        private const int MaxBlockRetries = 10;
        private const int MaxStartRetries = 20;
        private const int StartTimeoutMs = 3000;
        private const int AckTimeoutMs = 10000;

        /// <summary>
        /// Waits for the receiver's opening byte: 'C' asks for CRC-16 blocks, NAK for the older
        /// 8-bit checksum. A real receiver repeats this every few seconds until a sender answers,
        /// so the wait here is generous.
        /// </summary>
        public static async Task<bool> WaitForStartAsync(TransferIo io, CancellationToken ct)
        {
            for (int attempt = 0; attempt < MaxStartRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                int b = await io.ReadByteAsync(StartTimeoutMs, ct).ConfigureAwait(false);

                if (b == CrcMode)
                {
                    return true;
                }

                if (b == NAK)
                {
                    return false;
                }

                if (b == CAN)
                {
                    throw new FileTransferException("The receiver cancelled before the transfer started.");
                }
            }

            throw new FileTransferException(
                "No response from the receiver. Start the receive command on the far end first.");
        }

        /// <summary>
        /// Builds one full block: SOH/STX, the block number and its complement, the (already
        /// padded) payload, and a trailing checksum or CRC-16.
        /// </summary>
        public static byte[] BuildBlock(byte marker, byte blockNumber, byte[] payload, bool useCrc)
        {
            int tailLength = useCrc ? 2 : 1;
            byte[] block = new byte[3 + payload.Length + tailLength];

            block[0] = marker;
            block[1] = blockNumber;
            block[2] = (byte)(0xFF - blockNumber);
            Buffer.BlockCopy(payload, 0, block, 3, payload.Length);

            if (useCrc)
            {
                ushort crc = Crc16.Compute(payload, 0, payload.Length);
                block[^2] = (byte)(crc >> 8);
                block[^1] = (byte)crc;
            }
            else
            {
                byte sum = 0;
                for (int i = 0; i < payload.Length; i++)
                {
                    sum += payload[i];
                }

                block[^1] = sum;
            }

            return block;
        }

        /// <summary>Sends one block, retrying on NAK or silence, and giving up after too many tries.</summary>
        public static async Task SendBlockAsync(TransferIo io, byte[] block, CancellationToken ct)
        {
            for (int attempt = 0; attempt < MaxBlockRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                io.Write(block);

                int response = await io.ReadByteAsync(AckTimeoutMs, ct).ConfigureAwait(false);
                if (response == ACK)
                {
                    return;
                }

                if (response == CAN)
                {
                    throw new FileTransferException("The receiver cancelled the transfer.");
                }

                // NAK, garbage, or a timeout (-1): the receiver missed the block. Resend it.
            }

            throw new FileTransferException("The receiver kept rejecting a block; giving up.");
        }

        /// <summary>Signals end of file and waits for the last ACK — some receivers NAK the first EOT.</summary>
        public static async Task SendEotAsync(TransferIo io, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                io.Write(new[] { EOT });

                int response = await io.ReadByteAsync(AckTimeoutMs, ct).ConfigureAwait(false);
                if (response == ACK)
                {
                    return;
                }

                if (response == CAN)
                {
                    throw new FileTransferException("The receiver cancelled the transfer.");
                }
            }

            throw new FileTransferException("The receiver never acknowledged end of file.");
        }

        /// <summary>Pads the tail block with Ctrl-Z, which a plain Xmodem/Ymodem receiver strips back off.</summary>
        public static byte[] Pad(byte[] data, int offset, int length, int blockSize)
        {
            if (length == blockSize)
            {
                byte[] exact = new byte[blockSize];
                Buffer.BlockCopy(data, offset, exact, 0, blockSize);
                return exact;
            }

            byte[] padded = new byte[blockSize];
            Buffer.BlockCopy(data, offset, padded, 0, length);
            for (int i = length; i < blockSize; i++)
            {
                padded[i] = Sub;
            }

            return padded;
        }
    }
}
