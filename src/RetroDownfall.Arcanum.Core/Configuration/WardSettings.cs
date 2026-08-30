namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Ward compatibility projection retained through issue #219. No ordinary or Covenant-retirement
/// live path consumes the approval fields; the active-Ward API remains for compatibility.
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

    /// <summary>No ordinary or Covenant-retirement live path consumes this compatibility value.</summary>
    public bool AutoDenyInUnattendedMode { get; set; } = true;

    /// <summary>
    /// Default unattended flag for operator-facing callers (Command Center, <c>ask</c>/<c>chat</c>
    /// without <c>--unattended</c>). Headless paths (daemons, apprentices, OpenAI-compat, etc.)
    /// always force unattended and ignore this setting.
    /// </summary>
    public bool UnattendedMode { get; set; }

    /// <summary>
    /// Retained compatibility value (<c>Arcanum:Security:Ward:AutoApprove:Enabled</c>). No ordinary
    /// or Covenant-retirement live path consumes it.
    /// </summary>
    public bool AutoApproveEnabled { get; set; }

    private readonly List<string> _autoApproveTools = [];

    /// <summary>
    /// Retained compatibility names normalized by <c>ResolveWard</c> (trimmed, blanks dropped,
    /// ordinal-ignore-case deduplicated). No ordinary or Covenant-retirement live path consumes them.
    /// </summary>
    public IReadOnlyList<string> AutoApproveTools
    {

        get => _autoApproveTools;

        init => _autoApproveTools = new List<string>(value);

    }

}
