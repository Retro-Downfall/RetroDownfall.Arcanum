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

    public List<string> AllowedPaths { get; init; } = new();

    public NetworkPolicy NetworkPolicy { get; init; } = NetworkPolicy.AllowAll;

    public List<string> AllowedDomains { get; init; } = new();

    public ResourceLimits ResourceLimits { get; init; } = new();

    public List<string> DisabledTools { get; init; } = new();

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
