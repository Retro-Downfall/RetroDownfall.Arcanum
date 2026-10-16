using System.Security.Cryptography;
using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed class LongRestSweepFenceTests
{
    // Independently encoded from the approved Work.v2 grammar (the v1 fields, then the anchor unit's pairing floor as a final Int32BE), without the production encoder.
    private const string Preimage = "417263616E756D2E4C6F6E67526573742E576F726B2E763200000000010000002436363636363636362D363636362D343636362D383636362D3636363636363636363636360000002434343434343434342D343434342D343434342D383434342D34343434343434343434343400000006666F6C642D3100000002000000010000000000010000000000000000000000800000002431313131313131312D313131312D343131312D383131312D3131313131313131313131310000000000000001000000000000000100000000000000010000002435353535353535352D353535352D343535352D383535352D35353535353535353535353500000000000000070102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F200102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F2002030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F202100000000";

    [Fact]
    public void Work_v2_uses_only_authoritative_dataset_and_independent_process_facts_and_the_pairing_floor()
    {
        Assert.Equal(357, Convert.FromHexString(Preimage).Length);

        Assert.Equal("488B972B44407A23EBF67CE4FBE53FDBF5F61B667F72A0F0571B5CB7299A5A0F", Convert.ToHexString(SHA256.HashData(Convert.FromHexString(Preimage))));

        Assert.Equal("488B972B44407A23EBF67CE4FBE53FDBF5F61B667F72A0F0571B5CB7299A5A0F", LongRestDiscoveryHash.Work(Work()).Value.ToString());
    }

    [Fact]
    public void Stored_fence_serialization_contains_no_unowned_corpus_or_restore_scalar()
    {
        string json = JsonSerializer.Serialize(LongRestStoredFence.From(Work().Fence), LongRestJsonContext.Default.LongRestStoredFence);

        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(new[] { "campaignAvailabilityGeneration", "pathIdentityPolicyVersion", "pathIdentityRevision", "rootIdentityDigest", "insertionHighWatermark", "datasetGeneration", "admissionGeneration", "runtimeAuthorityGeneration", "capabilityGeneration", "configuration" }, document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    private static LongRestWorkIdentity Work()
    {
        CovenantDigest digest = new(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());

        LongRestSweepScope scope = LongRestSweepScope.Create(new(LongRestSweepScopeKind.Global, null)).Value;

        LongRestConfigurationStamp configuration = new(Guid.Parse("55555555-5555-4555-8555-555555555555"), 7, digest);

        LongRestSweepFence fence = new(CanonicalCampaignContext.GlobalOnly, 128, Guid.Parse("11111111-1111-4111-8111-111111111111"), 1, 1, 1, configuration);

        return new(Guid.Parse("66666666-6666-4666-8666-666666666666"), Guid.Parse("44444444-4444-4444-8444-444444444444"), "fold-1", LongRestWorkKind.HierarchyFold, scope, fence, digest, new CovenantDigest(Enumerable.Range(2, 32).Select(value => (byte)value).ToArray()));
    }
}
