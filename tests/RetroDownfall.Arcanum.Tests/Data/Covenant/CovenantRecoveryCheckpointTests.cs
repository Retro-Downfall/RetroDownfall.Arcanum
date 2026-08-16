using System.Text;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

/// <summary>
/// Issue #88 — the durable Covenant recovery checkpoints and the Infrastructure context that owns
/// them.
/// </summary>
/// <remarks>
/// The defect these prevent is a recovery that resumes from a payload it only half understood. A
/// checkpoint is read by a process that may be several releases newer than the one that wrote it, so
/// a field it does not recognize is not noise: it is an invariant the writer was maintaining and this
/// build is not. Silently dropping it would resume a database-replacing operation with one of its
/// guarantees missing, which is strictly worse than refusing to resume at all.
/// </remarks>
public sealed class CovenantRecoveryCheckpointTests
{

    private static readonly Guid Dataset = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static CovenantIndexRebuildCheckpointV1 Rebuild() =>
        new(
            CovenantIndexRebuildCheckpointV1.CurrentVersion,
            Dataset,
            AcceleratorEpoch: 7,
            BaseTargetSearchSequence: 4_096,
            CapturedCoreCampaignDeletionSequence: 12,
            CovenantIndexRebuildPhase.DeltaCatchUp,
            BaseScanAfterSearchRowId: 512,
            LastContiguousAppliedSequence: 4_000,
            BaseHeadsProcessed: 300,
            BaseHeadsTotal: 512,
            DeltaRowsProcessed: 96);

    private static CovenantFamilyReinitializeCheckpointV1 Reinitialize() =>
        new(
            CovenantFamilyReinitializeCheckpointV1.CurrentVersion,
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            InstallationIdentity: "installation-a",
            AuthorityEpoch: 9,
            DatabaseFileIdentityDigest: new string('a', 64),
            InspectedCatalogDigest: new string('b', 64),
            EffectDigest: new string('c', 64),
            OldDatasetGeneration: Dataset,
            NewDatasetGeneration: null,
            CovenantFamilyReinitializePhase.FamilyDropped,
            ManagedArtifactCursor: 3,
            OldFamilyDropped: true,
            CanonicalInstalled: false,
            AcceleratorInstalled: false,
            CompactedFileIdentityDigest: null,
            RetryCount: 1,
            LastDurableErrorCode: null);

    [Fact]
    public void An_index_rebuild_checkpoint_round_trips_through_its_owning_context()
    {

        byte[] encoded = CovenantRecoveryCheckpointCodec.Encode(Rebuild());

        Result<CovenantIndexRebuildCheckpointV1> decoded =
            CovenantRecoveryCheckpointCodec.DecodeIndexRebuild(encoded);

        Assert.True(decoded.IsSuccess);

        Assert.Equal(Rebuild(), decoded.Value);

    }

    [Fact]
    public void A_family_reinitialize_checkpoint_round_trips_through_its_owning_context()
    {

        byte[] encoded = CovenantRecoveryCheckpointCodec.Encode(Reinitialize());

        Result<CovenantFamilyReinitializeCheckpointV1> decoded =
            CovenantRecoveryCheckpointCodec.DecodeFamilyReinitialize(encoded);

        Assert.True(decoded.IsSuccess);

        Assert.Equal(Reinitialize(), decoded.Value);

    }

    [Fact]
    public void Phases_travel_as_names_so_a_reordered_enum_cannot_silently_change_a_resume_point()
    {

        string json = Encoding.UTF8.GetString(CovenantRecoveryCheckpointCodec.Encode(Rebuild()));

        Assert.Contains("\"phase\":\"DeltaCatchUp\"", json, StringComparison.Ordinal);

        Assert.DoesNotContain("\"phase\":2", json, StringComparison.Ordinal);

    }

    [Fact]
    public void A_numeric_phase_is_refused()
    {

        byte[] payload = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(CovenantRecoveryCheckpointCodec.Encode(Rebuild()))
                .Replace("\"phase\":\"DeltaCatchUp\"", "\"phase\":2", StringComparison.Ordinal));

        Result<CovenantIndexRebuildCheckpointV1> decoded =
            CovenantRecoveryCheckpointCodec.DecodeIndexRebuild(payload);

        Assert.True(decoded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, decoded.Error.Code);

    }

    [Fact]
    public void An_unknown_field_fails_recovery_rather_than_being_dropped()
    {

        string json = Encoding.UTF8.GetString(CovenantRecoveryCheckpointCodec.Encode(Rebuild()));

        byte[] payload = Encoding.UTF8.GetBytes(json.Insert(1, "\"unmappedInvariant\":true,"));

        Result<CovenantIndexRebuildCheckpointV1> decoded =
            CovenantRecoveryCheckpointCodec.DecodeIndexRebuild(payload);

        Assert.True(decoded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, decoded.Error.Code);

    }

    [Fact]
    public void A_future_version_discriminator_fails_recovery()
    {

        byte[] payload = CovenantRecoveryCheckpointCodec.Encode(Rebuild() with { Version = 2 });

        Result<CovenantIndexRebuildCheckpointV1> decoded =
            CovenantRecoveryCheckpointCodec.DecodeIndexRebuild(payload);

        Assert.True(decoded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, decoded.Error.Code);

    }

    [Fact]
    public void An_oversized_payload_is_refused_before_it_is_parsed()
    {

        byte[] payload = new byte[CovenantRecoveryJsonContext.MaxCheckpointBytes + 1];

        Result<CovenantIndexRebuildCheckpointV1> decoded =
            CovenantRecoveryCheckpointCodec.DecodeIndexRebuild(payload);

        Assert.True(decoded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, decoded.Error.Code);

    }

    [Fact]
    public void Malformed_bytes_fail_as_a_typed_result_rather_than_an_escaping_exception()
    {

        Result<CovenantFamilyReinitializeCheckpointV1> decoded =
            CovenantRecoveryCheckpointCodec.DecodeFamilyReinitialize("not json"u8.ToArray());

        Assert.True(decoded.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, decoded.Error.Code);

    }

    /// <summary>
    /// Both shapes are fixed-width by construction, so the cap has to leave real headroom over the
    /// largest legitimate payload. A cap that a valid checkpoint could cross would turn the safety
    /// bound into an outage.
    /// </summary>
    [Fact]
    public void The_largest_legitimate_checkpoint_fits_well_inside_the_cap()
    {

        int rebuild = CovenantRecoveryCheckpointCodec.Encode(Rebuild()).Length;

        int reinitialize = CovenantRecoveryCheckpointCodec
            .Encode(Reinitialize() with
            {
                NewDatasetGeneration = Guid.NewGuid(),
                CompactedFileIdentityDigest = new string('d', 64),
                LastDurableErrorCode = ErrorCodes.Covenant.MaintenanceFailed,
            })
            .Length;

        Assert.True(rebuild * 2 < CovenantRecoveryJsonContext.MaxCheckpointBytes);

        Assert.True(reinitialize * 2 < CovenantRecoveryJsonContext.MaxCheckpointBytes);

    }

}
