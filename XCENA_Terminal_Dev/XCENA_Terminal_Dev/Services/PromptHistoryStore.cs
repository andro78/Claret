using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XCENA_Terminal_Dev.Services
{
    /// <summary>
    /// The prompts you have sent, newest first, persisted to prompts.json. Capped, because this is
    /// a scratchpad of recent asks rather than an archive — and because the file sits beside the
    /// other settings, not somewhere a long transcript belongs.
    /// </summary>
    internal sealed class PromptHistoryStore
    {
        private const int Limit = 30;

        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

        public ObservableCollection<string> Prompts { get; } = new();

        public void Load()
        {
            try
            {
                if (!File.Exists(AppPaths.PromptsFile))
                {
                    return;
                }

                string json = File.ReadAllText(AppPaths.PromptsFile);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                List<string>? loaded = JsonSerializer.Deserialize<List<string>>(json, SerializerOptions);
                if (loaded is null)
                {
                    return;
                }

                Prompts.Clear();
                foreach (string prompt in loaded.Where(p => !string.IsNullOrWhiteSpace(p)).Take(Limit))
                {
                    Prompts.Add(prompt);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Start with none rather than failing to launch.
            }
        }

        /// <summary>
        /// Records a prompt at the top. Sending the same text again moves it up instead of adding a
        /// second copy — a repeated ask is the same ask.
        /// </summary>
        public void Record(string prompt)
        {
            string text = prompt.Trim();
            if (text.Length == 0)
            {
                return;
            }

            int existing = Prompts.IndexOf(text);
            if (existing >= 0)
            {
                Prompts.Move(existing, 0);
            }
            else
            {
                Prompts.Insert(0, text);

                while (Prompts.Count > Limit)
                {
                    Prompts.RemoveAt(Prompts.Count - 1);
                }
            }

            Save();
        }

        public void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Prompts, SerializerOptions);
                string temp = AppPaths.PromptsFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.PromptsFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-fatal: the history simply will not survive a restart.
            }
        }
    }
}
