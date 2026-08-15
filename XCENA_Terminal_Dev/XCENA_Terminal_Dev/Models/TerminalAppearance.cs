using System;
using System.Globalization;
using System.Text.Json.Serialization;
using Windows.UI;

namespace XCENA_Terminal_Dev.Models
{
    /// <summary>
    /// User-chosen terminal colours. Stored as #RRGGBB strings so the file stays readable and can
    /// be handed straight to xterm.js.
    /// </summary>
    public sealed class TerminalAppearance
    {
        public const string DefaultBackground = "#0C0C0C";
        public const string DefaultForeground = "#E6E6E6";

        public string Background { get; set; } = DefaultBackground;

        public string Foreground { get; set; } = DefaultForeground;

        [JsonIgnore]
        public Color BackgroundColor => Parse(Background, DefaultBackground);

        [JsonIgnore]
        public Color ForegroundColor => Parse(Foreground, DefaultForeground);

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
