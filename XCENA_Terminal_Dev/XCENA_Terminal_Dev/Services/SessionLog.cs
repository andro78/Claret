using System;
using System.IO;
using System.Text;

namespace XCENA_Terminal_Dev.Services
{
    /// <summary>
    /// Writes what a session printed to a file, as text rather than as the raw stream. A console
    /// log is read weeks later by a person, so the escape sequences that paint colours, move the
    /// cursor and set the window title are dropped and the characters kept.
    /// </summary>
    internal sealed class SessionLog : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly TextStream _text = new();
        private readonly object _gate = new();

        private bool _disposed;

        private SessionLog(StreamWriter writer, string path)
        {
            _writer = writer;
            Path = path;
        }

        public string Path { get; }

        /// <summary>
        /// Opens a log, appending if the file is already there — reopening the same name should add
        /// to the record rather than quietly replace it.
        /// </summary>
        public static SessionLog Open(string path, string header)
        {
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            writer.Write($"{Environment.NewLine}==== {header} · {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===={Environment.NewLine}");
            return new SessionLog(writer, path);
        }

        /// <summary>Takes the bytes as they arrive and writes the readable text out of them.</summary>
        public void Write(byte[] data)
        {
            if (data.Length == 0)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                string text = _text.Read(data);
                if (text.Length == 0)
                {
                    return;
                }

                try
                {
                    _writer.Write(text);
                }
                catch (IOException)
                {
                    // A full or disconnected drive must not take the session down with it.
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                try
                {
                    _writer.Write($"{Environment.NewLine}==== closed · {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===={Environment.NewLine}");
                    _writer.Dispose();
                }
                catch (IOException)
                {
                    // Nothing useful left to do about a log that cannot be closed cleanly.
                }
            }
        }
    }
}
