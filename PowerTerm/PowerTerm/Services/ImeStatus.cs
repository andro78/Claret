using System;
using System.Runtime.InteropServices;

namespace PowerTerm.Services
{
    /// <summary>What the keyboard will type right now.</summary>
    internal enum ImeMode
    {
        /// <summary>Nothing to report: the app is not in the foreground.</summary>
        Unavailable,

        /// <summary>Latin letters — either an English layout, or a Korean IME in alphanumeric mode.</summary>
        Latin,

        /// <summary>The Korean IME is composing Hangul.</summary>
        Hangul,
    }

    /// <summary>
    /// Reads the Korean IME conversion mode for the foreground window.
    ///
    /// It has to be asked, not observed: the terminal lives in a WebView2, whose focused window
    /// belongs to another process, and Windows raises no event when the user presses Han/Yeong. So
    /// the default IME window for the focused control is queried with WM_IME_CONTROL, which works
    /// across processes, and the caller polls it.
    /// </summary>
    internal static class ImeStatus
    {
        private const uint WmImeControl = 0x0283;
        private const int ImcGetConversionMode = 0x0001;
        private const int ImeCmodeNative = 0x0001;   // set while the IME composes Hangul
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint GaRoot = 2;
        private const ushort LangKorean = 0x12;

        /// <summary>
        /// The current mode, or <see cref="ImeMode.Unavailable"/> when the window is not in front —
        /// the IME state then belongs to another app and saying anything about it would be a guess.
        /// </summary>
        public static ImeMode Read(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return ImeMode.Unavailable;
            }

            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || GetAncestor(foreground, GaRoot) != windowHandle)
            {
                return ImeMode.Unavailable;
            }

            var info = new GuiThreadInfo { cbSize = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            IntPtr focused = GetGUIThreadInfo(0, ref info) && info.hwndFocus != IntPtr.Zero
                ? info.hwndFocus
                : foreground;

            // A non-Korean layout has no Hangul mode to be in.
            uint thread = GetWindowThreadProcessId(focused, out _);
            IntPtr layout = GetKeyboardLayout(thread);
            if ((((ulong)layout) & 0x3FF) != LangKorean)
            {
                return ImeMode.Latin;
            }

            IntPtr ime = ImmGetDefaultIMEWnd(focused);
            if (ime == IntPtr.Zero)
            {
                return ImeMode.Latin;
            }

            // The IME window may belong to a hung process; never block the UI thread on it.
            if (SendMessageTimeout(
                    ime,
                    WmImeControl,
                    new IntPtr(ImcGetConversionMode),
                    IntPtr.Zero,
                    SmtoAbortIfHung,
                    200,
                    out IntPtr result) == IntPtr.Zero)
            {
                return ImeMode.Latin;
            }

            return ((long)result & ImeCmodeNative) != 0 ? ImeMode.Hangul : ImeMode.Latin;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuiThreadInfo
        {
            public uint cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public int rcCaretLeft;
            public int rcCaretTop;
            public int rcCaretRight;
            public int rcCaretBottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint thread);

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hwnd,
            uint message,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeoutMilliseconds,
            out IntPtr result);
    }
}
