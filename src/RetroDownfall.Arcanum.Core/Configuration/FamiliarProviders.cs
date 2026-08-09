namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// A <b>Familiar</b> is a vendor CLI the operator has already installed and signed in to, which
/// Arcanum calls on for inference instead of dialing a metered HTTP endpoint. Arcanum invokes it; it
/// never installs, updates, configures, or authenticates it, and never reads its credential store.
/// </summary>
/// <remarks>
/// This is a transport kind, so everything above <c>IChatClientFactory</c> is unchanged. The
/// practical consequence for configuration is an inversion: a Familiar row has no endpoint and no
/// credential reference (there is nothing for Arcanum to hold), and its <c>models</c> array is
/// optional rather than the authoritative catalogue — the vendor ships models on its own schedule,
/// so an undeclared name is handed through verbatim and the CLI decides whether it exists.
/// </remarks>
public static class FamiliarProviders
{

    /// <summary>Default binary name for a Claude Code Familiar, resolved through <c>PATH</c>.</summary>
    public const string ClaudeCodeCommand = "claude";

    /// <summary>Default binary name for a Codex Familiar, resolved through <c>PATH</c>.</summary>
    public const string CodexCommand = "codex";

    public static bool IsFamiliar(AiProviderKind kind) =>
        kind is AiProviderKind.ClaudeCodeCli or AiProviderKind.CodexCli;

    public static bool IsFamiliar(ProviderSettings? provider) =>
        provider is not null && IsFamiliar(provider.Type);

    /// <summary>
    /// The binary Arcanum spawns when the operator supplies no override. Bare names on purpose: the
    /// CLI is a user-installed sibling binary found the same way the operator's own shell finds it.
    /// </summary>
    public static string DefaultCommand(AiProviderKind kind) =>
        kind switch
        {
            AiProviderKind.ClaudeCodeCli => ClaudeCodeCommand,

            AiProviderKind.CodexCli => CodexCommand,

            _ => string.Empty,
        };

    /// <summary>
    /// The binary for this provider row: the operator's <c>command</c> override when present,
    /// otherwise the kind's default name. A blank override is treated as absent so a half-filled
    /// field never spawns nothing.
    /// </summary>
    public static string ResolveCommand(ProviderSettings provider)
    {

        ArgumentNullException.ThrowIfNull(provider);

        return string.IsNullOrWhiteSpace(provider.Command)
            ? DefaultCommand(provider.Type)
            : provider.Command.Trim();

    }

    /// <summary>
    /// Operator-facing name for the CLI behind a kind, used in remediation text so the message names
    /// the thing the operator installed rather than an Arcanum enum member.
    /// </summary>
    public static string DisplayName(AiProviderKind kind) =>
        kind switch
        {
            AiProviderKind.ClaudeCodeCli => "Claude Code CLI",

            AiProviderKind.CodexCli => "Codex CLI",

            _ => kind.ToString(),
        };

    /// <summary>
    /// The sign-in command the operator runs themselves. Arcanum prints it and stops there — it
    /// never authenticates on the operator's behalf.
    /// </summary>
    public static string SignInCommand(AiProviderKind kind) =>
        kind switch
        {
            AiProviderKind.ClaudeCodeCli => Cli.DoctorRemedyCommands.ClaudeCodeSignIn,

            AiProviderKind.CodexCli => Cli.DoctorRemedyCommands.CodexSignIn,

            _ => string.Empty,
        };

    /// <summary>
    /// Whether <paramref name="model"/> is on this provider's hide list. Exact, case-insensitive,
    /// and deliberately only consulted by listing surfaces: hiding is a decluttering preference, not
    /// a policy control, so an explicitly named model still resolves.
    /// </summary>
    public static bool IsHidden(ProviderSettings provider, string? model)
    {

        ArgumentNullException.ThrowIfNull(provider);

        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        string[] hidden = provider.HiddenModels ?? [];

        for (int i = 0; i < hidden.Length; i++)
        {
            if (string.Equals(hidden[i]?.Trim(), model.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;

    }

}
