using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using Windows.UI;

namespace XCENA_Terminal_Dev.Models
{
    /// <summary>
    /// User-chosen terminal colours: the background and default text colour, plus the sixteen ANSI
    /// colours a shell paints with. Stored as #RRGGBB strings so the file stays readable and can be
    /// handed straight to xterm.js.
    /// </summary>
    public sealed class TerminalAppearance
    {
        public const string DefaultBackground = "#0C0C0C";
        public const string DefaultForeground = "#E6E6E6";

        /// <summary>Matches the page default, and the size Ctrl+0 returns to.</summary>
        public const int DefaultFontSize = 14;

        public const int MinFontSize = 8;
        public const int MaxFontSize = 28;

        public string Background { get; set; } = DefaultBackground;

        public string Foreground { get; set; } = DefaultForeground;

        /// <summary>
        /// Which preset these colours came from, for the dialog to preselect. Only a label: what
        /// the terminal paints comes from the colours themselves.
        /// </summary>
        public string SchemeName { get; set; } = TerminalScheme.Default.Name;

        /// <summary>
        /// Terminal font family, or empty for automatic — which picks a face that advances a
        /// Hangul syllable across exactly two cells, so CJK columns line up. See
        /// <see cref="TerminalFont"/>.
        /// </summary>
        public string FontFamily { get; set; } = string.Empty;

        /// <summary>Terminal font size in points, clamped to what the page accepts.</summary>
        public int FontSize { get; set; } = DefaultFontSize;

        /// <summary>
        /// The sixteen ANSI colours in <see cref="TerminalScheme.AnsiNames"/> order. A file written
        /// by an older build has none, so the default palette stands in.
        /// </summary>
        public string[] Ansi { get; set; } = TerminalScheme.Default.Ansi.ToArray();

        [JsonIgnore]
        public Color BackgroundColor => Parse(Background, DefaultBackground);

        [JsonIgnore]
        public Color ForegroundColor => Parse(Foreground, DefaultForeground);

        /// <summary>
        /// The ANSI palette, padded out from the default if the stored one is short or missing, so
        /// callers never have to check the length.
        /// </summary>
        [JsonIgnore]
        public IReadOnlyList<string> AnsiColors
        {
            get
            {
                string[] fallback = TerminalScheme.Default.Ansi;
                var colours = new string[fallback.Length];

                for (int i = 0; i < colours.Length; i++)
                {
                    string? stored = Ansi is not null && i < Ansi.Length ? Ansi[i] : null;
                    colours[i] = TryParse(stored, out _) ? stored! : fallback[i];
                }

                return colours;
            }
        }

        /// <summary>Cursor colour follows the text colour, so it stays visible on any background.</summary>
        [JsonIgnore]
        public string Cursor => Foreground;

        /// <summary>Selection tint derived from the text colour at low opacity over the background.</summary>
        [JsonIgnore]
        public string Selection => ToHex(Blend(ForegroundColor, BackgroundColor, 0.30));

        public TerminalAppearance Clone() => new()
        {
            Background = Background,
            Foreground = Foreground,
            SchemeName = SchemeName,
            FontFamily = FontFamily,
            FontSize = SafeFontSize,
            Ansi = AnsiColors.ToArray(),
        };

        /// <summary>The stored size, or the default when a file carries something unusable.</summary>
        [JsonIgnore]
        public int SafeFontSize => FontSize is >= MinFontSize and <= MaxFontSize
            ? FontSize
            : DefaultFontSize;

        /// <summary>Everything a preset defines, ready to store and apply.</summary>
        public static TerminalAppearance FromScheme(TerminalScheme scheme) => new()
        {
            Background = scheme.Background,
            Foreground = scheme.Foreground,
            SchemeName = scheme.Name,
            Ansi = scheme.Ansi.ToArray(),
        };

        public static string ToHex(Color color) =>
            $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private static Color Parse(string value, string fallback)
        {
            if (TryParse(value, out Color parsed))
            {
                return parsed;
            }

            return TryParse(fallback, out Color safe) ? safe : Color.FromArgb(255, 0, 0, 0);
        }

        private static bool TryParse(string? value, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string text = value.Trim().TrimStart('#');
            if (text.Length != 6
                || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            {
                return false;
            }

            color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }

        private static Color Blend(Color a, Color b, double weightOfA) => Color.FromArgb(
            255,
            (byte)Math.Round(a.R * weightOfA + b.R * (1 - weightOfA)),
            (byte)Math.Round(a.G * weightOfA + b.G * (1 - weightOfA)),
            (byte)Math.Round(a.B * weightOfA + b.B * (1 - weightOfA)));
    }
}
