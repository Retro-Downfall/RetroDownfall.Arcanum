namespace RetroDownfall.Arcanum.Core.Sanctum;

/// <summary>
/// Per-campaign execution isolation policy stored as JSON on the <see cref="TheForge.Campaign"/> entity.
/// </summary>
/// <remarks>
/// <see cref="ResourceLimits.MaxFileWriteMb"/> is enforced at runtime on in-process file-write tools.
/// <see cref="ResourceLimits.MaxCpuSeconds"/>, <see cref="ResourceLimits.MaxMemoryMb"/>, and
/// <see cref="ResourceLimits.MaxFileDescriptors"/> are enforced at the OS level (setrlimit / cgroups v2)
/// on Unix child processes spawned by <c>execute_command</c> and <c>run_spell_script</c>; on Windows,
/// Job Objects enforce CPU time, process/job memory, and <see cref="ResourceLimits.MaxProcessCount"/>
/// (file descriptors have no Job Object equivalent). See DESIGN §11.15 /
/// <c>Platform.IProcessResourceLimiter</c>. Container/VM isolation remains deferred to phase 2.
/// </remarks>
public sealed record SanctumConfig
{

    public bool Enabled { get; init; } = false;

    public SanctumMode Mode { get; init; } = SanctumMode.Strict;

    public bool EnforcePathBoundary { get; init; } = true;

    private readonly List<string> _allowedPaths = [];

    public IReadOnlyList<string> AllowedPaths
    {

        // W3.6: return a non-downcastable read-only view so a consumer cannot cast back to
        // List<string> and mutate the sandbox allow-list after construction.
        get => _allowedPaths.AsReadOnly();

        // A null value is not an error: System.Text.Json's generated object-initializer creator
        // assigns every init-only member on each deserialization, passing null for members the
        // payload omits. Degrade to the empty (most restrictive) allow-list rather than throwing.
        init => _allowedPaths = value is null ? [] : new List<string>(value);

    }

    public NetworkPolicy NetworkPolicy { get; init; } = NetworkPolicy.AllowAll;

    private readonly List<string> _allowedDomains = [];

    public IReadOnlyList<string> AllowedDomains
    {

        get => _allowedDomains.AsReadOnly();

        init => _allowedDomains = value is null ? [] : new List<string>(value);

    }

    public ResourceLimits ResourceLimits { get; init; } = new();

    private readonly List<string> _disabledTools = [];

    public IReadOnlyList<string> DisabledTools
    {

        get => _disabledTools.AsReadOnly();

        init => _disabledTools = value is null ? [] : new List<string>(value);

    }

    /// <summary>
    /// Per-campaign retention limit for persisted Sanctum breach rows (Grimoire-backed). Clamped via
    /// <see cref="Configuration.ArcanumSettingClamps.SanctumMaxBreachCount"/>. Distinct from the API
    /// query limit (<see cref="Configuration.ArcanumSettingClamps.SanctumBreachQueryLimit"/>), which
    /// bounds a single request's page size rather than total stored history.
    /// </summary>
    public int MaxBreachCount { get; init; } = 1000;

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

    /// <summary>Maximum CPU time in seconds per tool invocation, enforced at the OS level. 0 = unlimited.</summary>
    public int MaxCpuSeconds { get; init; } = 30;

    /// <summary>Maximum resident memory in megabytes per tool invocation, enforced at the OS level. 0 = unlimited.</summary>
    public int MaxMemoryMb { get; init; } = 512;

    /// <summary>Maximum open file descriptors per tool invocation, enforced at the OS level. 0 = unlimited.</summary>
    public int MaxFileDescriptors { get; init; } = 256;

}
