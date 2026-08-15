using System;
using System.Runtime.InteropServices;

namespace XCENA_Terminal_Dev.Controls
{
    /// <summary>
    /// Cursor position in window client pixels. Needed because a tab drag reports no drop location,
    /// so the pane under the pointer has to be resolved by hand.
    /// </summary>
    internal static class NativeCursor
    {
        public static bool TryGetClientPosition(IntPtr window, out double x, out double y)
        {
            x = 0;
            y = 0;

            if (window == IntPtr.Zero || !GetCursorPos(out POINT point) || !ScreenToClient(window, ref point))
            {
                return false;
            }

            x = point.X;
            y = point.Y;
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // Classic DllImport: LibraryImport's generated marshalling would require AllowUnsafeBlocks.
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ScreenToClient(IntPtr window, ref POINT point);
    }
}
