using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Claret.Services
{
    /// <summary>Single-file YMODEM sender with 1K data blocks and CRC-16/XMODEM.</summary>
    internal static class YmodemSender
    {
        private const byte Soh = 0x01;
        private const byte Stx = 0x02;
        private const byte Eot = 0x04;
        private const byte Ack = 0x06;
        private const byte Nak = 0x15;
        private const byte Can = 0x18;
        private const byte CrcRequest = (byte)'C';
        private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(10);
        private const int Retries = 10;

        public static async Task SendAsync(string path, ChannelReader<byte> input,
            Action<byte[]> write, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] name = Encoding.UTF8.GetBytes(Path.GetFileName(path));
            byte[] size = Encoding.ASCII.GetBytes(file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (name.Length == 0 || name.Length + size.Length + 2 > 128)
                throw new InvalidOperationException("The file name is too long for a YMODEM header.");

            byte[] header = new byte[128];
            name.CopyTo(header, 0);
            size.CopyTo(header, name.Length + 1);
            bool started = false;
            try
            {
                await ExpectAsync(input, CrcRequest, cancellationToken);
                started = true;
                await SendBlockAsync(Packet(Soh, 0, header), input, write, cancellationToken);
                await ExpectAsync(input, CrcRequest, cancellationToken);

                byte[] data = new byte[1024];
                byte block = 1;
                long sent = 0;
                int count;
                while ((count = await file.ReadAsync(data, cancellationToken)) > 0)
                {
                    if (count < data.Length)
                        Array.Fill(data, (byte)0x1a, count, data.Length - count);
                    await SendBlockAsync(Packet(Stx, block, data), input, write, cancellationToken);
                    sent += count;
                    progress?.Report(sent);
                    block++;
                }

                // Many receivers NAK the first EOT and ACK the second; some ACK the first.
                bool eotAcked = false;
                for (int attempt = 0; attempt < Retries; attempt++)
                {
                    write(new[] { Eot });
                    byte? reply = await ReadReplyAsync(input, cancellationToken);
                    if (reply == Ack) { eotAcked = true; break; }
                    if (reply == Nak) continue;
                    if (reply == Can) throw new IOException("The receiver cancelled the transfer.");
                }
                if (!eotAcked)
                    throw new TimeoutException("The receiver did not acknowledge the end of file.");

                await ExpectAsync(input, CrcRequest, cancellationToken);
                await SendBlockAsync(Packet(Soh, 0, new byte[128]), input, write, cancellationToken);
            }
            catch
            {
                if (started)
                {
                    try { write(new[] { Can, Can }); }
                    catch (Exception) { /* The port may already be gone. */ }
                }
                throw;
            }
        }

        private static async Task SendBlockAsync(byte[] packet, ChannelReader<byte> input,
            Action<byte[]> write, CancellationToken token)
        {
            for (int attempt = 0; attempt < Retries; attempt++)
            {
                token.ThrowIfCancellationRequested();
                write(packet);
                byte? reply = await ReadReplyAsync(input, token);
                if (reply == Ack) return;
                if (reply == Can) throw new IOException("The receiver cancelled the transfer.");
            }
            throw new TimeoutException("The receiver did not acknowledge a YMODEM block.");
        }

        private static async Task ExpectAsync(ChannelReader<byte> input, byte expected, CancellationToken token)
        {
            for (int attempt = 0; attempt < Retries; attempt++)
            {
                byte? reply = await ReadReplyAsync(input, token);
                if (reply == expected) return;
                if (reply == Can) throw new IOException("The receiver cancelled the transfer.");
            }
            throw new TimeoutException("The receiver did not request CRC-mode YMODEM transfer.");
        }

        private static async Task<byte?> ReadReplyAsync(ChannelReader<byte> input, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ReplyTimeout);
            try
            {
                return await input.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return null;
            }
        }

        private static byte[] Packet(byte marker, byte number, byte[] data)
        {
            byte[] packet = new byte[data.Length + 5];
            packet[0] = marker;
            packet[1] = number;
            packet[2] = (byte)~number;
            data.CopyTo(packet, 3);
            ushort crc = 0;
            foreach (byte value in data)
            {
                crc ^= (ushort)(value << 8);
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
            packet[^2] = (byte)(crc >> 8);
            packet[^1] = (byte)crc;
            return packet;
        }
    }
}
