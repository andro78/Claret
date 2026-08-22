using System;
using System.Text;

namespace XCENA_Terminal_Dev.Services
{
    /// <summary>
    /// Turns a terminal byte stream into the text a person would read: UTF-8 decoded, with the
    /// escape sequences that paint colours, move the cursor and set the window title removed.
    /// <para>
    /// Both halves are stateful because both can be split between two reads — a Hangul syllable
    /// across its three bytes, an escape sequence across its parameters. Anything that scans the
    /// stream (a log, a trigger) needs the same treatment, so it lives here rather than in either.
    /// </para>
    /// </summary>
    internal sealed class TextStream
    {
        private const char Escape = (char)0x1B;
        private const char Bell = (char)0x07;

        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

        private Filter _state = Filter.Text;

        /// <summary>Where the escape-sequence filter currently is.</summary>
        private enum Filter
        {
            Text,

            /// <summary>Just saw ESC; the next byte says what kind of sequence this is.</summary>
            Escape,

            /// <summary>Inside a CSI sequence: runs until a byte in 0x40..0x7E.</summary>
            Csi,

            /// <summary>Inside an OSC (window title and friends): runs until BEL or ESC.</summary>
            Osc,
        }

        /// <summary>
        /// Decodes and filters one chunk. Carriage returns are dropped where they precede a line
        /// feed and become a line break otherwise: a bare CR is a redraw in place, and gluing its
        /// states together would read as one corrupt line.
        /// </summary>
        public string Read(byte[] data)
        {
            if (data.Length == 0)
            {
                return string.Empty;
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

            return text.ToString();
        }
    }
}
