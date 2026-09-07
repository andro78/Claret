using System;
using System.Collections.Generic;
using System.Text;

namespace Claret.Services
{
    /// <summary>
    /// Turns the byte stream a session prints into whole lines of readable text.
    ///
    /// Both halves of that are stateful and have to stay so between reads: a UTF-8 character can be
    /// split across two of them, and so can an escape sequence. What comes out is what a person
    /// would say was on the line — the sequences that paint colour, move the cursor and set the
    /// window title are dropped and the characters kept.
    ///
    /// <see cref="SessionLog"/> does the same filtering to write a file. This is deliberately a
    /// separate copy rather than a shared helper: the log wants a stream of text and does not care
    /// where the lines fall, while a detector only ever wants complete lines, and the two would
    /// have to be pulled apart again the moment either changed.
    /// </summary>
    internal sealed class OutputLines
    {
        private const char Escape = (char)0x1B;
        private const char Bell = (char)0x07;

        /// <summary>
        /// A line longer than this is cut. Nothing that arrives as one line of console output is
        /// this long, so past it the stream is binary, or a program is drawing without newlines —
        /// and either way holding on to it only grows a buffer nobody will read.
        /// </summary>
        private const int MaxLineLength = 4096;

        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _line = new();

        private Filter _state = Filter.Text;

        private enum Filter
        {
            Text,

            /// <summary>Just saw ESC; the next byte says what kind of sequence this is.</summary>
            Escape,

            Csi,
            Osc,
        }

        /// <summary>
        /// Feeds a chunk and returns the lines it completed. A line that has arrived but has no
        /// newline yet is held back — a prompt is a line the shell has not finished writing, and
        /// reporting it would fire on every keystroke echoed into it.
        /// </summary>
        public List<string> Feed(byte[] data)
        {
            var lines = new List<string>();
            if (data.Length == 0)
            {
                return lines;
            }

            char[] chars = new char[data.Length];
            int count = _decoder.GetChars(data, 0, data.Length, chars, 0);

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
                    case '\n':
                        Take(lines);
                        break;

                    case '\r':
                        // CR before LF is just the line ending; let the LF end it. A bare CR is a
                        // redraw in place — a progress counter — and ends a line of its own, or the
                        // states would run together into one line nobody wrote.
                        if (i + 1 < count && chars[i + 1] == '\n')
                        {
                            break;
                        }

                        Take(lines);
                        break;

                    case '\b':
                        if (_line.Length > 0)
                        {
                            _line.Length--;
                        }

                        break;

                    default:
                        if (c >= ' ' || c == '\t')
                        {
                            if (_line.Length < MaxLineLength)
                            {
                                _line.Append(c);
                            }
                        }

                        break;
                }
            }

            return lines;
        }

        /// <summary>Starts again, for a pane that is being reconnected.</summary>
        public void Reset()
        {
            _line.Clear();
            _state = Filter.Text;
        }

        private void Take(List<string> lines)
        {
            if (_line.Length > 0)
            {
                lines.Add(_line.ToString());
                _line.Clear();
            }
        }
    }
}
