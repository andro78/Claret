using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using XCENA_Terminal_Dev.Models;

namespace XCENA_Terminal_Dev.Services
{
    /// <summary>Output triggers, persisted to triggers.json.</summary>
    internal sealed class TriggerStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public ObservableCollection<TriggerRule> Triggers { get; } = new();

        public void Load()
        {
            try
            {
                if (!File.Exists(AppPaths.TriggersFile))
                {
                    return;
                }

                string json = File.ReadAllText(AppPaths.TriggersFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                List<TriggerRule>? loaded = JsonSerializer.Deserialize<List<TriggerRule>>(json, SerializerOptions);
                if (loaded is null)
                {
                    return;
                }

                Triggers.Clear();
                foreach (TriggerRule rule in loaded)
                {
                    // Drop anything an older build saved that could not act now, rather than
                    // keeping a trigger that would silently never fire.
                    if (TriggerRule.Validate(rule.Pattern, rule.Action, rule.Response) is null)
                    {
                        Triggers.Add(rule);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Start with no triggers rather than failing to launch.
            }
        }

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Triggers, SerializerOptions);
                string temp = AppPaths.TriggersFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.TriggersFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-fatal: the triggers simply will not persist.
            }
        }
    }
}
