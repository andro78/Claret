namespace Claret.Services
{
    /// <summary>
    /// CRC-16/XMODEM (poly 0x1021, init 0, no reflect, no xorout). Xmodem, Ymodem, and Zmodem all
    /// check their blocks with this same polynomial — one table, three protocols.
    /// </summary>
    internal static class Crc16
    {
        public static ushort Compute(byte[] data, int offset, int count)
        {
            ushort crc = 0;

            for (int i = 0; i < count; i++)
            {
                crc ^= (ushort)(data[offset + i] << 8);

                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
                }
            }

            return crc;
        }
    }
}
