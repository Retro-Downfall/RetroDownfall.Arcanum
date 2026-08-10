using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Ward runtime projection. Operator policy comes from <c>Arcanum:Security:Ward</c>; timeout and
/// active-ward capacity are code-owned invariants.
/// </summary>
public sealed record WardSettings
{

    public bool Enabled { get; set; } = true;

    private readonly List<string> _forbiddenArts = new()
    {
        ToolRiskClassifier.ExecuteCommandToolName,
        ToolRiskClassifier.ApplyPatchToolName,
        ToolRiskClassifier.WorkspaceCheckToolName,
        "write_file",
        "replace_text_block",
        "delete_lexicon",
        "run_spell_script",
    };

    public IReadOnlyList<string> ForbiddenArts
    {

        get => _forbiddenArts;

        init => _forbiddenArts = new List<string>(value);

    }

    public int TimeoutSeconds { get; set; } = 120;

    public int MaxActiveWards { get; set; } = 50;

    public bool AutoDenyInUnattendedMode { get; set; } = true;

    /// <summary>
    /// Default unattended flag for operator-facing callers (Command Center, <c>ask</c>/<c>chat</c>
    /// without <c>--unattended</c>). Headless paths (daemons, apprentices, OpenAI-compat, etc.)
    /// always force unattended and ignore this setting.
    /// </summary>
    public bool UnattendedMode { get; set; }

    /// <summary>
    /// Master opt-in for operator auto-approval (<c>Arcanum:Security:Ward:AutoApprove:Enabled</c>).
    /// Off by default; on its own it grants nothing, because <see cref="AutoApproveTools"/> is the
    /// allowlist that names what may skip the prompt.
    /// </summary>
    public bool AutoApproveEnabled { get; set; }

    private readonly List<string> _autoApproveTools = [];

    /// <summary>
    /// Exact tool names whose Ward the host may resolve on the operator's behalf. Normalized by
    /// <c>ResolveWard</c> (trimmed, blanks dropped, ordinal-ignore-case deduplicated). Empty is a
    /// no-op, and a listed name that is unavailable, unadvertised, or excluded by attunement grants
    /// no capability — auto-approval only supplies the human consent step.
    /// </summary>
    public IReadOnlyList<string> AutoApproveTools
    {

        get => _autoApproveTools;

        init => _autoApproveTools = new List<string>(value);

    }

}
