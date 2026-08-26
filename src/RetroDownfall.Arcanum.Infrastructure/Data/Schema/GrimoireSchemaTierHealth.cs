namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// Why a transaction tier is or is not usable after an install attempt.
/// </summary>
/// <remarks>
/// Every non-healthy value is a fail-closed decision, not a warning. Arcanum never guesses at
/// compatibility: an installed tier that is newer than this binary, that recorded a different
/// definition at the same version, that lost its metadata, or whose catalog drifted is refused, and
/// the capability it belongs to is simply unavailable until an operator repairs it.
/// </remarks>
internal enum GrimoireSchemaTierHealth
{

    Healthy = 0,

    Unavailable = 1,

    /// <summary>Installed by a newer build. Downgrading silently would corrupt it.</summary>
    IncompatibleNewerVersion = 2,

    /// <summary>Same version, different source definition. The two disagree about what v1 means.</summary>
    SourceDefinitionMismatch = 3,

    /// <summary>The objects are present but no longer match the manifest that created them.</summary>
    InstalledCatalogDrift = 4,

    /// <summary>Objects exist with no metadata row, so nothing proves what installed them.</summary>
    MetadataMissing = 5,

    /// <summary>An earlier tier this one depends on is not healthy, so it was never attempted.</summary>
    DependencyUnavailable = 6,

    /// <summary>
    /// A journaled version run this binary can finish has not finished. The tier is not healthy, so
    /// the capability is unavailable and a dependent tier reports
    /// <see cref="DependencyUnavailable"/> — but this is deliberately not a refusal. Core in
    /// particular must not throw here: a Core tier whose pending sweep aborted startup could never
    /// run that sweep, and the installation would be permanently unopenable by the only process able
    /// to repair it.
    /// </summary>
    TransitionIncomplete = 7,

    /// <summary>
    /// A journal row this binary cannot finish — a target above head, a head that changed under the
    /// run, a from-version the metadata row no longer agrees with, or a pending sweep this binary
    /// does not declare. Fail-closed, and a refusal for Core.
    /// </summary>
    TransitionUnresumable = 8,

    /// <summary>
    /// The metadata row and the catalog disagree about version with no journal row to explain it — a
    /// catalog advanced without its metadata, which a restore or a hand edit can produce. Recording
    /// the newer version would advance past work nothing proves was ever done.
    /// </summary>
    MixedCatalogVersions = 9,

}

/// <summary>
/// The outcome of installing or converging one transaction tier.
/// </summary>
/// <remarks>
/// <paramref name="DiagnosticCode"/> is closed and content-free. It reaches logs and, through
/// Covenant availability, operator-facing surfaces, so it can never carry exception text, SQL, a
/// path, or a secret-derived value.
/// </remarks>
internal sealed record GrimoireSchemaTierInstallResult(
    GrimoireSchemaTransactionTier TransactionTier,
    int SchemaVersion,
    GrimoireSchemaTierHealth Health,
    string SourceDefinitionFingerprint,
    string? InstalledCatalogFingerprint,
    string? DiagnosticCode)
{

    public bool IsHealthy => Health == GrimoireSchemaTierHealth.Healthy;

}

/// <summary>
/// All three tier results from one installation. Callers carry the whole set rather than a single
/// success flag, because the three failure domains are the point of the design.
/// </summary>
internal sealed record GrimoireSchemaInstallResult(
    GrimoireSchemaTierInstallResult Core,
    GrimoireSchemaTierInstallResult CovenantCanonical,
    GrimoireSchemaTierInstallResult CovenantAccelerator);
