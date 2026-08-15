using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Core.Covenant;

public sealed class ProviderCallMaterializationSnapshot
{
    public ProviderCallMaterializationSnapshot(
        bool unprovenanced,
        IEnumerable<MaterializationSourceDigestInput> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        MaterializationSourceDigestInput[] frozen = sources
            .Select(static source => source is null
                ? throw new ArgumentException("Materialization sources cannot contain null.", nameof(sources))
                : source with
                {
                    Occurrences = source.Occurrences.IsDefault
                        ? default
                        : [.. source.Occurrences]
                })
            .ToArray();

        Unprovenanced = unprovenanced;
        Sources = [.. frozen];
        Digest = CovenantDigests.Materialization(ToDigestInput());
    }

    public bool Unprovenanced { get; }

    public ImmutableArray<MaterializationSourceDigestInput> Sources { get; }

    public CovenantDigest Digest { get; }

    public MaterializationDigestInput ToDigestInput() =>
        new(Unprovenanced, Sources);
}
