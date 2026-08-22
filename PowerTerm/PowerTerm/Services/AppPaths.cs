using System;
using System.IO;

namespace PowerTerm.Services
{
    /// <summary>
    /// Roaming-profile storage locations. Deliberately not using ApplicationData.Current so the
    /// same paths work whether the app runs packaged (MSIX) or unpackaged.
    /// </summary>
    internal static class AppPaths
    {
        public static string DataDirectory { get; } = CreateDataDirectory();

        public static string ProfilesFile => Path.Combine(DataDirectory, "profiles.json");

        public static string KnownHostsFile => Path.Combine(DataDirectory, "known_hosts.json");

        public static string RecentFile => Path.Combine(DataDirectory, "recent.json");

        public static string AppearanceFile => Path.Combine(DataDirectory, "appearance.json");

        public static string LayoutFile => Path.Combine(DataDirectory, "layout.json");

        public static string HighlightsFile => Path.Combine(DataDirectory, "highlights.json");

        public static string SerialProfilesFile => Path.Combine(DataDirectory, "serial.json");

        /// <summary>
        /// What this app was called before. Its folder is read once, on a first run, so a rename
        /// does not look like losing every saved connection.
        /// </summary>
        private const string FormerName = "XCENA Terminal";

        private static string CreateDataDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(root, "PowerTerm");

            bool fresh = !Directory.Exists(dir);
            Directory.CreateDirectory(dir);

            if (fresh)
            {
                CarryOver(Path.Combine(root, FormerName), dir);
            }

            return dir;
        }

        /// <summary>
        /// Copies the settings the earlier build left behind — connections, host keys, colours — on
        /// the first run only. Copied rather than moved: the older build may still be installed, and
        /// taking its settings out from under it would be a rename that breaks the thing it renamed.
        /// Saved passwords come across intact because the encryption is keyed to this Windows
        /// account, not to the app's name.
        /// </summary>
        private static void CarryOver(string from, string to)
        {
            try
            {
                if (!Directory.Exists(from))
                {
                    return;
                }

                foreach (string file in Directory.EnumerateFiles(from, "*.json"))
                {
                    string target = Path.Combine(to, Path.GetFileName(file));
                    if (!File.Exists(target))
                    {
                        File.Copy(file, target);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Starting empty is a worse first run, not a broken one.
            }
        }
    }
}
