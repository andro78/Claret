using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Claret.Services;

string path = Path.GetTempFileName();
try
{
    byte[] contents = Enumerable.Range(0, 1025).Select(i => (byte)i).ToArray();
    await File.WriteAllBytesAsync(path, contents);
    var replies = Channel.CreateUnbounded<byte>();
    replies.Writer.TryWrite((byte)'C');
    int dataPackets = 0;
    int eots = 0;
    bool completed = false;
    long progress = -1;

    await YmodemSender.SendAsync(path, replies.Reader, packet =>
    {
        if (packet.Length == 1 && packet[0] == 4)
        {
            eots++;
            replies.Writer.TryWrite(eots == 1 ? (byte)0x15 : (byte)0x06);
            if (eots == 2) replies.Writer.TryWrite((byte)'C');
            return;
        }
        Check(packet.Length is 133 or 1029, "packet length");
        Check(packet[2] == (byte)~packet[1], "block complement");
        ushort crc = 0;
        foreach (byte value in packet.AsSpan(3, packet.Length - 5))
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        Check(packet[^2] == (byte)(crc >> 8) && packet[^1] == (byte)crc, "CRC");
        if (packet[0] == 1 && packet[1] == 0 && packet[3] != 0)
        {
            string header = Encoding.UTF8.GetString(packet, 3, 128);
            Check(header.StartsWith(Path.GetFileName(path) + "\0" + contents.Length + "\0"), "metadata");
            replies.Writer.TryWrite(0x06);
            replies.Writer.TryWrite((byte)'C');
        }
        else if (packet[0] == 2)
        {
            dataPackets++;
            int index = packet[1] - 1;
            Check(index is 0 or 1, "block number");
            int count = index == 0 ? 1024 : 1;
            Check(packet.AsSpan(3, count).SequenceEqual(contents.AsSpan(index * 1024, count)), "data");
            if (index == 1) Check(packet.AsSpan(4, 1023).ToArray().All(b => b == 0x1a), "padding");
            replies.Writer.TryWrite(dataPackets == 1 ? (byte)0x15 : (byte)0x06);
        }
        else
        {
            Check(packet[0] == 1 && packet[1] == 0 && packet.AsSpan(3, 128).ToArray().All(b => b == 0), "final header");
            completed = true;
            replies.Writer.TryWrite(0x06);
        }
    }, new InlineProgress(value => progress = value), CancellationToken.None);

    Check(completed && dataPackets == 3 && eots == 2 && progress == contents.Length, "handshake");
    Console.WriteLine("YMODEM packet, retry, EOF, and CRC checks passed.");
}
finally
{
    File.Delete(path);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("YMODEM test failed: " + message);
}

sealed class InlineProgress(Action<long> report) : IProgress<long>
{
    public void Report(long value) => report(value);
}
