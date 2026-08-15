using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Services
{
    /// <summary>
    /// Most-recently-connected endpoints, newest first, stored in %APPDATA%. Holds no secrets.
    /// </summary>
    internal sealed class RecentStore
    {
        private const int MaxEntries = 20;

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly List<RecentConnection> _items = new();

        public IReadOnlyList<RecentConnection> Items => _items;

        public void Load()
        {
            _items.Clear();

            try
            {
                if (!File.Exists(AppPaths.RecentFile))
                {
                    return;
                }

                string json = File.ReadAllText(AppPaths.RecentFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                List<RecentConnection>? loaded =
                    JsonSerializer.Deserialize<List<RecentConnection>>(json, SerializerOptions);

                if (loaded is not null)
                {
                    _items.AddRange(loaded.OrderByDescending(r => r.LastUsed).Take(MaxEntries));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // History is a convenience; a damaged file just starts over.
            }
        }

        /// <summary>Moves this endpoint to the top of the history, replacing any earlier row for it.</summary>
        public void Record(ConnectionProfile profile)
        {
            if (string.IsNullOrWhiteSpace(profile.Host) || string.IsNullOrWhiteSpace(profile.Username))
            {
                return;
            }

            RecentConnection entry = RecentConnection.From(profile);
            _items.RemoveAll(r => string.Equals(r.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
            _items.Insert(0, entry);

            if (_items.Count > MaxEntries)
            {
                _items.RemoveRange(MaxEntries, _items.Count - MaxEntries);
            }

            Save();
        }

        public void Clear()
        {
            _items.Clear();
            Save();
        }

        private void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(_items, SerializerOptions);
                string temp = AppPaths.RecentFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.RecentFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-fatal: history simply will not persist across restarts.
            }
        }
    }
}
