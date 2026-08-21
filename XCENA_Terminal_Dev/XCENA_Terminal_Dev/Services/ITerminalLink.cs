using System;
using System.Threading;
using System.Threading.Tasks;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Services
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
    }
}
