using System;
using System.Threading;
using System.Threading.Tasks;
using Claret.Models;

namespace Claret.Services
{
    /// <summary>
    /// Whatever the terminal is talking to. The view only moves bytes, so SSH and a serial port
    /// differ in how they open and what a resize means — not in anything the terminal has to know.
    /// Events are raised on background threads; callers marshal to the UI thread themselves.
    /// </summary>
    internal interface ITerminalLink : IDisposable
    {
        /// <summary>Raw bytes received. The array is owned by the handler.</summary>
        event EventHandler<byte[]>? OutputReceived;

        /// <summary>Raised exactly once. Null argument for a clean exit, otherwise the reason.</summary>
        event EventHandler<string?>? Closed;

        /// <summary>Informational lines to echo into the terminal.</summary>
        event EventHandler<string>? Notice;

        bool IsConnected { get; }

        long BytesReceived { get; }

        long BytesSent { get; }

        /// <summary>
        /// Opens the link and starts pumping. Throws on failure; the caller surfaces the message.
        /// The grid size matters to a PTY and means nothing to a serial port.
        /// </summary>
        Task ConnectAsync(uint columns, uint rows, CancellationToken cancellationToken);

        /// <summary>What is on the other end, if it can be worked out. Best-effort by design.</summary>
        Task<RemotePlatform> DetectPlatformAsync(CancellationToken cancellationToken);

        void Send(byte[] data);

        void SendText(string text);

        /// <summary>Tells the far end the window changed size. A no-op where there is no PTY.</summary>
        void Resize(uint columns, uint rows);

        /// <summary>Whether a break can be sent — a serial thing, and the reason this is asked.</summary>
        bool SupportsBreak { get; }

        /// <summary>
        /// Holds the line in the break state briefly. On a board console this is what drops into
        /// the boot loader or a kernel debugger, so it is never sent by accident.
        /// </summary>
        void SendBreak();

        /// <summary>
        /// Whether this link can carry a raw X/Y/Zmodem transfer — a serial thing. An SSH shell has
        /// SFTP for moving files, and layering a modem protocol over its PTY would fight the same
        /// channel the shell prompt is using.
        /// </summary>
        bool SupportsFileTransfer { get; }

        /// <summary>
        /// Sends <paramref name="path"/> using <paramref name="protocol"/>, taking over the raw byte
        /// stream for as long as it runs. <paramref name="baudRate"/>, when given, is applied for the
        /// transfer only and restored afterward — a board's bootloader often wants a different rate
        /// than the console it was dropped into.
        /// </summary>
        Task SendFileAsync(
            string path,
            FileTransferProtocol protocol,
            int? baudRate,
            IProgress<FileTransferProgress> progress,
            CancellationToken cancellationToken);
    }
}
