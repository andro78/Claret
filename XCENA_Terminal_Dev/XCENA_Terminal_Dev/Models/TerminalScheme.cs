using System;
using System.Collections.Generic;
using System.Linq;

namespace XCENA_Terminal_Dev.Models
{
    /// <summary>
    /// A ready-made terminal palette: the background, the default text colour, and the sixteen
    /// ANSI colours a shell actually paints with. Picking one of these is the quick way to a
    /// readable terminal; the colour picker is still there for anyone who wants to tune it.
    /// </summary>
    public sealed class TerminalScheme
    {
        /// <summary>The sixteen ANSI slots, in the order xterm.js names them.</summary>
        public static readonly string[] AnsiNames =
        {
            "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
            "brightBlack", "brightRed", "brightGreen", "brightYellow",
            "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
        };

        public required string Name { get; init; }

        public required string Background { get; init; }

        public required string Foreground { get; init; }

        /// <summary>Sixteen #rrggbb values in <see cref="AnsiNames"/> order.</summary>
        public required string[] Ansi { get; init; }

        /// <summary>
        /// The presets offered in the dialog. Campbell is first because it is what the terminal
        /// has always shipped with, so it stays the default and the "Defaults" button target.
        /// </summary>
        public static IReadOnlyList<TerminalScheme> All { get; } = new[]
        {
            new TerminalScheme
            {
                Name = "Campbell (default)",
                Background = "#0C0C0C",
                Foreground = "#E6E6E6",
                Ansi = new[]
                {
                    "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00",
                    "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
                    "#767676", "#E74856", "#16C60C", "#F9F1A5",
                    "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2",
                },
            },
            new TerminalScheme
            {
                Name = "Pastel",
                Background = "#1B1D21",
                Foreground = "#DFDFDF",
                Ansi = new[]
                {
                    "#4F4F4F", "#FF6C60", "#A8FF60", "#FFFFB6",
                    "#96CBFE", "#FF73FD", "#C6C5FE", "#EEEEEE",
                    "#7C7C7C", "#FFB6B0", "#CEFFAB", "#FFFFCB",
                    "#B5DCFE", "#FF9CFE", "#DFDFFE", "#FFFFFF",
                },
            },
            new TerminalScheme
            {
                Name = "One Half Dark",
                Background = "#282C34",
                Foreground = "#DCDFE4",
                Ansi = new[]
                {
                    "#282C34", "#E06C75", "#98C379", "#E5C07B",
                    "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4",
                    "#5A6374", "#E06C75", "#98C379", "#E5C07B",
                    "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4",
                },
            },
            new TerminalScheme
            {
                Name = "Nord",
                Background = "#2E3440",
                Foreground = "#D8DEE9",
                Ansi = new[]
                {
                    "#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B",
                    "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
                    "#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B",
                    "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4",
                },
            },
            new TerminalScheme
            {
                Name = "Dracula",
                Background = "#282A36",
                Foreground = "#F8F8F2",
                Ansi = new[]
                {
                    "#21222C", "#FF5555", "#50FA7B", "#F1FA8C",
                    "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
                    "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5",
                    "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF",
                },
            },
            new TerminalScheme
            {
                Name = "Gruvbox Dark",
                Background = "#282828",
                Foreground = "#EBDBB2",
                Ansi = new[]
                {
                    "#282828", "#CC241D", "#98971A", "#D79921",
                    "#458588", "#B16286", "#689D6A", "#A89984",
                    "#928374", "#FB4934", "#B8BB26", "#FABD2F",
                    "#83A598", "#D3869B", "#8EC07C", "#EBDBB2",
                },
            },
            new TerminalScheme
            {
                Name = "Tokyo Night",
                Background = "#1A1B26",
                Foreground = "#C0CAF5",
                Ansi = new[]
                {
                    "#15161E", "#F7768E", "#9ECE6A", "#E0AF68",
                    "#7AA2F7", "#BB9AF7", "#7DCFFF", "#A9B1D6",
                    "#414868", "#F7768E", "#9ECE6A", "#E0AF68",
                    "#7AA2F7", "#BB9AF7", "#7DCFFF", "#C0CAF5",
                },
            },
            new TerminalScheme
            {
                Name = "Solarized Dark",
                Background = "#002B36",
                Foreground = "#839496",
                Ansi = new[]
                {
                    "#073642", "#DC322F", "#859900", "#B58900",
                    "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
                    "#002B36", "#CB4B16", "#586E75", "#657B83",
                    "#839496", "#6C71C4", "#93A1A1", "#FDF6E3",
                },
            },
            new TerminalScheme
            {
                Name = "Solarized Light",
                Background = "#FDF6E3",
                Foreground = "#657B83",
                Ansi = new[]
                {
                    "#073642", "#DC322F", "#859900", "#B58900",
                    "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
                    "#002B36", "#CB4B16", "#586E75", "#657B83",
                    "#839496", "#6C71C4", "#93A1A1", "#FDF6E3",
                },
            },
        };

        public static TerminalScheme Default => All[0];

        /// <summary>
        /// The preset a set of colours came from, or null once they have been edited by hand. The
        /// name is only a hint: the colours decide, so a hand-edited palette never claims a preset.
        /// </summary>
        public static TerminalScheme? Match(string background, string foreground, IReadOnlyList<string> ansi)
        {
            return All.FirstOrDefault(scheme =>
                Same(scheme.Background, background)
                && Same(scheme.Foreground, foreground)
                && scheme.Ansi.Length == ansi.Count
                && scheme.Ansi.Zip(ansi, Same).All(equal => equal));
        }

        private static bool Same(string a, string b) =>
            string.Equals(a.Trim().TrimStart('#'), b.Trim().TrimStart('#'), StringComparison.OrdinalIgnoreCase);
    }
}
