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

        /// <summary>The box the serial connector is drawn in before scaling. Square, ink centred.</summary>
        private const double SerialGrid = 22;

        /// <summary>
        /// A serial console. Nothing identifies the far end of a cable, so the icon says what the
        /// link is instead of what is on it — and matches the rail icon that opened it.
        /// </summary>
        public static IconSource Serial() => new PathIconSource { Data = SerialGeometry(16) };

        /// <summary>
        /// The face of a DE-9 connector: a rounded trapezoid shell with nine pins, five over four.
        /// Even-odd, so the two outlines read as a stroke and the pins inside the hole come back
        /// filled.
        /// <para>
        /// One set of coordinates, scaled to whatever box the caller has — the rail can afford more
        /// room than a tab strip, and two hand-kept copies of the same drawing would drift.
        /// </para>
        /// </summary>
        public static Geometry SerialGeometry(double size)
        {
            double s = size / SerialGrid;

            var shell = new GeometryGroup { FillRule = FillRule.EvenOdd };
            shell.Children.Add(Shell(0.6, 4.8, 21.4, 18.0, 17.2, 4.0, 2.2, s));
            shell.Children.Add(Shell(2.95, 6.6, 19.05, 16.64, 15.4, 5.37, 1.2, s));

            foreach (double x in new[] { 5.4, 8.2, 11.0, 13.8, 16.6 })
            {
                shell.Children.Add(Pin(x, 9.5, s));
            }

            foreach (double x in new[] { 6.8, 9.6, 12.4, 15.2 })
            {
                shell.Children.Add(Pin(x, 12.7, s));
            }

            return shell;
        }

        private static EllipseGeometry Pin(double x, double y, double scale) => new()
        {
            Center = new Point(x * scale, y * scale),
            RadiusX = scale,
            RadiusY = scale,
        };

        /// <summary>
        /// A trapezoid with rounded corners, top edge wider than the bottom. Each corner is one
        /// quadratic through the sharp point, which at icon size is indistinguishable from an arc.
        /// </summary>
        private static PathGeometry Shell(
            double topLeft,
            double top,
            double topRight,
            double bottomRight,
            double bottom,
            double bottomLeft,
            double radius,
            double scale)
        {
            Point[] corners =
            {
                new(topLeft * scale, top * scale),
                new(topRight * scale, top * scale),
                new(bottomRight * scale, bottom * scale),
                new(bottomLeft * scale, bottom * scale),
            };

            double r = radius * scale;

            var figure = new PathFigure
            {
                // Start on the top edge, past the top-left corner's curve, so the loop below can
                // close back onto exactly this point.
                StartPoint = Along(corners[0], corners[1], r),
                IsClosed = true,
                IsFilled = true,
            };

            for (int i = 1; i <= corners.Length; i++)
            {
                Point corner = corners[i % corners.Length];
                Point previous = corners[i - 1];
                Point next = corners[(i + 1) % corners.Length];

                figure.Segments.Add(new LineSegment { Point = Along(corner, previous, r) });
                figure.Segments.Add(new QuadraticBezierSegment
                {
                    Point1 = corner,
                    Point2 = Along(corner, next, r),
                });
            }

            var path = new PathGeometry();
            path.Figures.Add(figure);
            return path;
        }

        /// <summary>The point <paramref name="distance"/> from <paramref name="from"/> towards <paramref name="towards"/>.</summary>
        private static Point Along(Point from, Point towards, double distance)
        {
            double dx = towards.X - from.X;
            double dy = towards.Y - from.Y;
            double length = System.Math.Sqrt((dx * dx) + (dy * dy));

            return length <= distance
                ? towards
                : new Point(from.X + (dx / length * distance), from.Y + (dy / length * distance));
        }

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
