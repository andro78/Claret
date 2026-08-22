using System;
using System.Text.Json.Serialization;

namespace XCENA_Terminal_Dev.Models
{
    /// <summary>What a trigger does when its text turns up in the output.</summary>
    public enum TriggerEffect
    {
        /// <summary>Beep and print a line saying which trigger fired.</summary>
        Notify,

        /// <summary>Begin recording the session to a file, if it is not already being recorded.</summary>
        StartLog,

        /// <summary>Stop recording.</summary>
        StopLog,

        /// <summary>Type something back at the far end.</summary>
        Send,
    }

    /// <summary>
    /// One "when this text appears, do that" rule. The console equivalent of an oscilloscope
    /// trigger: you leave a board running, and the thing you were waiting for arms the action
    /// instead of you having to be watching the moment it scrolls past.
    /// <para>
    /// Matching is on the readable stream — escape sequences removed, once, as the bytes arrive —
    /// rather than on what is currently on screen. A line that scrolls away between two frames
    /// still counts, and a redraw of a line already matched does not count twice.
    /// </para>
    /// </summary>
    public sealed class TriggerRule
    {
        /// <summary>The literal text to wait for. Plain text, never a pattern language.</summary>
        public string Pattern { get; set; } = string.Empty;

        public bool IgnoreCase { get; set; } = true;

        public TriggerEffect Action { get; set; } = TriggerEffect.Notify;

        /// <summary>What to type back. Only meaningful for <see cref="TriggerEffect.Send"/>.</summary>
        public string Response { get; set; } = string.Empty;

        /// <summary>
        /// Whether the response is a line, ending in a Return. Off matters more than it sounds:
        /// "Hit any key to stop autoboot" wants one bare character, and a Return with it would
        /// carry straight on into the boot loader's prompt.
        /// </summary>
        public bool SendReturn { get; set; } = true;

        /// <summary>
        /// Fire once per session and then stand down. For a thing that happens at boot — a login
        /// prompt, a banner — one answer is the whole intent, and answering the second one is a
        /// surprise.
        /// </summary>
        public bool Once { get; set; }

        public bool Enabled { get; set; } = true;

        [JsonIgnore]
        public bool IsUsable => Enabled && Pattern.Length > 0 && Validate(Pattern, Action, Response) is null;

        /// <summary>
        /// Identity of the rule as a watcher sees it: what it waits for and what it does. Used to
        /// carry "already fired" and "cooling down" across a re-push of the list, so saving an
        /// unrelated rule does not re-arm a one-shot that has already gone off. Editing any of
        /// these deliberately does re-arm it, which is what editing a trigger means.
        /// </summary>
        [JsonIgnore]
        public string Key => $"{IgnoreCase}|{(int)Action}|{SendReturn}|{Pattern}|{Response}";

        /// <summary>Short description for the trigger list.</summary>
        [JsonIgnore]
        public string Summary
        {
            get
            {
                string what = Action switch
                {
                    TriggerEffect.StartLog => "start recording",
                    TriggerEffect.StopLog => "stop recording",
                    TriggerEffect.Send => SendReturn ? $"send \u201C{Response}\u201D + Return" : $"send \u201C{Response}\u201D",
                    _ => "alert me",
                };

                string casing = IgnoreCase ? "any case" : "exact case";
                string once = Once ? " \u00B7 once" : string.Empty;
                return $"{what} \u00B7 {casing}{once}";
            }
        }

        /// <summary>The glyph shown against the rule in the list, by action.</summary>
        [JsonIgnore]
        public string Glyph => Action switch
        {
            TriggerEffect.StartLog => "\uE7C8",  // page
            TriggerEffect.StopLog => "\uE71A",   // stop
            TriggerEffect.Send => "\uE724",      // send
            _ => "\uEA8F",                       // alert
        };

        public TriggerRule Clone() => new()
        {
            Pattern = Pattern,
            IgnoreCase = IgnoreCase,
            Action = Action,
            Response = Response,
            SendReturn = SendReturn,
            Once = Once,
            Enabled = Enabled,
        };

        /// <summary>Rejects a rule that could not do anything useful. Null when it is fine.</summary>
        public static string? Validate(string pattern, TriggerEffect action, string response)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return "Enter the text to wait for.";
            }

            if (action == TriggerEffect.Send && response.Length == 0)
            {
                return "Enter what to send back.";
            }

            return null;
        }
    }
}
