using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace PowerTerm.Controls
{
    /// <summary>
    /// Measures a font family without rendering it, so the dialog can say whether a face is
    /// installed and whether it lines Hangul up with two Latin cells. Measurement is the only
    /// reliable test: WinUI silently substitutes a missing family, and the substitute is
    /// proportional, which is exactly what the monospace check catches.
    /// </summary>
    internal static class FontProbe
    {
        private const double ProbeSize = 64;

        private static readonly Dictionary<string, Metrics> Cache = new(StringComparer.OrdinalIgnoreCase);

        internal readonly record struct Metrics(bool Monospace, double Ratio)
        {
            /// <summary>The family resolved to something monospaced, so it exists as a terminal font.</summary>
            public bool Installed => Monospace;

            /// <summary>One Hangul syllable takes exactly two Latin advances.</summary>
            public bool AlignsHangul => Monospace && Math.Abs(Ratio - 2) < 0.02;
        }

        public static Metrics Measure(string family)
        {
            if (Cache.TryGetValue(family, out Metrics cached))
            {
                return cached;
            }

            double latin = Width(family, "M");
            double narrow = Width(family, "i");
            double wide = Width(family, "가");

            bool monospace = latin > 0 && Math.Abs(latin - narrow) < 0.05;
            var metrics = new Metrics(monospace, latin > 0 ? wide / latin : 0);

            Cache[family] = metrics;
            return metrics;
        }

        /// <summary>The family the automatic setting resolves to, or null when none qualifies.</summary>
        public static string? ResolveAutomatic(IReadOnlyList<string> order)
        {
            foreach (string family in order)
            {
                if (Measure(family).AlignsHangul)
                {
                    return family;
                }
            }

            return null;
        }

        private static double Width(string family, string text)
        {
            var block = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily(family),
                FontSize = ProbeSize,
                TextWrapping = TextWrapping.NoWrap,
            };

            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return block.DesiredSize.Width;
        }
    }
}
