using System.Collections.Generic;

namespace PowerTerm.Models
{
    /// <summary>
    /// The fonts offered for the terminal, and why the list looks the way it does.
    ///
    /// A terminal gives every wide character exactly two cells. Hangul fills them only when one
    /// syllable advances twice the font's Latin width; pair a Latin font with a fallback for
    /// Hangul and the glyphs come out narrower than their cells, which is what makes mixed
    /// Korean output look ragged. The faces marked <see cref="Candidate.AlignsHangul"/> get the
    /// ratio right on their own.
    /// </summary>
    public static class TerminalFont
    {
        /// <summary>Stored as the family name; empty means "let the app choose".</summary>
        public const string Automatic = "";

        public sealed record Candidate(string Family, string Note, bool AlignsHangul);

        /// <summary>
        /// Offered in the dialog, best-for-Hangul first. The two Korean coding fonts are not part
        /// of Windows, so they are shown but reported as missing when they are not installed; the
        /// *Che faces always are.
        /// </summary>
        public static IReadOnlyList<Candidate> Candidates { get; } = new[]
        {
            new Candidate("D2Coding", "Korean coding font", AlignsHangul: true),
            new Candidate("NanumGothicCoding", "Korean coding font", AlignsHangul: true),
            new Candidate("GulimChe", "ships with Windows", AlignsHangul: true),
            new Candidate("DotumChe", "ships with Windows", AlignsHangul: true),
            new Candidate("Cascadia Mono", "Latin only", AlignsHangul: false),
            new Candidate("Consolas", "Latin only", AlignsHangul: false),
        };

        /// <summary>
        /// The order the automatic choice walks, matching the page. Kept here so the dialog can
        /// tell the user which font automatic will actually land on.
        /// </summary>
        public static IReadOnlyList<string> AutomaticOrder { get; } = new[]
        {
            "D2Coding", "NanumGothicCoding", "GulimChe", "DotumChe",
        };
    }
}
