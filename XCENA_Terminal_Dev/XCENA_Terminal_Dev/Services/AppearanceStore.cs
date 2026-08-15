using System;
using System.IO;
using System.Text.Json;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Services
{
    /// <summary>Persists the terminal colour choices in %APPDATA%.</summary>
    internal sealed class AppearanceStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

        public TerminalAppearance Current { get; private set; } = new();

        public void Load()
        {
            try
            {
                if (!File.Exists(AppPaths.AppearanceFile))
                {
                    return;
                }

                string json = File.ReadAllText(AppPaths.AppearanceFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                TerminalAppearance? loaded = JsonSerializer.Deserialize<TerminalAppearance>(json, SerializerOptions);
                if (loaded is not null)
                {
                    Current = loaded;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Fall back to the defaults already in Current.
            }
        }

        public void Save(TerminalAppearance appearance)
        {
            Current = appearance;

            try
            {
                string json = JsonSerializer.Serialize(appearance, SerializerOptions);
                string temp = AppPaths.AppearanceFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.AppearanceFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-fatal: colours simply will not persist across restarts.
            }
        }
    }
}
