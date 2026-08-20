using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Serialization;

namespace XCENA_Terminal_Dev.Models
{
    /// <summary>
    /// One "colour this text when it appears" rule for the terminal. Matching happens in the page
    /// that hosts xterm.js; this is the shape the shell stores and hands over.
    /// </summary>
    public sealed class HighlightRule : INotifyPropertyChanged
    {
        /// <summary>
        /// Colours handed out by <see cref="AutoColor"/>, in order. All are light enough to read
        /// both as a block behind black text and as text on a dark terminal, and far enough apart
        /// in hue to tell two rules apart at a glance.
        /// </summary>
        private static readonly string[] AutoPalette =
        {
            "#F5C542", // amber
            "#4FC3F7", // sky
            "#81C784", // green
            "#FF8A65", // orange
            "#BA68C8", // violet
            "#4DD0E1", // cyan
            "#E57373", // red
            "#DCE775", // lime
            "#F06292", // pink
            "#A1887F", // taupe
        };

        private string _effectiveColor = AutoPalette[0];

        /// <summary>The literal text to colour. Matching is plain text, never a pattern language.</summary>
        public string Pattern { get; set; } = string.Empty;

        public bool IgnoreCase { get; set; } = true;

        /// <summary>The colour the user picked, as #rrggbb. Ignored while <see cref="AutoColor"/> is set.</summary>
        public string Color { get; set; } = AutoPalette[0];

        /// <summary>
        /// Let the app choose the colour: each auto rule takes the next unused palette entry, so
        /// adding rules never means picking colours that clash with the ones already in the list.
        /// Off by default so rules saved by an older build keep the colour they were given.
        /// </summary>
        public bool AutoColor { get; set; }

        /// <summary>
        /// Colour the characters instead of painting a block behind them. Off by default, which is
        /// the block highlight rules have always had.
        /// </summary>
        public bool TextOnly { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>
        /// The colour actually painted: <see cref="Color"/>, or the assigned palette entry when
        /// <see cref="AutoColor"/> is set. Written by <see cref="ResolveColors"/>, never persisted.
        /// </summary>
        [JsonIgnore]
        public string EffectiveColor
        {
            get => _effectiveColor;
            private set
            {
                if (_effectiveColor == value)
                {
                    return;
                }

                _effectiveColor = value;
                Raise(nameof(EffectiveColor));
                Raise(nameof(SwatchFill));
            }
        }

        /// <summary>
        /// Fill for the swatch in the rule list: the colour for a block rule, nothing for a
        /// text-only one, which shows as an outline instead so the two read differently.
        /// </summary>
        [JsonIgnore]
        public string SwatchFill => TextOnly ? string.Empty : EffectiveColor;

        [JsonIgnore]
        public bool IsUsable => Enabled && Pattern.Length > 0;

        /// <summary>Short description for the rule list.</summary>
        [JsonIgnore]
        public string Summary
        {
            get
            {
                string casing = IgnoreCase ? "any case" : "exact case";
                string paint = TextOnly ? "text" : "block";
                string colour = AutoColor ? " · auto colour" : string.Empty;
                return $"{casing} · {paint}{colour}";
            }
        }

        /// <summary>The swatch shown for a rule that has not been resolved yet.</summary>
        public static string FirstAutoColor => AutoPalette[0];

        public event PropertyChangedEventHandler? PropertyChanged;

        public HighlightRule Clone()
        {
            var copy = new HighlightRule
            {
                Pattern = Pattern,
                IgnoreCase = IgnoreCase,
                Color = Color,
                AutoColor = AutoColor,
                TextOnly = TextOnly,
                Enabled = Enabled,
            };

            copy.EffectiveColor = EffectiveColor;
            return copy;
        }

        /// <summary>
        /// Fills in <see cref="EffectiveColor"/> for a whole list. Auto rules walk the palette in
        /// list order and skip anything a hand-picked rule already uses, so the colours stay
        /// distinct; once the palette runs out it wraps round.
        /// </summary>
        public static void ResolveColors(IEnumerable<HighlightRule> rules)
        {
            List<HighlightRule> all = rules.ToList();

            var taken = new HashSet<string>(
                all.Where(rule => !rule.AutoColor).Select(rule => Normalize(rule.Color)),
                StringComparer.Ordinal);

            int next = 0;
            foreach (HighlightRule rule in all)
            {
                if (!rule.AutoColor)
                {
                    rule.EffectiveColor = rule.Color;
                    continue;
                }

                rule.EffectiveColor = TakeAutoColor(taken, ref next);
            }
        }

        /// <summary>
        /// The colour an auto rule would end up with, for the preview in the dialog. Pass the rule
        /// being edited so it claims its own slot rather than the one after it; null when adding.
        /// </summary>
        public static string PreviewAutoColor(IEnumerable<HighlightRule> rules, HighlightRule? editing)
        {
            List<HighlightRule> all = rules.ToList();

            var taken = new HashSet<string>(
                all.Where(rule => !rule.AutoColor && !ReferenceEquals(rule, editing))
                   .Select(rule => Normalize(rule.Color)),
                StringComparer.Ordinal);

            int next = 0;
            foreach (HighlightRule rule in all)
            {
                if (ReferenceEquals(rule, editing))
                {
                    return TakeAutoColor(taken, ref next);
                }

                if (rule.AutoColor)
                {
                    _ = TakeAutoColor(taken, ref next);
                }
            }

            // Not in the list: a new rule, which lands after everything else.
            return TakeAutoColor(taken, ref next);
        }

        /// <summary>Next palette entry no hand-picked rule is using, wrapping when all are taken.</summary>
        private static string TakeAutoColor(HashSet<string> taken, ref int next)
        {
            for (int step = 0; step < AutoPalette.Length; step++)
            {
                string candidate = AutoPalette[(next + step) % AutoPalette.Length];
                if (!taken.Contains(Normalize(candidate)))
                {
                    next += step + 1;
                    return candidate;
                }
            }

            string wrapped = AutoPalette[next % AutoPalette.Length];
            next++;
            return wrapped;
        }

        private static string Normalize(string color) => color.Trim().TrimStart('#').ToUpperInvariant();

        private void Raise(string property) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

        /// <summary>Rejects a pattern the terminal could not use.</summary>
        public static string? Validate(string pattern) =>
            string.IsNullOrEmpty(pattern) ? "Enter the text to highlight." : null;
    }
}
