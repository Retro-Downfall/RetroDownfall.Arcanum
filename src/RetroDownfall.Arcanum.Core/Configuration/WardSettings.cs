namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Ward compatibility projection. Ordinary tool calls are record-only; the live-Ward fields remain
/// for the separate Covenant retirement path and the active-Ward API.
/// </summary>
public sealed record WardSettings
{

    public bool Enabled { get; set; } = true;

    private readonly List<string> _forbiddenArts = [];

    /// <summary>
    /// Operator-configured names removed by <c>ToolPolicy.NoForbiddenArts</c>. This list does not
    /// classify or gate ordinary tool execution.
    /// </summary>
    public IReadOnlyList<string> ForbiddenArts
    {

        get => _forbiddenArts;

        init => _forbiddenArts = new List<string>(value);

    }

    public int TimeoutSeconds { get; set; } = 120;

    public int MaxActiveWards { get; set; } = 50;

    /// <summary>Retained compatibility value; ordinary unattended tool calls ignore it.</summary>
    public bool AutoDenyInUnattendedMode { get; set; } = true;

    /// <summary>
    /// Default unattended flag for operator-facing callers (Command Center, <c>ask</c>/<c>chat</c>
    /// without <c>--unattended</c>). Headless paths (daemons, apprentices, OpenAI-compat, etc.)
    /// always force unattended and ignore this setting.
    /// </summary>
    public bool UnattendedMode { get; set; }

    /// <summary>
    /// Master opt-in for the retained Covenant retirement auto-approval policy
    /// (<c>Arcanum:Security:Ward:AutoApprove:Enabled</c>). Ordinary tool calls ignore it.
    /// </summary>
    public bool AutoApproveEnabled { get; set; }

    private readonly List<string> _autoApproveTools = [];

    /// <summary>
    /// Exact tool names eligible for the retained Covenant auto-approval policy. Normalized by
    /// <c>ResolveWard</c> (trimmed, blanks dropped, ordinal-ignore-case deduplicated). Ordinary tool
    /// calls ignore this list.
    /// </summary>
    public IReadOnlyList<string> AutoApproveTools
    {

        get => _autoApproveTools;

        init => _autoApproveTools = new List<string>(value);

    }

}
