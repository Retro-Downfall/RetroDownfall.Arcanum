using System.Text.Json.Serialization;

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

    /// <summary>
    /// Carries the declared default of every member whose default is not <c>default(T)</c>, so a
    /// partial payload cannot silently weaken the policy.
    /// </summary>
    /// <remarks>
    /// System.Text.Json's generated creator models each init-only member as a pseudo constructor
    /// parameter emitted with <c>HasDefaultValue = false</c> and assigns all of them from an args
    /// array pre-filled with <c>default(T)</c>, so a member the payload omits loses its C# property
    /// initializer entirely. For the collections that only means "degrade to the empty, most
    /// restrictive value", which their own accessors already handle. For the members below it means
    /// the opposite: an omitted <c>enforcePathBoundary</c> would read as a deliberate request to turn
    /// path containment off, an omitted <c>maxBreachCount</c> would floor retention to the clamp
    /// minimum, and an omitted <c>resourceLimits</c> would arrive null and dereference inside the
    /// endpoint's clamp. A real constructor parameter carries its default into the generated
    /// <c>JsonParameterInfo</c>, so these values survive a body that leaves them out.
    /// <see cref="Enabled"/>, <see cref="Mode"/>, and <see cref="NetworkPolicy"/> need no such
    /// treatment: their declared defaults already equal <c>default(T)</c>.
    /// </remarks>
    [JsonConstructor]
    public SanctumConfig(
        bool enforcePathBoundary = true,
        ResourceLimits? resourceLimits = null,
        int maxBreachCount = 1000)
    {

        EnforcePathBoundary = enforcePathBoundary;

        ResourceLimits = resourceLimits!;

        MaxBreachCount = maxBreachCount;

    }

    public bool Enabled { get; init; } = false;

    public SanctumMode Mode { get; init; } = SanctumMode.Strict;

    /// <summary>
    /// Whether path containment is enforced for this Campaign. Defaults to <c>true</c>; the default
    /// lives on the constructor so a payload that omits it cannot read as an explicit <c>false</c>.
    /// </summary>
    public bool EnforcePathBoundary { get; init; }

    private readonly List<string> _allowedPaths = [];

    public IReadOnlyList<string> AllowedPaths
    {

        // W3.6: return a non-downcastable read-only view so a consumer cannot cast back to
        // List<string> and mutate the sandbox allow-list after construction.
        get => _allowedPaths.AsReadOnly();

        // A null value is not an error: System.Text.Json's generated object-initializer creator
        // assigns every init-only member on each deserialization, passing null for members the
        // payload omits, and it does not honour nullable annotations. Throwing here would turn an
        // explicit JSON null — or a merely absent property — into an ArgumentNullException during
        // body binding, surfacing as an unhandled 500 out of every containment check for the
        // campaign. Degrade to the empty (most restrictive) allow-list instead.
        init => _allowedPaths = value is null ? [] : new List<string>(value);

    }

    public NetworkPolicy NetworkPolicy { get; init; } = NetworkPolicy.AllowAll;

    private readonly List<string> _allowedDomains = [];

    public IReadOnlyList<string> AllowedDomains
    {

        get => _allowedDomains.AsReadOnly();

        init => _allowedDomains = value is null ? [] : new List<string>(value);

    }

    private readonly ResourceLimits _resourceLimits = new();

    /// <summary>
    /// Per-invocation resource ceilings. Never null.
    /// </summary>
    public ResourceLimits ResourceLimits
    {

        get => _resourceLimits;

        // Same hazard the collections guard against, with a worse landing: an omitted or explicitly
        // null resourceLimits would otherwise bind null onto a non-nullable member and dereference
        // inside SanctumEndpoints' clamp, turning a partial body into an unhandled 500.
        init => _resourceLimits = value ?? new ResourceLimits();

    }

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
    /// bounds a single request's page size rather than total stored history. Defaults to 1000; the
    /// default lives on the constructor so a payload that omits it is not floored to the clamp
    /// minimum.
    /// </summary>
    public int MaxBreachCount { get; init; }

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

/// <summary>
/// Per-invocation resource ceilings for a Sanctum-governed tool call.
/// </summary>
/// <remarks>
/// Positional so every ceiling is a real constructor parameter with a real default. Written as
/// init-only auto-properties these would be pseudo constructor parameters to System.Text.Json's
/// generated creator, and a body such as <c>{"resourceLimits":{}}</c> would zero all seven — which
/// for <see cref="MaxCpuSeconds"/>, <see cref="MaxMemoryMb"/>, and <see cref="MaxFileDescriptors"/>
/// means "unlimited", i.e. the omission would remove the ceiling rather than keep it.
/// </remarks>
/// <param name="MaxProcessMemoryMb">Maximum per-process memory in megabytes.</param>
/// <param name="MaxProcessCount">Maximum concurrent child processes.</param>
/// <param name="MaxFileWriteMb">Maximum single file-write size in megabytes.</param>
/// <param name="ProcessTimeoutSeconds">Maximum wall-clock seconds per child process.</param>
/// <param name="MaxCpuSeconds">Maximum CPU time in seconds per tool invocation, enforced at the OS level. 0 = unlimited.</param>
/// <param name="MaxMemoryMb">Maximum resident memory in megabytes per tool invocation, enforced at the OS level. 0 = unlimited.</param>
/// <param name="MaxFileDescriptors">Maximum open file descriptors per tool invocation, enforced at the OS level. 0 = unlimited.</param>
public sealed record ResourceLimits(
    int MaxProcessMemoryMb = 512,
    int MaxProcessCount = 10,
    int MaxFileWriteMb = 100,
    int ProcessTimeoutSeconds = 300,
    int MaxCpuSeconds = 30,
    int MaxMemoryMb = 512,
    int MaxFileDescriptors = 256);
