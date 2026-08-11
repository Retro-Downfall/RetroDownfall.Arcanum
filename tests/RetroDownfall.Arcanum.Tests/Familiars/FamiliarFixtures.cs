using System.Reflection;

namespace RetroDownfall.Arcanum.Tests.Familiars;

/// <summary>
/// Golden output recorded from the real CLIs on a developer machine, then redacted. Nothing here
/// requires a live subscription, and nothing here is hand-written: the frames are exactly what
/// <c>claude 2.1.220</c> and <c>codex-cli 0.147.0</c> emitted, so a wire-format change shows up as a
/// failing mapping test instead of a broken turn in production.
/// </summary>
/// <remarks>
/// Recorded with:
/// <code>
/// claude -p --output-format stream-json --verbose --tools "" --model sonnet
/// claude -p --output-format stream-json --verbose --include-partial-messages --tools "" --model haiku
/// codex exec --json --sandbox read-only --skip-git-repo-check --ephemeral -C /tmp
/// claude auth status --json
/// codex doctor --json
/// </code>
/// Home directories, account e-mail, and organisation identifiers were replaced with placeholders;
/// the frame structure is untouched.
/// </remarks>
internal static class FamiliarFixtures
{

    internal const string ClaudeSuccess = "claude-code-cli-success.ndjson";

    internal const string ClaudePartialMessages = "claude-code-cli-partial-messages.ndjson";

    internal const string ClaudeModelError = "claude-code-cli-model-error.ndjson";

    internal const string CodexSuccess = "codex-cli-success.ndjson";

    internal const string CodexTurnFailed = "codex-cli-turn-failed.ndjson";

    internal const string CodexShellTool = "codex-cli-shell-tool.ndjson";

    internal const string ClaudeAuthStatusConfigured = "claude-auth-status-configured.json";

    internal const string ClaudeAuthStatusSignedOut = "claude-auth-status-signed-out.json";

    internal const string CodexDoctorConfigured = "codex-doctor-configured.json";

    internal const string CodexDoctorSignedOut = "codex-doctor-signed-out.json";

    internal static string ReadText(string fileName) =>
        File.ReadAllText(Path.Combine(Directory(), fileName));

    internal static string[] ReadLines(string fileName) =>
        [
            .. File.ReadAllLines(Path.Combine(Directory(), fileName))
                .Where(static line => !string.IsNullOrWhiteSpace(line)),
        ];

    private static string Directory() =>
        Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
            "TestData",
            "Familiars");

}
