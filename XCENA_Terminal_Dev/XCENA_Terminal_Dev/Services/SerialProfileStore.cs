using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Services
{
    /// <summary>Pinned serial consoles, persisted to serial.json beside the other stores.</summary>
    internal sealed class SerialProfileStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

        public ObservableCollection<SerialProfile> Profiles { get; } = new();

        public void Load()
        {
            try
            {
                if (!File.Exists(AppPaths.SerialProfilesFile))
                {
                    return;
                }

                string json = File.ReadAllText(AppPaths.SerialProfilesFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                List<SerialProfile>? loaded = JsonSerializer.Deserialize<List<SerialProfile>>(json, SerializerOptions);
                if (loaded is null)
                {
                    return;
                }

                Profiles.Clear();
                foreach (SerialProfile profile in loaded.Where(p => p.Settings.PortName.Length > 0))
                {
                    Profiles.Add(profile);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Start with none rather than failing to launch.
            }
        }

        public void Add(SerialProfile profile)
        {
            Profiles.Add(profile);
            Save();
        }

        public void Remove(SerialProfile profile)
        {
            Profiles.Remove(profile);
            Save();
        }

        /// <summary>Renames in place, keeping the order the list is already in.</summary>
        public void Rename(SerialProfile profile, string name)
        {
            int index = Profiles.IndexOf(profile);
            if (index < 0)
            {
                return;
            }

            SerialProfile renamed = profile.Clone();
            renamed.Name = name;

            // Replace rather than mutate: the list template binds one way, so a swap is what
            // redraws the row.
            Profiles[index] = renamed;
            Save();
        }

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Profiles, SerializerOptions);
                string temp = AppPaths.SerialProfilesFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.SerialProfilesFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-fatal: the pins simply will not survive a restart.
            }
        }
    }
}
