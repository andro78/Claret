using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;

namespace Claret.Services
{
    /// <summary>A serial port as offered in the panel: the name to open, and something to read.</summary>
    internal sealed record SerialPortInfo(string PortName, string Description)
    {
        /// <summary>What the list shows: "COM7" with the device underneath it.</summary>
        public string Detail => Description.Length > 0 ? Description : "serial port";
    }

    /// <summary>
    /// Finds the serial ports. The names come from the port class itself; the descriptions come
    /// from WMI, which is slow enough to keep off the UI thread and optional enough that a failure
    /// just means plainer labels.
    /// </summary>
    internal static class SerialPortScanner
    {
        public static IReadOnlyList<SerialPortInfo> Scan()
        {
            string[] names;

            try
            {
                names = SerialPort.GetPortNames();
            }
            catch (Exception)
            {
                return Array.Empty<SerialPortInfo>();
            }

            Dictionary<string, string> descriptions = Describe();

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(NumberIn)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new SerialPortInfo(
                    name,
                    descriptions.TryGetValue(name, out string? text) ? text : string.Empty))
                .ToList();
        }

        /// <summary>
        /// Device names keyed by port. PnP entities carry the port in their caption, as in
        /// "Silicon Labs CP210x USB to UART Bridge (COM7)" — the part before it is the useful bit.
        /// </summary>
        private static Dictionary<string, string> Describe()
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var search = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

                foreach (ManagementBaseObject item in search.Get())
                {
                    if (item["Name"] is not string name)
                    {
                        continue;
                    }

                    int open = name.LastIndexOf('(');
                    int close = name.LastIndexOf(')');
                    if (open < 0 || close < open)
                    {
                        continue;
                    }

                    string port = name[(open + 1)..close].Trim();
                    string device = name[..open].Trim();

                    if (port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && device.Length > 0)
                    {
                        found[port] = device;
                    }
                }
            }
            catch (Exception)
            {
                // WMI can be disabled or slow to answer; the ports are still listed either way.
            }

            return found;
        }

        /// <summary>Sorts COM10 after COM9 rather than before it.</summary>
        private static int NumberIn(string portName) =>
            int.TryParse(new string(portName.Where(char.IsDigit).ToArray()), out int number) ? number : int.MaxValue;
    }
}
