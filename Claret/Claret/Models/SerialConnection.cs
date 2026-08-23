using System;
using System.IO.Ports;

namespace Claret.Models
{
    /// <summary>
    /// One serial console: the port and the line settings to open it with. Defaults are 115200 8N1
    /// with no flow control, which is what board and BMC consoles almost always use.
    /// </summary>
    public sealed class SerialConnection
    {
        public const int DefaultBaudRate = 115200;

        /// <summary>Baud rates offered in the panel, slowest first.</summary>
        public static readonly int[] CommonBaudRates =
        {
            9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600,
        };

        public string PortName { get; set; } = string.Empty;

        public int BaudRate { get; set; } = DefaultBaudRate;

        public int DataBits { get; set; } = 8;

        public Parity Parity { get; set; } = Parity.None;

        public StopBits StopBits { get; set; } = StopBits.One;

        /// <summary>
        /// Flow control. None by default: a console cable without the handshake lines wired will
        /// simply never send anything if RTS/CTS is expected, which looks like a dead port.
        /// </summary>
        public Handshake Handshake { get; set; } = Handshake.None;

        /// <summary>Short label for the tab, e.g. "COM7".</summary>
        public string DisplayName => PortName.Length > 0 ? PortName : "serial";

        /// <summary>Full label for the status bar, e.g. "COM7 · 115200 8N1".</summary>
        public string Summary => $"{DisplayName} · {BaudRate} {Format}";

        /// <summary>The line format in the usual shorthand, e.g. "8N1".</summary>
        public string Format => $"{DataBits}{ParityLetter}{StopBitsDigits}";

        private char ParityLetter => Parity switch
        {
            Parity.Even => 'E',
            Parity.Odd => 'O',
            Parity.Mark => 'M',
            Parity.Space => 'S',
            _ => 'N',
        };

        private string StopBitsDigits => StopBits switch
        {
            StopBits.Two => "2",
            StopBits.OnePointFive => "1.5",
            _ => "1",
        };

        public SerialConnection Clone() => new()
        {
            PortName = PortName,
            BaudRate = BaudRate,
            DataBits = DataBits,
            Parity = Parity,
            StopBits = StopBits,
            Handshake = Handshake,
        };
    }
}
