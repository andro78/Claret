using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Claret.Models;

namespace Claret.Services
{
    /// <summary>Workspace preferences remembered between runs.</summary>
    public sealed class WorkspaceLayout
    {
        public double SidebarWidth { get; set; } = 256;

        public bool SidebarOnRight { get; set; }

        /// <summary>Whether the Files tree lists files as well as folders (Options &gt; SFTP).</summary>
        public bool ShowRemoteFiles { get; set; }

        /// <summary>
        /// Put a mouse selection straight on the clipboard, the way PuTTY and X11 terminals do.
        /// On by default: it is what "select to copy" means, and the paste side has never needed a
        /// keyboard shortcut either.
        /// </summary>
        public bool CopyOnSelect { get; set; } = true;

        /// <summary>
        /// Prefix each line of serial output with the time it arrived. Off by default: it changes
        /// what the screen looks like, and only a console being read as a record wants it.
        /// </summary>
        public bool SerialTimestamps { get; set; }

        /// <summary>
        /// Hosts where answering an AI prompt automatically is refused outright, whatever the
        /// session asks for. Kept rather than the opposite list on purpose: the dangerous setting
        /// is the one that should need repeating, and the safe one the one that should stick.
        /// </summary>
        public List<string> AutoApproveBlockedHosts { get; set; } = new();

        /// <summary>
        /// Line settings the serial panel opens ports with. Remembered without the port name: the
        /// baud rate is a property of the board you talk to, the COM number is an accident of which
        /// USB socket the adapter went into.
        /// </summary>
        public SerialConnection Serial { get; set; } = new();

        /// <summary>
        /// Where downloads are saved without asking. Empty means "show the save dialog every time",
        /// which is the default because writing to a remembered folder should be a deliberate choice.
        /// </summary>
        public string DownloadFolder { get; set; } = string.Empty;
    }

    internal sealed class LayoutStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

        public WorkspaceLayout Current { get; private set; } = new();

        public void Load()
        {
            try
            {
                if (!File.Exists(AppPaths.LayoutFile))
                {
                    return;
                }

                string json = File.ReadAllText(AppPaths.LayoutFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                WorkspaceLayout? loaded = JsonSerializer.Deserialize<WorkspaceLayout>(json, SerializerOptions);
                if (loaded is not null)
                {
                    loaded.SidebarWidth = Math.Clamp(loaded.SidebarWidth, 160, 620);
                    Current = loaded;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Defaults already in Current.
            }
        }

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Current, SerializerOptions);
                string temp = AppPaths.LayoutFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.LayoutFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-fatal: the layout simply will not persist.
            }
        }
    }
}
