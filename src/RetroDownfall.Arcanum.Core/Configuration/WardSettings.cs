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

}
