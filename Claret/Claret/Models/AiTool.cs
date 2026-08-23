using System.Collections.Generic;

namespace Claret.Models
{
    /// <summary>
    /// An AI coding CLI the status bar can install on the host you are connected to. Each entry is
    /// a one-line shell script rather than a bare install command: it checks its prerequisite first
    /// and says what is missing, and it never calls <c>exit</c>, which would end the login shell.
    /// </summary>
    public sealed record AiTool(string Name, string Summary, string Script)
    {
        private const string NeedsNpm =
            "npm is required — install Node.js 20 or newer first";

        public static IReadOnlyList<AiTool> All { get; } = new[]
        {
            new AiTool(
                "Claude Code",
                "Anthropic, native installer",
                "curl -fsSL https://claude.ai/install.sh | bash"),

            new AiTool(
                "Gemini CLI",
                "Google, npm package",
                "if command -v npm >/dev/null 2>&1; then npm install -g @google/gemini-cli; "
                + $"else echo '{NeedsNpm}'; fi"),

            new AiTool(
                "Codex CLI",
                "OpenAI (ChatGPT), npm package",
                "if command -v npm >/dev/null 2>&1; then npm install -g @openai/codex; "
                + $"else echo '{NeedsNpm}'; fi"),
        };
    }
}
