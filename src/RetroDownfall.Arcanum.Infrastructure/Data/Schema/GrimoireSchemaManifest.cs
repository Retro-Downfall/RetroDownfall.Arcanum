namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The complete closed set of objects one transaction tier expects to find installed, with the
/// source identity that produced them.
/// </summary>
/// <remarks>
/// Entry order participates in the installed-catalog fingerprint, so a manifest is built once from
/// the ordered catalog and never reordered.
/// </remarks>
internal sealed record GrimoireSchemaManifest(
    GrimoireSchemaFamily Family,
    GrimoireSchemaTransactionTier TransactionTier,
    int Version,
    string SourceDefinitionFingerprint,
    IReadOnlyList<GrimoireSchemaManifestEntry> Entries);

/// <summary>
/// Why an inspection rejected a tier. Closed and content-free: a diagnostic names the tier and the
/// object, never SQL text, a path, or a value, because these codes reach logs and API responses.
/// </summary>
internal enum GrimoireSchemaInspectionFailure
{

    MissingObject = 0,

    UnexpectedObject = 1,

    DefinitionDrift = 2,

    IndexShapeDrift = 3,

    ShadowObjectDrift = 4,

    CatalogReadFailed = 5,

}

/// <summary>
/// The outcome of inspecting one tier's installed catalog.
/// </summary>
/// <remarks>
/// A successful inspection carries the installed-catalog fingerprint, which is the value written to
/// and compared against <c>grimoire_feature_schemas</c>. A failed one carries a closed reason and
/// the offending object name and no fingerprint at all, so a caller cannot accidentally persist a
/// fingerprint for a catalog that did not validate.
/// </remarks>
internal sealed record GrimoireSchemaInspectionResult(
    bool IsValid,
    string? InstalledCatalogFingerprint,
    GrimoireSchemaInspectionFailure? Failure,
    string? ObjectName)
{

    public static GrimoireSchemaInspectionResult Valid(string fingerprint) =>
        new(true, fingerprint, null, null);

    public static GrimoireSchemaInspectionResult Invalid(
        GrimoireSchemaInspectionFailure failure,
        string objectName) =>
        new(false, null, failure, objectName);

    /// <summary>
    /// The stable, content-free code recorded in tier metadata and surfaced to operators.
    /// </summary>
    public string? DiagnosticCode =>
        Failure is null ? null : $"Grimoire.Schema.{Failure}";

}
