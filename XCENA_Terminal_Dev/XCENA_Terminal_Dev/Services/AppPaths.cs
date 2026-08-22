using System;
using System.IO;

namespace XCENA_Terminal_Dev.Services
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

        public static string PromptsFile => Path.Combine(DataDirectory, "prompts.json");

        public static string TriggersFile => Path.Combine(DataDirectory, "triggers.json");

        /// <summary>
        /// Where a recording goes when nobody chose a folder — a trigger that starts a log has to
        /// pick the file itself, since the thing it was waiting for will not wait for a dialog.
        /// Created on demand rather than at startup.
        /// </summary>
        public static string LogDirectory
        {
            get
            {
                string dir = Path.Combine(DataDirectory, "logs");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string CreateDataDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(root, "XCENA Terminal");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
