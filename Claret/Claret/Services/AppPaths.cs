using System;
using System.IO;
using System.Linq;

namespace Claret.Services
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
        /// What this app was called before, newest name first. A folder is read once, on a first
        /// run, so a rename does not look like losing every saved connection. There are two entries
        /// because there have been two renames, and someone may be upgrading from either one.
        /// </summary>
        private static readonly string[] FormerNames = { "PowerTerm", "XCENA Terminal" };

        private static string CreateDataDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(root, "Claret");

            bool fresh = HasNoSettings(dir);
            Directory.CreateDirectory(dir);

            if (fresh)
            {
                // Newest first, and CarryOver never overwrites: if both old folders exist, the
                // more recent one wins file by file and the older one only fills in gaps.
                foreach (string former in FormerNames)
                {
                    CarryOver(Path.Combine(root, former), dir);
                }
            }

            return dir;
        }

        /// <summary>
        /// Whether the folder holds no settings yet — which is not the same question as whether it
        /// exists. Something else may have made it first: an installer, a file-sync client, or a
        /// build that started once and was closed before it wrote anything. Keying the carry-over
        /// on existence alone means an empty folder like that quietly costs the user every saved
        /// connection, with no way to ask for them back short of copying files by hand.
        /// </summary>
        private static bool HasNoSettings(string dir)
        {
            try
            {
                return !Directory.Exists(dir) || !Directory.EnumerateFiles(dir, "*.json").Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable is not the same as empty. Carrying files in on top of settings that
                // are there but cannot be listed would be the one outcome worse than doing nothing.
                return false;
            }
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
