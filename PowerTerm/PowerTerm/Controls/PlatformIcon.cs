using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using PowerTerm.Models;

namespace PowerTerm.Controls
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

        // The outer shell, in grid units: a trapezoid whose top edge is wider than its bottom.
        private const double ShellLeft = 0.6;
        private const double ShellTop = 4.8;
        private const double ShellRight = 21.4;
        private const double ShellBottomLeft = 4.0;
        private const double ShellBottom = 17.2;
        private const double ShellBottomRight = 18.0;
        private const double ShellRadius = 2.2;

        /// <summary>
        /// How thick the shell's outline is, in device pixels — not grid units. The rail draws this
        /// icon larger than the tab strip does, and a stroke that scaled with the box would come out
        /// heavier in one place than the other, and heavier than the symbol-font icons beside it.
        /// Keeping it in pixels is what makes every line on the rail the same weight.
        /// </summary>
        private const double ShellStroke = 1.2;

        /// <summary>Pin diameter, in device pixels for the same reason.</summary>
        private const double PinSize = 1.8;

        /// <summary>
        /// A serial console. Nothing identifies the far end of a cable, so the icon says what the
        /// link is instead of what is on it — and matches the rail icon that opened it.
        /// </summary>
        public static IconSource Serial() => new PathIconSource { Data = SerialGeometry(16) };

        /// <summary>
        /// The face of a DE-9 connector: a rounded trapezoid shell with nine pins, five over four.
        /// Even-odd, so the pair of outlines reads as a stroke and the pins inside the hole come
        /// back filled.
        /// <para>
        /// One set of coordinates, scaled to whatever box the caller has — the rail can afford more
        /// room than a tab strip, and two hand-kept copies of the same drawing would drift. The
        /// inner outline is derived from the outer one and the stroke rather than written down, so
        /// the line cannot end up thicker at the corners than along the edges.
        /// </para>
        /// </summary>
        public static Geometry SerialGeometry(double size)
        {
            double scale = size / SerialGrid;

            // The stroke and the pins are given in pixels, so convert them back to grid units.
            double stroke = ShellStroke / scale;
            double pin = PinSize / (2 * scale);

            // The sides lean, so insetting them by the stroke means moving further than the stroke
            // horizontally — the inset has to be perpendicular to the edge, not level with it.
            double lean = (ShellBottomLeft - ShellLeft) / (ShellBottom - ShellTop);
            double sideways = stroke * Math.Sqrt(1 + (lean * lean));

            double innerTop = ShellTop + stroke;
            double innerBottom = ShellBottom - stroke;

            var shell = new GeometryGroup { FillRule = FillRule.EvenOdd };

            shell.Children.Add(Shell(
                ShellLeft, ShellTop, ShellRight,
                ShellBottomRight, ShellBottom, ShellBottomLeft,
                ShellRadius, scale));

            shell.Children.Add(Shell(
                LeftEdgeAt(innerTop, lean) + sideways,
                innerTop,
                RightEdgeAt(innerTop, lean) - sideways,
                RightEdgeAt(innerBottom, lean) - sideways,
                innerBottom,
                LeftEdgeAt(innerBottom, lean) + sideways,
                // Concentric corners: a radius short by exactly the stroke keeps the ring even.
                Math.Max(0.3, ShellRadius - stroke),
                scale));

            foreach (double x in new[] { 5.4, 8.2, 11.0, 13.8, 16.6 })
            {
                shell.Children.Add(Pin(x, 9.5, pin, scale));
            }

            foreach (double x in new[] { 6.8, 9.6, 12.4, 15.2 })
            {
                shell.Children.Add(Pin(x, 12.7, pin, scale));
            }

            return shell;
        }

        private static double LeftEdgeAt(double y, double lean) => ShellLeft + (lean * (y - ShellTop));

        private static double RightEdgeAt(double y, double lean) => ShellRight - (lean * (y - ShellTop));

        private static EllipseGeometry Pin(double x, double y, double radius, double scale) => new()
        {
            Center = new Point(x * scale, y * scale),
            RadiusX = radius * scale,
            RadiusY = radius * scale,
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
            double length = Math.Sqrt((dx * dx) + (dy * dy));

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
