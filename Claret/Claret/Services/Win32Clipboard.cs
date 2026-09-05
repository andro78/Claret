using System;
using System.Runtime.InteropServices;

namespace Claret.Services
{
    /// <summary>
    /// Plain Win32 clipboard access. <see cref="Windows.ApplicationModel.DataTransfer.Clipboard"/>
    /// (the WinRT clipboard) has a well-known history of throwing or silently doing nothing in an
    /// unpackaged desktop process — exactly this app's deployment (see standalone-x64.pubxml) — and
    /// that is what copy and paste were built on. The classic OpenClipboard/SetClipboardData pair
    /// carries none of that baggage: it is the same API every Win32 text editor has used for thirty
    /// years, regardless of whether the caller has a package identity.
    /// </summary>
    internal static class Win32Clipboard
    {
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        /// <summary>
        /// Sets the clipboard to plain Unicode text. Windows allows one clipboard owner at a time,
        /// so another process holding it open makes this fail transiently — worth a retry, which the
        /// caller does.
        /// </summary>
        public static bool TrySetText(string text)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                return false;
            }

            IntPtr hGlobal = IntPtr.Zero;

            try
            {
                if (!EmptyClipboard())
                {
                    return false;
                }

                int bytes = (text.Length + 1) * 2;
                hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hGlobal == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr target = GlobalLock(hGlobal);
                if (target == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                    Marshal.WriteInt16(target, text.Length * 2, 0);
                }
                finally
                {
                    GlobalUnlock(hGlobal);
                }

                if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    return false;
                }

                // The clipboard now owns hGlobal; freeing it here would corrupt the next paste.
                hGlobal = IntPtr.Zero;
                return true;
            }
            finally
            {
                if (hGlobal != IntPtr.Zero)
                {
                    GlobalFree(hGlobal);
                }

                CloseClipboard();
            }
        }

        /// <summary>Reads plain Unicode text from the clipboard, or null if there is none.</summary>
        public static string? TryGetText()
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                return null;
            }

            if (!OpenClipboard(IntPtr.Zero))
            {
                return null;
            }

            try
            {
                IntPtr handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                IntPtr pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUni(pointer);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
    }
}
