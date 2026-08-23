using System;
using System.IO;
using System.Text;

namespace Claret.Services
{
    /// <summary>
    /// Writes what a session printed to a file, as text rather than as the raw stream. A console
    /// log is read weeks later by a person, so the escape sequences that paint colours, move the
    /// cursor and set the window title are dropped and the characters kept.
    /// </summary>
    internal sealed class SessionLog : IDisposable
    {
        private const char Escape = (char)0x1B;
        private const char Bell = (char)0x07;

        private readonly StreamWriter _writer;
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly object _gate = new();

        private Filter _state = Filter.Text;
        private bool _disposed;

        private SessionLog(StreamWriter writer, string path)
        {
            _writer = writer;
            Path = path;
        }

        /// <summary>Where the escape-sequence filter currently is.</summary>
        private enum Filter
        {
            Text,

            /// <summary>Just saw ESC; the next byte says what kind of sequence this is.</summary>
            Escape,

            /// <summary>Inside a CSI sequence: runs until a byte in 0x40..0x7E.</summary>
            Csi,

            /// <summary>Inside an OSC (window title and friends): runs until BEL or ESC \.</summary>
            Osc,
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

        /// <summary>
        /// Takes the bytes as they arrive. Decoding is stateful, because a UTF-8 character can be
        /// split across two reads, and so is the filter, because so can an escape sequence.
        /// </summary>
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

                char[] chars = new char[data.Length];
                int count = _decoder.GetChars(data, 0, data.Length, chars, 0);

                var text = new StringBuilder(count);

                for (int i = 0; i < count; i++)
                {
                    char c = chars[i];

                    switch (_state)
                    {
                        case Filter.Escape:
                            _state = c switch
                            {
                                '[' => Filter.Csi,
                                ']' => Filter.Osc,
                                // Two-character sequences end here; anything else was not one.
                                _ => Filter.Text,
                            };
                            continue;

                        case Filter.Csi:
                            if (c is >= '@' and <= '~')
                            {
                                _state = Filter.Text;
                            }

                            continue;

                        case Filter.Osc:
                            if (c == Bell || c == Escape)
                            {
                                _state = Filter.Text;
                            }

                            continue;
                    }

                    if (c == Escape)
                    {
                        _state = Filter.Escape;
                        continue;
                    }

                    switch (c)
                    {

                        case '\r':
                            // A CR before LF is just the line ending. A bare one is a redraw in
                            // place — a progress counter, usually — and becomes a line of its own,
                            // because gluing the states together reads as one corrupt line.
                            if (i + 1 < count && chars[i + 1] == '\n')
                            {
                                break;
                            }

                            if (text.Length > 0 && text[^1] != '\n')
                            {
                                text.Append('\n');
                            }

                            break;

                        case '\b':
                            if (text.Length > 0)
                            {
                                text.Length--;
                            }

                            break;

                        default:
                            if (c >= ' ' || c == '\n' || c == '\t')
                            {
                                text.Append(c);
                            }

                            break;
                    }
                }

                if (text.Length > 0)
                {
                    try
                    {
                        _writer.Write(text.ToString());
                    }
                    catch (IOException)
                    {
                        // A full or disconnected drive must not take the session down with it.
                    }
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
