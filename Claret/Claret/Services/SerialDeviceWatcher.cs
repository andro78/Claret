using System;
using System.Runtime.InteropServices;

namespace Claret.Services
{
    /// <summary>
    /// Tells the caller the instant a COM port interface appears or disappears — no timer, no
    /// polling. Windows already knows this the moment it happens (that is how Device Manager's own
    /// list updates live); <c>RegisterDeviceNotification</c> filtered to the serial port device
    /// interface class is the same mechanism, just asked for directly instead of through a WMI
    /// event query, which is itself a poll underneath.
    /// </summary>
    internal sealed class SerialDeviceWatcher : IDisposable
    {
        // GUID_DEVINTERFACE_COMPORT — every serial port, virtual COM ports included, registers
        // under this device interface class; nothing else does, so no further filtering is needed.
        private static readonly Guid ComPortInterfaceGuid = new("86E0D1E0-8089-11D0-9CE4-08003E301F73");

        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVTYP_DEVICEINTERFACE = 5;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct DevBroadcastDeviceInterface
        {
            public int Size;
            public int DeviceType;
            public int Reserved;
            public Guid ClassGuid;
        }

        private delegate IntPtr SubclassProc(
            IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr refData);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, IntPtr filter, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);

        [DllImport("Comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(
            IntPtr hWnd, SubclassProc callback, UIntPtr subclassId, IntPtr refData);

        [DllImport("Comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc callback, UIntPtr subclassId);

        [DllImport("Comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // Comctl32 keeps only a raw function pointer, so the delegate has to be pinned in a field —
        // otherwise the GC is free to collect it while Windows still holds the address.
        private readonly SubclassProc _proc;
        private readonly IntPtr _hwnd;
        private readonly UIntPtr _subclassId = (UIntPtr)0x434F4D31; // "COM1" as a tag, just needs to be unique to this class
        private IntPtr _notificationHandle;
        private bool _disposed;

        public SerialDeviceWatcher(IntPtr windowHandle)
        {
            _hwnd = windowHandle;
            _proc = WndProc;

            if (!SetWindowSubclass(_hwnd, _proc, _subclassId, IntPtr.Zero))
            {
                // Nothing fatal about missing the interrupt path — the caller still has its own
                // manual Refresh button. Silently living without live updates beats crashing over it.
                return;
            }

            var filter = new DevBroadcastDeviceInterface
            {
                DeviceType = DBT_DEVTYP_DEVICEINTERFACE,
                ClassGuid = ComPortInterfaceGuid,
            };
            filter.Size = Marshal.SizeOf<DevBroadcastDeviceInterface>();

            IntPtr buffer = Marshal.AllocHGlobal(filter.Size);
            try
            {
                Marshal.StructureToPtr(filter, buffer, false);
                _notificationHandle = RegisterDeviceNotification(_hwnd, buffer, DEVICE_NOTIFY_WINDOW_HANDLE);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Raised on the UI thread whenever a serial device interface arrives or leaves.</summary>
        public event EventHandler? DevicesChanged;

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, IntPtr refData)
        {
            if (msg == WM_DEVICECHANGE)
            {
                long code = wParam.ToInt64();
                if (code is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
                {
                    DevicesChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_notificationHandle != IntPtr.Zero)
            {
                UnregisterDeviceNotification(_notificationHandle);
                _notificationHandle = IntPtr.Zero;
            }

            RemoveWindowSubclass(_hwnd, _proc, _subclassId);
        }
    }
}
