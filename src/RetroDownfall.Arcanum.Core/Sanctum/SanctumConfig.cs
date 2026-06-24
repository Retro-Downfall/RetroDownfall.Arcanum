namespace RetroDownfall.Arcanum.Core.Sanctum;

/// <summary>
/// Per-campaign execution isolation policy stored as JSON on the <see cref="TheForge.Campaign"/> entity.
/// </summary>
/// <remarks>
/// <see cref="ResourceLimits.MaxFileWriteMb"/> is enforced at runtime on in-process file-write tools.
/// Process/memory limits remain deferred to phase 2 (container backend).
/// </remarks>
public sealed record SanctumConfig
{

    public bool Enabled { get; init; } = false;

    public SanctumMode Mode { get; init; } = SanctumMode.Strict;

    public bool EnforcePathBoundary { get; init; } = true;

    private readonly List<string> _allowedPaths = [];

    public IReadOnlyList<string> AllowedPaths
    {

        get => _allowedPaths;

        init => _allowedPaths = new List<string>(value);

    }

    public NetworkPolicy NetworkPolicy { get; init; } = NetworkPolicy.AllowAll;

    private readonly List<string> _allowedDomains = [];

    public IReadOnlyList<string> AllowedDomains
    {

        get => _allowedDomains;

        init => _allowedDomains = new List<string>(value);

    }

    public ResourceLimits ResourceLimits { get; init; } = new();

    private readonly List<string> _disabledTools = [];

    public IReadOnlyList<string> DisabledTools
    {

        get => _disabledTools;

        init => _disabledTools = new List<string>(value);

    }

}

public enum SanctumMode
{

    Strict,

    AuditOnly,

}

public enum NetworkPolicy
{

    AllowAll,

    AllowList,

    DenyAll,

}

public sealed record ResourceLimits
{

    public int MaxProcessMemoryMb { get; init; } = 512;

    public int MaxProcessCount { get; init; } = 10;

    public int MaxFileWriteMb { get; init; } = 100;

    public int ProcessTimeoutSeconds { get; init; } = 300;

}
