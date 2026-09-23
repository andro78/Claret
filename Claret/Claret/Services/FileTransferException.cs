using System;

namespace Claret.Services
{
    /// <summary>
    /// A modem transfer failed in a way worth naming instead of surfacing as a raw timeout or
    /// index error — the receiver cancelled, never answered, or gave up after too many retries.
    /// </summary>
    internal sealed class FileTransferException : Exception
    {
        public FileTransferException(string message)
            : base(message)
        {
        }
    }
}
