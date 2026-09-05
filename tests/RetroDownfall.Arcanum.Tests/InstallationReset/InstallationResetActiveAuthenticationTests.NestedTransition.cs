using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// Payload validation for the nested offline-transition receipt.
/// </summary>
/// <remarks>
/// The receipt is the outer workflow's half of a nested healthy-catalog factory erasure. Its two
/// phases are three distinct facts once absence is counted: no nested transition was ever started, one
/// was started and has not reported, and one completed. Only the first and third may let a reset go
/// on, so the validator has to keep them apart rather than treat a blank receipt as a benign default.
///
/// <para>The class is a partial of the authentication suite so it can reuse the fixture payload the
/// other checkpoint tests are written against. A filter derived from this file's name matches nothing
/// — filter on <c>InstallationResetActiveAuthenticationTests</c> instead.</para>
/// </remarks>
public sealed partial class InstallationResetActiveAuthenticationTests
{

    [Fact]
    public void A_nested_receipt_is_absent_claimed_or_completed_and_nothing_between()
    {

        InstallationResetActivePayloadV3 absent = FixturePayload();

        Assert.Null(absent.NestedTransitionReceipt);

        Assert.True(
            InstallationResetActiveRecordAuthenticator.ValidatePayload(absent).IsSuccess);

        Assert.True(
            InstallationResetActiveRecordAuthenticator.ValidatePayload(
                absent with { NestedTransitionReceipt = Claimed() }).IsSuccess);

        Assert.True(
            InstallationResetActiveRecordAuthenticator.ValidatePayload(
                absent with { NestedTransitionReceipt = Completed() }).IsSuccess);

        // A claim carries no outcome and a completion carries both halves of one. Every mixture is a
        // receipt describing a state the nested transition was never in.
        InstallationResetNestedTransitionReceiptV1[] invalid =
        [
            Claimed() with { Version = 2 },
            Claimed() with { NestedOperationId = Guid.Empty },
            Claimed() with { Phase = (InstallationResetNestedTransitionPhase)7 },
            Claimed() with { NestedEffectDigest = DigestRange(0x21) },
            Claimed() with { TerminalWinnerDigest = DigestRange(0x22) },
            Completed() with { NestedEffectDigest = null },
            Completed() with { TerminalWinnerDigest = null },
            Completed() with { NestedEffectDigest = default(CovenantDigest) },
            Completed() with { TerminalWinnerDigest = default(CovenantDigest) },
        ];

        Assert.All(
            invalid,
            receipt => Assert.True(
                InstallationResetActiveRecordAuthenticator
                    .ValidatePayload(absent with { NestedTransitionReceipt = receipt })
                    .IsFailure));

    }

    [Fact]
    public void A_legacy_payload_version_may_be_read_but_may_not_carry_a_receipt()
    {

        InstallationResetActivePayloadV3 legacy = FixturePayload() with
        {
            Version = InstallationResetActiveRecordAuthenticator.LegacyPayloadVersion,
        };

        // The value 2 stays readable because a record written before this build exists is still that
        // installation's evidence. What it may not do is carry a member it could not have written:
        // that combination is a record somebody assembled, not one that was ever sealed.
        Assert.True(
            InstallationResetActiveRecordAuthenticator.ValidatePayload(legacy).IsSuccess);

        Assert.True(
            InstallationResetActiveRecordAuthenticator
                .ValidatePayload(legacy with { NestedTransitionReceipt = Completed() })
                .IsFailure);

        Assert.True(
            InstallationResetActiveRecordAuthenticator
                .ValidatePayload(legacy with { Version = 1 })
                .IsFailure);

        Assert.True(
            InstallationResetActiveRecordAuthenticator
                .ValidatePayload(legacy with { Version = 4 })
                .IsFailure);

    }

    [Fact]
    public void A_checkpoint_bearing_record_sealed_before_this_build_still_opens()
    {

        // The authenticated open requires the decrypted plaintext to re-serialize byte for byte, so a
        // member that appeared unconditionally would change the canonical spelling of every checkpoint
        // sealed before it existed — and those records would then be refused as if they had been
        // tampered with, which is the one class of record the legacy version exists to rescue.
        InstallationResetActivePayloadV3 current = CheckpointPayload();

        Assert.NotNull(current.HostToolsMarkerPairReset);

        Assert.Null(current.HostToolsMarkerPairReset.TransitionTerminal);

        string canonical = Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(
                current,
                InstallationResetActiveJsonContext.Default.InstallationResetActivePayloadV3));

        Assert.DoesNotContain("transitionTerminal", canonical, StringComparison.Ordinal);

        // And the same for the payload's own new member, which an older record also never carried.
        Assert.DoesNotContain("nestedTransitionReceipt", canonical, StringComparison.Ordinal);

        InstallationResetActivePayloadV3 sealedRoundTrip = Value(
            JsonSerializer.Deserialize(
                canonical,
                InstallationResetActiveJsonContext.Default.InstallationResetActivePayloadV3));

        Assert.Equal(
            canonical,
            Encoding.UTF8.GetString(
                JsonSerializer.SerializeToUtf8Bytes(
                    sealedRoundTrip,
                    InstallationResetActiveJsonContext.Default.InstallationResetActivePayloadV3)));

    }

    private static T Value<T>(T? value)
        where T : class
    {

        Assert.NotNull(value);

        return value;

    }

    private static InstallationResetNestedTransitionReceiptV1 Claimed() =>
        new(
            Version: 1,
            NestedOperationId: Guid.Parse("2f9d1c0b-4a5e-6d7f-8091-a2b3c4d5e6f7"),
            Phase: InstallationResetNestedTransitionPhase.Claimed,
            NestedEffectDigest: null,
            TerminalWinnerDigest: null);

    private static InstallationResetNestedTransitionReceiptV1 Completed() =>
        Claimed() with
        {
            Phase = InstallationResetNestedTransitionPhase.Completed,
            NestedEffectDigest = DigestRange(0x31),
            TerminalWinnerDigest = DigestRange(0x41),
        };

}
