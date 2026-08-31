namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Ward runtime projection. Ordinary tool calls create informational Ward records and never consult
/// an approval setting; the active-Ward API remains for compatibility.
/// </summary>
public sealed record WardSettings
{

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

    /// <summary>
    /// Default unattended flag for operator-facing callers (Command Center, <c>ask</c>/<c>chat</c>
    /// without <c>--unattended</c>). Headless paths (daemons, apprentices, OpenAI-compat, etc.)
    /// always force unattended and ignore this setting.
    /// </summary>
    public bool UnattendedMode { get; set; }

}
