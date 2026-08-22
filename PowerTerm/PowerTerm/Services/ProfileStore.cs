using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PowerTerm.Models;

namespace PowerTerm.Services
{
    /// <summary>
    /// Loads and persists <see cref="ConnectionProfile"/> entries as JSON under %APPDATA%.
    /// Writes are atomic (temp file + replace) so a crash mid-save cannot truncate the list.
    /// </summary>
    internal sealed class ProfileStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        public ObservableCollection<ConnectionProfile> Profiles { get; } = new();

        /// <summary>Set when the on-disk file could not be read, so the UI can surface it.</summary>
        public string? LoadError { get; private set; }

        public void Load()
        {
            LoadError = null;
            Profiles.Clear();

            string path = AppPaths.ProfilesFile;
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                List<ConnectionProfile>? loaded = JsonSerializer.Deserialize<List<ConnectionProfile>>(json, SerializerOptions);
                if (loaded is null)
                {
                    return;
                }

                foreach (ConnectionProfile profile in loaded.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
                {
                    Profiles.Add(profile);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                LoadError = ex.Message;
            }
        }

        public void Save()
        {
            string path = AppPaths.ProfilesFile;
            string temp = path + ".tmp";
            string json = JsonSerializer.Serialize(Profiles.ToList(), SerializerOptions);

            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>Inserts a new profile or replaces the existing one with the same <see cref="ConnectionProfile.Id"/>.</summary>
        public void AddOrUpdate(ConnectionProfile profile)
        {
            int index = IndexOf(profile.Id);
            if (index >= 0)
            {
                Profiles[index] = profile;
            }
            else
            {
                Profiles.Add(profile);
            }

            Resort();
            Save();
        }

        public void Remove(ConnectionProfile profile)
        {
            int index = IndexOf(profile.Id);
            if (index >= 0)
            {
                Profiles.RemoveAt(index);
                Save();
            }
        }

        private int IndexOf(string id)
        {
            for (int i = 0; i < Profiles.Count; i++)
            {
                if (string.Equals(Profiles[i].Id, id, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private void Resort()
        {
            List<ConnectionProfile> sorted = Profiles
                .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                int current = Profiles.IndexOf(sorted[i]);
                if (current != i)
                {
                    Profiles.Move(current, i);
                }
            }
        }
    }
}
