using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace XCENA_Terminal_Dev.Controls
{
    /// <summary>
    /// The app's burgundy accent, for the surfaces that are painted from code (pane outlines,
    /// dividers, drop hints). XAML picks the same colour up from the AccentFill* theme brushes in
    /// App.xaml; both are kept here in one place so they cannot drift apart.
    /// </summary>
    internal static class AppAccent
    {
        public static readonly Color Default = Color.FromArgb(0xFF, 0x8C, 0x23, 0x32);

        public static readonly Color Hover = Color.FromArgb(0xFF, 0xA8, 0x2C, 0x3E);

        /// <summary>Translucent fill for the drag-and-drop preview.</summary>
        public static readonly Color DropFill = Color.FromArgb(0x55, 0x8C, 0x23, 0x32);

        public static SolidColorBrush Brush() => new(Default);

        public static SolidColorBrush HoverBrush() => new(Hover);

        public static SolidColorBrush DropFillBrush() => new(DropFill);
    }
}
