using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using PowerTerm.Models;

namespace PowerTerm.Services
{
    /// <summary>Terminal highlight rules, persisted to highlights.json.</summary>
    internal sealed class HighlightStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

        public ObservableCollection<HighlightRule> Rules { get; } = new();

        public void Load()
        {
            try
            {
                if (!File.Exists(AppPaths.HighlightsFile))
                {
                    return;
                }

                string json = File.ReadAllText(AppPaths.HighlightsFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                List<HighlightRule>? loaded = JsonSerializer.Deserialize<List<HighlightRule>>(json, SerializerOptions);
                if (loaded is null)
                {
                    return;
                }

                Rules.Clear();
                foreach (HighlightRule rule in loaded)
                {
                    // A pattern saved by an older build could be unusable by now; drop it
                    // rather than shipping something the terminal will refuse.
                    if (HighlightRule.Validate(rule.Pattern) is null)
                    {
                        Rules.Add(rule);
                    }
                }

                ResolveColors();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Start with no rules rather than failing to launch.
            }
        }

        /// <summary>Re-assigns the automatic colours after the rule list changes.</summary>
        public void ResolveColors() => HighlightRule.ResolveColors(Rules);

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Rules, SerializerOptions);
                string temp = AppPaths.HighlightsFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.HighlightsFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-fatal: the rules simply will not persist.
            }
        }
    }
}
