using System;
using System.IO;
using System.Text.Json;

namespace XCENA_Terminal_Dev.Services
{
    /// <summary>Where the sidebar sits and how wide it is, remembered between runs.</summary>
    public sealed class WorkspaceLayout
    {
        public double SidebarWidth { get; set; } = 256;

        public bool SidebarOnRight { get; set; }
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
