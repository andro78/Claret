using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Controls
{
    /// <summary>
    /// Tab icons for what the far end is running. Emoji carry the OS families — a penguin says
    /// Linux at a glance — and Ubuntu gets its own mark because its ring of three is simple enough
    /// to draw. Anything unrecognised returns null so the caller keeps the plain globe.
    /// </summary>
    internal static class PlatformIcon
    {
        private const string EmojiFont = "Segoe UI Emoji";

        /// <summary>
        /// A serial console. Nothing identifies the far end of a cable, so the icon says what the
        /// link is instead of what is on it — and matches the rail icon that opened it.
        /// </summary>
        public static IconSource Serial() => new FontIconSource { Glyph = "", FontSize = 14 };

        /// <summary>An icon for the platform, or null when there is nothing better than the globe.</summary>
        public static IconSource? For(RemoteOs os) => os switch
        {
            RemoteOs.Ubuntu => UbuntuMark(),
            RemoteOs.Linux
                or RemoteOs.Debian
                or RemoteOs.Fedora
                or RemoteOs.RedHat
                or RemoteOs.Suse
                or RemoteOs.Arch
                or RemoteOs.Alpine
                or RemoteOs.Raspbian => Emoji("\U0001F427"),   // penguin
            RemoteOs.MacOs => Emoji("\U0001F34E"),             // red apple
            RemoteOs.Windows => Emoji("\U0001FA9F"),           // window
            RemoteOs.Bsd => Emoji("\U0001F608"),               // the daemon, near enough
            _ => null,
        };

        private static IconSource Emoji(string glyph) => new FontIconSource
        {
            Glyph = glyph,
            FontFamily = new FontFamily(EmojiFont),
            FontSize = 14,
        };

        /// <summary>
        /// The Ubuntu ring of three: an annulus (two circles, even-odd, so the middle stays hollow)
        /// with three dots set outside it, in Ubuntu orange.
        /// </summary>
        private static IconSource UbuntuMark()
        {
            var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
            geometry.Children.Add(Circle(8, 8, 4.6));
            geometry.Children.Add(Circle(8, 8, 3.1));

            // 90°, 210°, 330° — the same spacing as the logo, far enough out to stay separate.
            geometry.Children.Add(Circle(8.0, 2.4, 1.9));
            geometry.Children.Add(Circle(3.15, 10.8, 1.9));
            geometry.Children.Add(Circle(12.85, 10.8, 1.9));

            return new PathIconSource
            {
                Data = geometry,
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xE9, 0x54, 0x20)),
            };
        }

        private static EllipseGeometry Circle(double x, double y, double radius) => new()
        {
            Center = new Point(x, y),
            RadiusX = radius,
            RadiusY = radius,
        };
    }
}
