namespace RetroDownfall.Arcanum.Infrastructure.Data.Schema;

/// <summary>
/// The complete closed mapping from every trusted object and explicit index to the single tier that
/// owns it.
/// </summary>
/// <remarks>
/// Without this, inspecting one tier could not tell "an object another tier legitimately installed"
/// apart from "an object nobody declared". The first must be ignored; the second is drift. Owning
/// every name in one registry is what lets all three tiers be inspected independently against the
/// same live database without any of them reporting the others as unexpected.
///
/// <para>Construction rejects an object claimed by two tiers. A duplicate would make ownership
/// depend on which tier happened to be inspected first, which is exactly the ambiguity this type
/// exists to remove.</para>
/// </remarks>
internal sealed class GrimoireSchemaTierOwnershipRegistry
{

    private readonly Dictionary<string, GrimoireSchemaTransactionTier> _objects;

    private readonly Dictionary<string, GrimoireSchemaTransactionTier> _indexes;

    internal GrimoireSchemaTierOwnershipRegistry(IReadOnlyList<GrimoireSchemaManifest> manifests)
    {

        ArgumentNullException.ThrowIfNull(manifests);

        _objects = new Dictionary<string, GrimoireSchemaTransactionTier>(StringComparer.Ordinal);

        _indexes = new Dictionary<string, GrimoireSchemaTransactionTier>(StringComparer.Ordinal);

        foreach (GrimoireSchemaManifest manifest in manifests)
        {

            foreach (GrimoireSchemaManifestEntry entry in manifest.Entries)
            {

                Claim(_objects, entry.Name, manifest.TransactionTier, "object");

                foreach (GrimoireExpectedIndex index in entry.Indexes)
                {

                    Claim(_indexes, index.Name, manifest.TransactionTier, "index");

                }

            }

        }

    }

    /// <summary>
    /// Builds the registry over all three shipped tiers.
    /// </summary>
    internal static GrimoireSchemaTierOwnershipRegistry CreateDefault() =>
        new(
        [
            GrimoireSchemaManifests.Core,
            GrimoireSchemaManifests.CovenantCanonical,
            GrimoireSchemaManifests.CovenantAccelerator,
        ]);

    internal bool TryGetObjectOwner(string name, out GrimoireSchemaTransactionTier tier) =>
        _objects.TryGetValue(name, out tier);

    internal bool TryGetIndexOwner(string name, out GrimoireSchemaTransactionTier tier) =>
        _indexes.TryGetValue(name, out tier);

    private static void Claim(
        Dictionary<string, GrimoireSchemaTransactionTier> owners,
        string name,
        GrimoireSchemaTransactionTier tier,
        string kind)
    {

        if (owners.TryGetValue(name, out GrimoireSchemaTransactionTier existing))
        {

            throw new InvalidOperationException(
                $"Grimoire schema {kind} '{name}' is claimed by both the {existing} and {tier} tiers.");

        }

        owners[name] = tier;

    }

}

/// <summary>
/// The three shipped tier manifests, built once from the catalog.
/// </summary>
internal static class GrimoireSchemaManifests
{

    private static readonly Lazy<GrimoireSchemaManifest> LoadedCore =
        new(
            () => GrimoireSchemaManifestBuilder.Build(
                GrimoireSchemaFamily.Core,
                GrimoireSchemaTransactionTier.Core,
                GrimoireSchemaManifestBuilder.CovenantSchemaVersion,
                GrimoireSchemaCatalog.CoreSchemaFingerprint,
                GrimoireSchemaCatalog.CoreObjects),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<GrimoireSchemaManifest> LoadedCanonical =
        new(
            () => GrimoireSchemaManifestBuilder.Build(
                GrimoireSchemaFamily.Covenant,
                GrimoireSchemaTransactionTier.CovenantCanonical,
                GrimoireSchemaManifestBuilder.CovenantSchemaVersion,
                GrimoireSchemaCatalog.CovenantCanonicalSchemaFingerprint,
                GrimoireSchemaCatalog.CovenantCanonicalObjects),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<GrimoireSchemaManifest> LoadedAccelerator =
        new(
            () => GrimoireSchemaManifestBuilder.Build(
                GrimoireSchemaFamily.Covenant,
                GrimoireSchemaTransactionTier.CovenantAccelerator,
                GrimoireSchemaManifestBuilder.CovenantSchemaVersion,
                GrimoireSchemaCatalog.CovenantAcceleratorSchemaFingerprint,
                GrimoireSchemaCatalog.CovenantAcceleratorObjects),
            LazyThreadSafetyMode.ExecutionAndPublication);

    public static GrimoireSchemaManifest Core => LoadedCore.Value;

    public static GrimoireSchemaManifest CovenantCanonical => LoadedCanonical.Value;

    public static GrimoireSchemaManifest CovenantAccelerator => LoadedAccelerator.Value;

    public static GrimoireSchemaManifest ForTier(GrimoireSchemaTransactionTier tier) =>
        tier switch
        {

            GrimoireSchemaTransactionTier.Core => Core,

            GrimoireSchemaTransactionTier.CovenantCanonical => CovenantCanonical,

            GrimoireSchemaTransactionTier.CovenantAccelerator => CovenantAccelerator,

            _ => throw new ArgumentOutOfRangeException(nameof(tier)),

        };

}
