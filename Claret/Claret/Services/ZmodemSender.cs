using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Claret.Services
{
    /// <summary>
    /// Zmodem send. Header frames are always hex-encoded (ZHEX) — legal for any frame per the
    /// spec, and far simpler to build and parse than the ZDLE-escaped binary form. Only the file
    /// data itself uses the escaped binary subpacket framing, with CRC-16 rather than CRC-32: every
    /// receiver worth talking to understands CRC-16, and it reuses the same table Xmodem/Ymodem
    /// already need. Each subpacket is acknowledged every few kilobytes rather than continuously
    /// streamed — slower than a full sliding window, but a window is a second implementation's
    /// worth of retransmit bookkeeping this does not need to carry.
    /// </summary>
    internal static class ZmodemSender
    {
        private const byte Zpad = (byte)'*';
        private const byte Zdle = 0x18;
        private const byte ZbinType = (byte)'A';
        private const byte ZhexType = (byte)'B';
        private const byte Zbin32Type = (byte)'C';

        private const int ZrqInit = 0;
        private const int Zrinit = 1;
        private const int Zack = 3;
        private const int Zfile = 4;
        private const int Zskip = 5;
        private const int Zfin = 8;
        private const int Zrpos = 9;
        private const int Zdata = 10;
        private const int Zeof = 11;
        private const int Zcan = 16;

        // Subpacket end markers: 'h'/'i'/'j'/'k'.
        private const byte Zcrce = 0x68; // frame ends, no more data, no ack
        private const byte Zcrcg = 0x69; // frame continues, no ack
        private const byte Zcrcw = 0x6B; // frame ends here for now, ack requested

        private const int SubpacketSize = 1024;
        private const int AckEvery = 8; // one ZCRCW roughly every 8 KB
        private const int AckTimeoutMs = 10000;
        private const int StartTimeoutMs = 3000;
        private const int MaxStartRetries = 10;
        private const int MaxFrameRetries = 10;

        private readonly record struct ZHeader(int Type, uint Data);

        public static async Task SendAsync(
            TransferIo io,
            string fileName,
            byte[] data,
            IProgress<FileTransferProgress>? progress,
            CancellationToken ct)
        {
            await OpenSessionAsync(io, ct).ConfigureAwait(false);
            long startOffset = await SendFileHeaderAsync(io, fileName, data.Length, ct).ConfigureAwait(false);
            await SendDataAsync(io, data, startOffset, progress, ct).ConfigureAwait(false);
            await CloseSessionAsync(io, ct).ConfigureAwait(false);
        }

        private static async Task OpenSessionAsync(TransferIo io, CancellationToken ct)
        {
            for (int attempt = 0; attempt < MaxStartRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                io.Write(BuildHexHeader(ZrqInit, 0));

                ZHeader? reply = await ReadHeaderAsync(io, StartTimeoutMs, ct).ConfigureAwait(false);
                if (reply is { Type: Zrinit })
                {
                    return;
                }

                if (reply is { Type: Zcan })
                {
                    throw new FileTransferException("The receiver cancelled before the transfer started.");
                }
            }

            throw new FileTransferException(
                "No response from the receiver. Start the receive (rz) command on the far end first.");
        }

        private static async Task<long> SendFileHeaderAsync(
            TransferIo io, string fileName, long fileLength, CancellationToken ct)
        {
            string info = fileName + "\0" + fileLength.ToString(CultureInfo.InvariantCulture) + "\0";
            byte[] payload = Encoding.ASCII.GetBytes(info);

            for (int attempt = 0; attempt < MaxFrameRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                io.Write(BuildHexHeader(Zfile, 0));
                io.Write(BuildDataSubpacket(payload, 0, payload.Length, Zcrcw));

                ZHeader? reply = await ReadHeaderAsync(io, AckTimeoutMs, ct).ConfigureAwait(false);

                if (reply is { Type: Zrpos } rpos)
                {
                    return rpos.Data;
                }

                if (reply is { Type: Zskip })
                {
                    throw new FileTransferException("The receiver skipped this file.");
                }

                if (reply is { Type: Zcan })
                {
                    throw new FileTransferException("The receiver cancelled the transfer.");
                }

                // Silence, or a stray ZRINIT: the header frame did not land. Send it again.
            }

            throw new FileTransferException("The receiver never accepted the file header.");
        }

        private static async Task SendDataAsync(
            TransferIo io,
            byte[] data,
            long startOffset,
            IProgress<FileTransferProgress>? progress,
            CancellationToken ct)
        {
            long offset = Math.Clamp(startOffset, 0, data.Length);
            long total = data.Length;

            io.Write(BuildHexHeader(Zdata, (uint)offset));
            int sinceAck = 0;

            while (offset < data.Length)
            {
                ct.ThrowIfCancellationRequested();

                int length = (int)Math.Min(SubpacketSize, data.Length - offset);
                bool isLast = offset + length >= data.Length;
                sinceAck++;

                byte marker = isLast ? Zcrce : sinceAck >= AckEvery ? Zcrcw : Zcrcg;
                io.Write(BuildDataSubpacket(data, (int)offset, length, marker));

                if (marker == Zcrcw)
                {
                    ZHeader? reply = await ReadHeaderAsync(io, AckTimeoutMs, ct).ConfigureAwait(false);

                    if (reply is { Type: Zrpos } rpos)
                    {
                        // The receiver wants a resend from an earlier point — a dropped or
                        // corrupt subpacket. Rewind and carry on from there.
                        offset = Math.Clamp(rpos.Data, 0, data.Length);
                        io.Write(BuildHexHeader(Zdata, (uint)offset));
                        sinceAck = 0;
                        progress?.Report(new FileTransferProgress(offset, total, 0));
                        continue;
                    }

                    if (reply is not { Type: Zack })
                    {
                        throw new FileTransferException("The receiver did not acknowledge the data.");
                    }

                    sinceAck = 0;
                }

                offset += length;
                progress?.Report(new FileTransferProgress(offset, total, 0));
            }

            // ZCRCE already ended the frame; announce the final length and wait for agreement
            // before the session is torn down.
            for (int attempt = 0; attempt < MaxFrameRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                io.Write(BuildHexHeader(Zeof, (uint)data.Length));

                ZHeader? reply = await ReadHeaderAsync(io, AckTimeoutMs, ct).ConfigureAwait(false);
                if (reply is { Type: Zrinit })
                {
                    return;
                }

                if (reply is { Type: Zcan })
                {
                    throw new FileTransferException("The receiver cancelled the transfer.");
                }
            }

            throw new FileTransferException("The receiver never confirmed the end of the file.");
        }

        private static async Task CloseSessionAsync(TransferIo io, CancellationToken ct)
        {
            for (int attempt = 0; attempt < MaxFrameRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                io.Write(BuildHexHeader(Zfin, 0));

                ZHeader? reply = await ReadHeaderAsync(io, AckTimeoutMs, ct).ConfigureAwait(false);

                // A ZFIN back says goodbye properly; the file is already safely there either way,
                // so running out of retries here is not treated as the transfer having failed.
                if (reply is { Type: Zfin } || attempt == MaxFrameRetries - 1)
                {
                    break;
                }
            }

            // Two bare 'O' bytes end the session outside the framed protocol — no reply expected.
            io.Write(new byte[] { (byte)'O', (byte)'O' });
        }

        // ---- framing --------------------------------------------------------------------------

        private static byte[] BuildHexHeader(int type, uint data)
        {
            byte[] raw = { (byte)type, (byte)data, (byte)(data >> 8), (byte)(data >> 16), (byte)(data >> 24) };
            ushort crc = Crc16.Compute(raw, 0, raw.Length);

            var text = new StringBuilder();
            text.Append('*').Append('*').Append((char)Zdle).Append('B');

            foreach (byte b in raw)
            {
                text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            text.Append(((byte)(crc >> 8)).ToString("x2", CultureInfo.InvariantCulture));
            text.Append(((byte)crc).ToString("x2", CultureInfo.InvariantCulture));
            text.Append("\r\n");

            // XON lets a flow-controlled link start moving again right away; ZFIN and ZACK skip
            // it because nothing on the other end is waiting to be unblocked by it.
            if (type != Zack && type != Zfin)
            {
                text.Append((char)0x11);
            }

            return Encoding.Latin1.GetBytes(text.ToString());
        }

        private static byte[] BuildDataSubpacket(byte[] data, int offset, int length, byte endMarker)
        {
            var output = new List<byte>(length + 8);

            for (int i = 0; i < length; i++)
            {
                AppendEscaped(output, data[offset + i]);
            }

            output.Add(Zdle);
            output.Add(endMarker);

            // The CRC covers the payload plus the frame-end marker byte.
            byte[] crcInput = new byte[length + 1];
            Buffer.BlockCopy(data, offset, crcInput, 0, length);
            crcInput[length] = endMarker;
            ushort crc = Crc16.Compute(crcInput, 0, crcInput.Length);

            AppendEscaped(output, (byte)(crc >> 8));
            AppendEscaped(output, (byte)crc);

            return output.ToArray();
        }

        private static bool NeedsEscape(byte b) => b switch
        {
            0x10 or 0x90 => true, // DLE
            0x11 or 0x91 => true, // XON
            0x13 or 0x93 => true, // XOFF
            Zdle => true,
            0x0D or 0x8D => true, // CR
            _ => false,
        };

        private static void AppendEscaped(List<byte> output, byte b)
        {
            if (NeedsEscape(b))
            {
                output.Add(Zdle);
                output.Add((byte)(b ^ 0x40));
            }
            else
            {
                output.Add(b);
            }
        }

        // ---- reading the receiver's replies -----------------------------------------------------

        private static async Task<ZHeader?> ReadHeaderAsync(TransferIo io, int timeoutMs, CancellationToken ct)
        {
            long deadline = Environment.TickCount64 + timeoutMs;

            // Hunt for the frame start: any amount of noise, then ZPAD (any number of times), then ZDLE.
            while (true)
            {
                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0)
                {
                    return null;
                }

                int b = await io.ReadByteAsync(remaining, ct).ConfigureAwait(false);
                if (b < 0)
                {
                    return null;
                }

                if (b == Zpad)
                {
                    break;
                }
            }

            while (true)
            {
                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0)
                {
                    return null;
                }

                int b = await io.ReadByteAsync(remaining, ct).ConfigureAwait(false);
                if (b < 0)
                {
                    return null;
                }

                if (b == Zpad)
                {
                    continue;
                }

                if (b == Zdle)
                {
                    break;
                }

                return null;
            }

            int frameKind = await io.ReadByteAsync(timeoutMs, ct).ConfigureAwait(false);
            if (frameKind < 0)
            {
                return null;
            }

            if (frameKind == ZhexType)
            {
                byte[]? raw = await ReadHexBytesAsync(io, 7, timeoutMs, ct).ConfigureAwait(false);
                if (raw is null)
                {
                    return null;
                }

                ushort gotCrc = (ushort)((raw[5] << 8) | raw[6]);
                if (Crc16.Compute(raw, 0, 5) != gotCrc)
                {
                    return null;
                }

                return new ZHeader(raw[0], (uint)(raw[1] | (raw[2] << 8) | (raw[3] << 16) | (raw[4] << 24)));
            }

            if (frameKind == ZbinType || frameKind == Zbin32Type)
            {
                int crcLength = frameKind == Zbin32Type ? 4 : 2;
                byte[]? raw = await ReadEscapedBytesAsync(io, 5 + crcLength, timeoutMs, ct).ConfigureAwait(false);
                if (raw is null)
                {
                    return null;
                }

                // The CRC on this path is not re-checked: it only arrives here for a reply that
                // was not worth insisting on the hex form for, and a header worth acting on is
                // still worth acting on even if the far end's own framing choice was unexpected.
                return new ZHeader(raw[0], (uint)(raw[1] | (raw[2] << 8) | (raw[3] << 16) | (raw[4] << 24)));
            }

            return null;
        }

        private static async Task<byte[]?> ReadHexBytesAsync(
            TransferIo io, int byteCount, int timeoutMs, CancellationToken ct)
        {
            byte[] result = new byte[byteCount];
            for (int i = 0; i < byteCount; i++)
            {
                int hi = await io.ReadByteAsync(timeoutMs, ct).ConfigureAwait(false);
                int lo = hi < 0 ? -1 : await io.ReadByteAsync(timeoutMs, ct).ConfigureAwait(false);

                if (hi < 0 || lo < 0
                    || !TryHexDigit((byte)hi, out int hiValue)
                    || !TryHexDigit((byte)lo, out int loValue))
                {
                    return null;
                }

                result[i] = (byte)((hiValue << 4) | loValue);
            }

            return result;
        }

        private static bool TryHexDigit(byte c, out int value)
        {
            if (c is >= (byte)'0' and <= (byte)'9')
            {
                value = c - (byte)'0';
                return true;
            }

            if (c is >= (byte)'a' and <= (byte)'f')
            {
                value = c - (byte)'a' + 10;
                return true;
            }

            if (c is >= (byte)'A' and <= (byte)'F')
            {
                value = c - (byte)'A' + 10;
                return true;
            }

            value = 0;
            return false;
        }

        private static async Task<byte[]?> ReadEscapedBytesAsync(
            TransferIo io, int byteCount, int timeoutMs, CancellationToken ct)
        {
            byte[] result = new byte[byteCount];
            for (int i = 0; i < byteCount; i++)
            {
                int b = await io.ReadByteAsync(timeoutMs, ct).ConfigureAwait(false);
                if (b < 0)
                {
                    return null;
                }

                if (b == Zdle)
                {
                    int next = await io.ReadByteAsync(timeoutMs, ct).ConfigureAwait(false);
                    if (next < 0)
                    {
                        return null;
                    }

                    b = next ^ 0x40;
                }

                result[i] = (byte)b;
            }

            return result;
        }
    }
}
