using System;

namespace Claret.Models
{
    /// <summary>
    /// A board worth keeping: a name, the port it usually appears on, and the line settings it
    /// wants. Saved so a console that gets opened every day is one click rather than three.
    /// </summary>
    public sealed class SerialProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>What the board is called, e.g. "N1 safety island".</summary>
        public string Name { get; set; } = string.Empty;

        public SerialConnection Settings { get; set; } = new();

        /// <summary>Falls back to the port when the entry was saved without a name.</summary>
        public string DisplayName => Name.Length > 0 ? Name : Settings.DisplayName;

        /// <summary>The line underneath the name in the list.</summary>
        public string Detail => Settings.Summary;

        public SerialProfile Clone() => new()
        {
            Id = Id,
            Name = Name,
            Settings = Settings.Clone(),
        };
    }
}
