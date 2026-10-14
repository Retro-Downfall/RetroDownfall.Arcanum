using System.Collections.Immutable;
using System.Text;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.LongRest;

/// <summary>Pure fixtures for the packing, planning and layout tests: valid exact leaves, children, work identities and payloads.</summary>
internal static class LongRestPackingData
{
    internal const string Hash = "0000000000000000000000000000000000000000000000000000000000000001";

    internal static readonly CovenantDigest Digest = new(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

    internal static readonly LongRestSweepScope Scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null)).Value;

    /// <summary>An exact eligible source leaf whose content is <paramref name="content"/>; the memory and version identities are derived from the index.</summary>
    internal static LongRestSourceLeaf Leaf(int index, string content)
    {
        DateTimeOffset instant = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        string memory = "m-" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

        LongRestSnapshot snapshot = new(memory, "claim-" + memory, "v-" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), 1,
            AnnalContentHashFormat.LegacyStoreDigest, Hash, AnnalOrigin.OperatorStated, null, SagaMemoryScopeKind.Global, null,
            ContentSensitivity.None, instant, null, instant, null, null, true, false, false, false, false, [], []);

        return new(snapshot, index + 1, 1, null, ContentSensitivity.None, GenerationProvenance.CreateExact([]), []) { Content = content };
    }

    /// <summary>A content of exactly <paramref name="utf8Bytes"/> UTF-8 bytes built from one-, two-, three- and four-byte characters.</summary>
    internal static string Content(Random random, int utf8Bytes)
    {
        StringBuilder text = new();

        int remaining = utf8Bytes;

        while (remaining > 0)
        {
            int width = Math.Min(remaining, random.Next(1, 5));

            switch (width)
            {
                case 1:
                    text.Append((char)('a' + random.Next(26)));

                    break;

                case 2:
                    text.Append((char)('à' + random.Next(32)));

                    break;

                case 3:
                    text.Append((char)('あ' + random.Next(64)));

                    break;

                default:
                    text.Append(char.ConvertFromUtf32(0x1F600 + random.Next(64)));

                    width = 4;

                    break;
            }

            remaining -= width;
        }

        return text.ToString();
    }

    internal static LongRestWorkIdentity BaseWork(LongRestWorkKind kind, string item, CovenantDigest source) => new(Guid.Parse("66666666-6666-4666-8666-666666666666"),
        Guid.Parse("44444444-4444-4444-8444-444444444444"), item, kind, Scope,
        new(CanonicalCampaignContext.GlobalOnly, 128, Guid.Parse("11111111-1111-4111-8111-111111111111"), 1, 1, 1,
            new(Guid.Parse("55555555-5555-4555-8555-555555555555"), 7, Digest)), source, new CovenantDigest(Enumerable.Range(2, 32).Select(value => (byte)value).ToArray()));

    /// <summary>The adjudication payload the maintenance would build for <paramref name="pack"/> under <paramref name="budget"/>.</summary>
    internal static Result<LongRestProviderPayload> AdjudicationPayload(ImmutableArray<LongRestSourceLeaf> pack, int budget)
    {
        LongRestWorkIdentity work = BaseWork(LongRestWorkKind.Anchor, "anchor:00", LongRestDiscoveryHash.SourcePage(Scope, pack).Value);

        return LongRestProviderPayload.Adjudication(work, pack, budget);
    }

    internal static LongRestHierarchyChild Child(int index, string content)
    {
        CovenantDigest digest = new(Enumerable.Repeat((byte)(index % 250 + 1), 32).ToArray());

        return new(Guid.Parse(index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + "-0000-4000-8000-000000000000"), 0, index, digest, digest,
            content, ContentSensitivity.None, GenerationProvenance.CreateExact([]), [],
            [new("m-" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), "v-" + index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), Hash)]);
    }

    /// <summary>The summary payload the maintenance would build for one planned fold under <paramref name="budget"/>.</summary>
    internal static Result<LongRestProviderPayload> SummaryPayload(ImmutableArray<LongRestHierarchyChild> group, CovenantDigest recipe, int budget)
    {
        LongRestWorkIdentity work = BaseWork(LongRestWorkKind.HierarchyFold, "fold-1", Digest);

        LongRestHierarchyFold fold = new(work, 1, 0, LongRestHierarchy.Membership(group, recipe), group);

        fold = fold with { Work = work with { SourceDigest = LongRestDiscoveryHash.HierarchyFold(fold).Value } };

        return LongRestProviderPayload.Summary(fold, budget);
    }
}
