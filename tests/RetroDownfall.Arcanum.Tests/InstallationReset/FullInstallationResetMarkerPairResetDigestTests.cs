using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class FullInstallationResetMarkerPairResetDigestTests
{

    [Fact]

    public void Signed_attestation_projection_round_trips_every_detached_field_without_interpretation()
    {

        FullInstallationResetExternalRemediationAttestation attestation = new(
            Version: 7,
            OperationId: Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            InstallationId: Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
            HostToolsTransitionId: Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
            TaintMasterKeyVersion: 0x0102030405060708,
            AuthorityFingerprint: Digest(0x11),
            DatabaseMarkerDigest: Digest(0x22),
            OsMarkerDigest: Digest(0x33),
            RemediationActionDigest: Digest(0x44),
            NonceBase64Url: "nonce-spelling==",
            Issuer: "issuer spelling",
            IssuedAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(1),
            ExpiresAtUtc: DateTimeOffset.UnixEpoch.AddSeconds(2),
            SignatureBase64Url: "signature-spelling==");

        FullInstallationResetSignedAttestationProjectionV1 projection =
            FullInstallationResetSignedAttestationProjectionV1.FromAttestation(attestation);

        Assert.Equal(attestation.Version, projection.Version);

        Assert.Equal(attestation.OperationId, projection.OperationId);

        Assert.Equal(attestation.InstallationId, projection.InstallationId);

        Assert.Equal(attestation.HostToolsTransitionId, projection.HostToolsTransitionId);

        Assert.Equal(attestation.TaintMasterKeyVersion, projection.TaintMasterKeyVersion);

        Assert.Equal(attestation.AuthorityFingerprint, projection.AuthorityFingerprint);

        Assert.Equal(attestation.DatabaseMarkerDigest, projection.DatabaseMarkerDigest);

        Assert.Equal(attestation.OsMarkerDigest, projection.OsMarkerDigest);

        Assert.Equal(attestation.RemediationActionDigest, projection.RemediationActionDigest);

        Assert.Equal(attestation.NonceBase64Url, projection.NonceBase64Url);

        Assert.Equal(attestation.Issuer, projection.Issuer);

        Assert.Equal(attestation.IssuedAtUtc, projection.IssuedAtUtc);

        Assert.Equal(attestation.ExpiresAtUtc, projection.ExpiresAtUtc);

        Assert.Equal(attestation.SignatureBase64Url, projection.SignatureBase64Url);

        FullInstallationResetExternalRemediationAttestation roundTripped =
            projection.ToAttestation();

        Assert.Equal(attestation.Version, roundTripped.Version);

        Assert.Equal(attestation.OperationId, roundTripped.OperationId);

        Assert.Equal(attestation.InstallationId, roundTripped.InstallationId);

        Assert.Equal(attestation.HostToolsTransitionId, roundTripped.HostToolsTransitionId);

        Assert.Equal(attestation.TaintMasterKeyVersion, roundTripped.TaintMasterKeyVersion);

        Assert.Equal(attestation.AuthorityFingerprint, roundTripped.AuthorityFingerprint);

        Assert.Equal(attestation.DatabaseMarkerDigest, roundTripped.DatabaseMarkerDigest);

        Assert.Equal(attestation.OsMarkerDigest, roundTripped.OsMarkerDigest);

        Assert.Equal(attestation.RemediationActionDigest, roundTripped.RemediationActionDigest);

        Assert.Equal(attestation.NonceBase64Url, roundTripped.NonceBase64Url);

        Assert.Equal(attestation.Issuer, roundTripped.Issuer);

        Assert.Equal(attestation.IssuedAtUtc, roundTripped.IssuedAtUtc);

        Assert.Equal(attestation.ExpiresAtUtc, roundTripped.ExpiresAtUtc);

        Assert.Equal(attestation.SignatureBase64Url, roundTripped.SignatureBase64Url);

    }

    [Fact]

    public void Pair_evidence_preimage_uses_the_exact_fourteen_field_v1_order_and_encodings()
    {

        HostProcessToolsMatchedPair pair = MatchedPair();

        Result<byte[]> preimage =
            FullInstallationResetMarkerPairResetDigests.PairEvidencePreimage(pair);

        Result<CovenantDigest> digest =
            FullInstallationResetMarkerPairResetDigests.PairEvidence(pair);

        byte[] expectedPreimage = Convert.FromHexString(
            "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e"
            + "4d61726b65725061697245766964656e63652e7631000024313032313332"
            + "34332d353436352d373638372d393861392d626163626463656466653066"
            + "03ffeeddccbbaa998877665544332211000102030405060708111111111111"
            + "1111111111111111111111111111111111111111111111111111ab55ffcd"
            + "3d9425165e9b0d66e8ee519cf8c7419392082a498ab2c56fe7c046c3e479"
            + "d2fc4196ea2167e9e646432947da87980a76bbe96292307c43f13f801732"
            + "002431303231333234332d353436352d373638372d393861392d62616362"
            + "6463656466653066ffeeddccbbaa9988776655443322110001020304050607"
            + "08111111111111111111111111111111111111111111111111111111111111"
            + "11113333333333333333333333333333333333333333333333333333333333"
            + "33333355555555555555555555555555555555555555555555555555555555"
            + "55555555ab55ffcd3d9425165e9b0d66e8ee519cf8c7419392082a498ab2c"
            + "56fe7c046c3");

        Assert.True(preimage.IsSuccess, preimage.Error.Message);

        Assert.Equal(expectedPreimage, preimage.Value);

        Assert.True(digest.IsSuccess, digest.Error.Message);

        Assert.Equal(
            "B3AB6AEA1D497488C4233E3608ACBCC3FEA368130CE31BA888B8182ED64E3E2F",
            digest.Value.ToString());

    }

    [Fact]

    public void Pair_evidence_rejects_clean_partial_default_non_strict_or_oversized_evidence()
    {

        HostProcessToolsMatchedPair valid = MatchedPair();

        HostProcessToolsDatabaseMarkerEvidence pending = new(
            valid.Database.InstallationIdentity,
            CovenantHostToolsState.PendingHostToolsTaint,
            valid.Database.TransitionId,
            valid.Database.TaintMasterKeyVersion,
            valid.Database.TaintFingerprint);

        HostProcessToolsDatabaseMarkerEvidence clean = new(
            valid.Database.InstallationIdentity,
            CovenantHostToolsState.Clean,
            null,
            null,
            null);

        HostProcessToolsOsMarkerEvidence nonStrictMarker = new(
            "\uD800",
            valid.OsMarker.TransitionId,
            valid.OsMarker.TaintMasterKeyVersion,
            valid.OsMarker.TaintFingerprint,
            valid.OsMarker.MarkerBytesDigest,
            valid.OsMarker.DurableIdentityDigest);

        HostProcessToolsDatabaseMarkerEvidence nonStrictDatabase = new(
            "\uD800",
            CovenantHostToolsState.HostToolsTainted,
            valid.Database.TransitionId,
            valid.Database.TaintMasterKeyVersion,
            valid.Database.TaintFingerprint);

        HostProcessToolsOsMarkerEvidence mismatchedMarker = new(
            valid.OsMarker.InstallationIdentity,
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            valid.OsMarker.TaintMasterKeyVersion,
            valid.OsMarker.TaintFingerprint,
            valid.OsMarker.MarkerBytesDigest,
            valid.OsMarker.DurableIdentityDigest);

        HostProcessToolsMatchedPair[] rejected =
        [
            new(pending, valid.OsMarker),
            new(clean, valid.OsMarker),
            new(null!, valid.OsMarker),
            new(valid.Database, null!),
            new(nonStrictDatabase, nonStrictMarker),
            new(valid.Database, mismatchedMarker),
        ];

        Assert.All(rejected, static candidate =>
        {

            Assert.True(
                FullInstallationResetMarkerPairResetDigests
                    .PairEvidence(candidate).IsFailure);

        });

        Assert.Throws<ArgumentException>(() =>
            new HostProcessToolsDatabaseMarkerEvidence(
                new string('x', HostProcessToolsMarkerPayload.InstallationIdentityFieldBytes + 1),
                CovenantHostToolsState.HostToolsTainted,
                Guid.NewGuid(),
                1,
                Digest(0x11)));

    }

    [Fact]

    public void Campaign_display_path_digest_uses_strict_utf8_and_checked_uint16be_framing()
    {

        Result<byte[]> preimage =
            FullInstallationResetMarkerPairResetDigests.CampaignDisplayPathPreimage(
                "Café/🧙");

        Result<CovenantDigest> digest =
            FullInstallationResetMarkerPairResetDigests.CampaignDisplayPath(
                "Café/🧙");

        Assert.True(preimage.IsSuccess, preimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e"
                + "43616d706169676e446973706c6179506174682e763100000a436166c3a92f"
                + "f09fa799"),
            preimage.Value);

        Assert.True(digest.IsSuccess, digest.Error.Message);

        Assert.Equal(
            "2E3110A9A4237456530FBDB145120651BE9BFCAE2E7B10843FBB607ED0848636",
            digest.Value.ToString());

        Assert.True(
            FullInstallationResetMarkerPairResetDigests
                .CampaignDisplayPath("\uD800").IsFailure);

        Assert.True(
            FullInstallationResetMarkerPairResetDigests
                .CampaignDisplayPath(new string('x', ushort.MaxValue + 1)).IsFailure);

    }

    [Fact]

    public void Same_handle_ownership_digest_uses_one_retained_root_and_exact_network_order_fields()
    {

        Result<byte[]> preimage =
            FullInstallationResetMarkerPairResetDigests.SameHandleOwnershipPreimage(
                Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                0x0102030405060708,
                Digest(0x11),
                Digest(0x22),
                Digest(0x33),
                0x1112131415161718,
                0x2122232425262728);

        Result<CovenantDigest> digest =
            FullInstallationResetMarkerPairResetDigests.SameHandleOwnership(
                Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                0x0102030405060708,
                Digest(0x11),
                Digest(0x22),
                Digest(0x33),
                0x1112131415161718,
                0x2122232425262728);

        Assert.True(preimage.IsSuccess, preimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e"
                + "43616d706169676e4d61726b65724f776e6572736869702e76310000112233"
                + "445566778899aabbccddeeff0102030405060708111111111111111111111111"
                + "1111111111111111111111111111111111111111222222222222222222222222"
                + "2222222222222222222222222222222222222222333333333333333333333333"
                + "3333333333333333333333333333333333333333111213141516171821222324"
                + "25262728"),
            preimage.Value);

        Assert.True(digest.IsSuccess, digest.Error.Message);

        Assert.Equal(
            "A7E7638D8BE24BCA2E158B4B00A421C07A84312C2EFA2595B11A8493E7831898",
            digest.Value.ToString());

        Result<byte[]> zeroVolume =
            FullInstallationResetMarkerPairResetDigests.SameHandleOwnershipPreimage(
                Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                0x0102030405060708,
                Digest(0x11),
                Digest(0x22),
                Digest(0x33),
                0,
                0x2122232425262728);

        Result<byte[]> zeroFile =
            FullInstallationResetMarkerPairResetDigests.SameHandleOwnershipPreimage(
                Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                0x0102030405060708,
                Digest(0x11),
                Digest(0x22),
                Digest(0x33),
                0x1112131415161718,
                0);

        Assert.True(zeroVolume.IsSuccess, zeroVolume.Error.Message);

        Assert.Equal(
            Convert.FromHexString("00000000000000002122232425262728"),
            zeroVolume.Value[^16..]);

        Assert.True(zeroFile.IsSuccess, zeroFile.Error.Message);

        Assert.Equal(
            Convert.FromHexString("11121314151617180000000000000000"),
            zeroFile.Value[^16..]);

    }

    [Fact]

    public void Campaign_inventory_entry_digest_uses_the_exact_six_entry_fields_without_count()
    {

        CampaignMarkerInventoryEntryV1 entry = InventoryEntry(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            0x0102030405060708,
            0x11);

        Result<byte[]> preimage =
            FullInstallationResetMarkerPairResetDigests.CampaignInventoryEntryPreimage(
                entry);

        Result<CovenantDigest> digest =
            FullInstallationResetMarkerPairResetDigests.CampaignInventoryEntry(entry);

        Assert.True(preimage.IsSuccess, preimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e4361"
                + "6d706169676e4d61726b6572496e76656e746f7279456e7472792e7631000011"
                + "2233445566778899aabbccddeeff010203040506070811111111111111111111"
                + "1111111111111111111111111111111111111111111122222222222222222222"
                + "2222222222222222222222222222222222222222222233333333333333333333"
                + "3333333333333333333333333333333333333333333344444444444444444444"
                + "44444444444444444444444444444444444444444444"),
            preimage.Value);

        Assert.True(digest.IsSuccess, digest.Error.Message);

        Assert.Equal(
            "5AF51D5E2D119130FAFEF169AE9EDC9058B0508D55ABC08532516782A45CD055",
            digest.Value.ToString());

    }

    [Fact]

    public void Campaign_inventory_digest_freezes_zero_one_many_and_rfc4122_uuid_order()
    {

        Guid firstByNetworkOrder =
            Guid.Parse("00000001-0000-0000-0000-000000000000");

        Guid secondByNetworkOrder =
            Guid.Parse("00000100-0000-0000-0000-000000000000");

        Assert.True(firstByNetworkOrder.CompareTo(secondByNetworkOrder) < 0);

        Assert.True(
            firstByNetworkOrder.ToByteArray().AsSpan().SequenceCompareTo(
                secondByNetworkOrder.ToByteArray()) > 0);

        ImmutableArray<CampaignMarkerInventoryEntryV1> one =
        [
            InventoryEntry(
                Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                0x0102030405060708,
                0x11),
        ];

        ImmutableArray<CampaignMarkerInventoryEntryV1> many =
        [
            InventoryEntryConsecutive(firstByNetworkOrder, 1, 0x10),
            InventoryEntryConsecutive(secondByNetworkOrder, 2, 0x20),
        ];

        Result<byte[]> zeroPreimage =
            FullInstallationResetMarkerPairResetDigests.CampaignInventoryPreimage([]);

        Result<byte[]> manyPreimage =
            FullInstallationResetMarkerPairResetDigests.CampaignInventoryPreimage(many);

        Result<CovenantDigest> zero =
            FullInstallationResetMarkerPairResetDigests.CampaignInventory([]);

        Result<CovenantDigest> single =
            FullInstallationResetMarkerPairResetDigests.CampaignInventory(one);

        Result<CovenantDigest> multiple =
            FullInstallationResetMarkerPairResetDigests.CampaignInventory(many);

        Assert.True(zeroPreimage.IsSuccess, zeroPreimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e"
                + "43616d706169676e4d61726b6572496e76656e746f72792e76310000000000"
                + "00000000"),
            zeroPreimage.Value);

        Assert.True(manyPreimage.IsSuccess, manyPreimage.Error.Message);

        const int domainAndCountBytes = 65;

        const int entryBytes = 16 + 8 + (4 * 32);

        Assert.Equal(
            Convert.FromHexString("00000001000000000000000000000000"),
            manyPreimage.Value.AsSpan(domainAndCountBytes, 16).ToArray());

        Assert.Equal(
            Convert.FromHexString("00000100000000000000000000000000"),
            manyPreimage.Value.AsSpan(domainAndCountBytes + entryBytes, 16).ToArray());

        Assert.True(zero.IsSuccess, zero.Error.Message);

        Assert.True(single.IsSuccess, single.Error.Message);

        Assert.True(multiple.IsSuccess, multiple.Error.Message);

        Assert.Equal(
            "8B9C3023A5D8AA024F03EBC93BD724E7B025452F7D11A4EB7FDEC5858E1E15CD",
            zero.Value.ToString());

        Assert.Equal(
            "EF7EA3BE703D48F1DC269F02B2EAA299A3A1501AF8016B5672234F8CFCE44E41",
            single.Value.ToString());

        Assert.Equal(
            "D6EAC176EB0D18FF58596F04EE113B88FC51DE5875B4105ED760A38D220D9ABD",
            multiple.Value.ToString());

    }

    [Fact]

    public void Campaign_inventory_rejects_default_duplicate_reordered_zero_revision_invalid_or_oversized_vectors()
    {

        CampaignMarkerInventoryEntryV1 first =
            InventoryEntryConsecutive(
                Guid.Parse("00000001-0000-0000-0000-000000000000"),
                1,
                0x10);

        CampaignMarkerInventoryEntryV1 second =
            InventoryEntryConsecutive(
                Guid.Parse("00000100-0000-0000-0000-000000000000"),
                2,
                0x20);

        ImmutableArray<CampaignMarkerInventoryEntryV1>[] rejected =
        [
            default,
            [null!],
            [first, first],
            [second, first],
            [first with { CampaignId = Guid.Empty }],
            [first with { PriorPathRevision = 0 }],
            [first with { PriorPathRevision = -1 }],
            [first with { MarkerDigest = default }],
            [first with { IndexedPhysicalIdentityDigest = default }],
            [first with { CanonicalDisplayPathDigest = default }],
            [first with { SameHandleOwnershipEvidenceDigest = default }],
        ];

        Assert.All(rejected, static candidate =>
        {

            Assert.True(
                FullInstallationResetMarkerPairResetDigests
                    .CampaignInventory(candidate).IsFailure);

        });

        CampaignMarkerInventoryEntryV1[] oversized = Enumerable
            .Range(1, 4097)
            .Select(static value => InventoryEntryConsecutive(
                Guid.Parse($"{value:x8}-0000-0000-0000-000000000000"),
                value,
                0x10))
            .ToArray();

        Assert.True(
            FullInstallationResetMarkerPairResetDigests
                .CampaignInventory(ImmutableArray.Create(oversized)).IsFailure);

    }

    [Fact]

    public void Full_reset_effect_digest_uses_only_the_ten_unframed_fields_and_scope_byte_01()
    {

        CovenantDigest remediationAction = new(Convert.FromHexString(
            "761e8536128080d5936070524da90a6558b8901ea46d93194646b413bb27a1d9"));

        Result<byte[]> preimage =
            FullInstallationResetMarkerPairResetDigests.FullResetEffectPreimage(
                Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
                Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
                0x0102030405060708,
                Digest(0x11),
                Digest(0x22),
                Digest(0x33),
                remediationAction,
                Digest(0x55));

        Result<CovenantDigest> digest =
            FullInstallationResetMarkerPairResetDigests.FullResetEffect(
                Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
                Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
                0x0102030405060708,
                Digest(0x11),
                Digest(0x22),
                Digest(0x33),
                remediationAction,
                Digest(0x55));

        Assert.True(preimage.IsSuccess, preimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e4566"
                + "666563742e76310000112233445566778899aabbccddeeff1021324354657687"
                + "98a9bacbdcedfe0fffeeddccbbaa998877665544332211000102030405060708"
                + "1111111111111111111111111111111111111111111111111111111111111111"
                + "2222222222222222222222222222222222222222222222222222222222222222"
                + "3333333333333333333333333333333333333333333333333333333333333333"
                + "761e8536128080d5936070524da90a6558b8901ea46d93194646b413bb27a1d9"
                + "5555555555555555555555555555555555555555555555555555555555555555"
                + "01"),
            preimage.Value);

        Assert.True(digest.IsSuccess, digest.Error.Message);

        Assert.Equal(
            "7BCDB874455467AAAD6FC1B6CE8A5A2CCFBD08CBA6EB3FBD7D9CB2A796CA07EA",
            digest.Value.ToString());

    }

    [Fact]

    public void Full_reset_intent_vector_freezes_the_empty_literal_and_preserves_authenticated_campaign_order()
    {

        ImmutableArray<Guid> authenticatedOrder =
        [
            Guid.Parse("00000100-0000-0000-0000-000000000000"),
            Guid.Parse("00000001-0000-0000-0000-000000000000"),
        ];

        Result<byte[]> emptyPreimage =
            FullInstallationResetMarkerPairResetDigests.FullResetIntentVectorPreimage([]);

        Result<byte[]> orderedPreimage =
            FullInstallationResetMarkerPairResetDigests.FullResetIntentVectorPreimage(
                authenticatedOrder);

        Result<CovenantDigest> empty =
            FullInstallationResetMarkerPairResetDigests.FullResetIntentVector([]);

        Result<CovenantDigest> ordered =
            FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(
                authenticatedOrder);

        Assert.True(emptyPreimage.IsSuccess, emptyPreimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e"
                + "43616d706169676e4d61726b6572496e74656e74566563746f722e76310000"
                + "00000000000000"),
            emptyPreimage.Value);

        Assert.True(orderedPreimage.IsSuccess, orderedPreimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e4361"
                + "6d706169676e4d61726b6572496e74656e74566563746f722e76310000000000"
                + "0000000200000100000000000000000000000000000000010000000000000000"
                + "00000000"),
            orderedPreimage.Value);

        Assert.True(empty.IsSuccess, empty.Error.Message);

        Assert.True(ordered.IsSuccess, ordered.Error.Message);

        Assert.Equal(
            "26B63BE668FE309ADD01922EA6DD3FEFE222C7833FF9DFA379BDA0275CF98574",
            empty.Value.ToString());

        Assert.Equal(
            "7DEF0FC7393907585D3D1D086C88D4598874464AC6EE8659119670709B6E2B72",
            ordered.Value.ToString());

    }

    [Fact]

    public void Full_reset_intent_vector_rejects_default_zero_duplicate_or_more_than_4096_ids()
    {

        Guid intentId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

        ImmutableArray<Guid>[] rejected =
        [
            default,
            [Guid.Empty],
            [intentId, intentId],
        ];

        Assert.All(rejected, static candidate =>
        {

            Assert.True(
                FullInstallationResetMarkerPairResetDigests
                    .FullResetIntentVector(candidate).IsFailure);

        });

        Guid[] oversized = Enumerable.Range(1, 4097)
            .Select(static value =>
                Guid.Parse($"{value:x8}-0000-0000-0000-000000000000"))
            .ToArray();

        Assert.True(
            FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(
                ImmutableArray.Create(oversized)).IsFailure);

    }

    [Fact]

    public void Campaign_observation_digest_has_exact_opened_and_blocked_code_dependent_preimages()
    {

        Result<byte[]> openedPreimage =
            FullInstallationResetMarkerPairResetDigests.CampaignObservationPreimage(
                CampaignPathFullResetCleanupObservationCode.Opened,
                Digest(0x11),
                Digest(0x22));

        Result<byte[]> unavailablePreimage =
            FullInstallationResetMarkerPairResetDigests.CampaignObservationPreimage(
                CampaignPathFullResetCleanupObservationCode.Unavailable,
                Digest(0x11),
                null);

        Result<byte[]> mismatchPreimage =
            FullInstallationResetMarkerPairResetDigests.CampaignObservationPreimage(
                CampaignPathFullResetCleanupObservationCode.Mismatch,
                Digest(0x11),
                null);

        Result<CovenantDigest> opened =
            FullInstallationResetMarkerPairResetDigests.CampaignObservation(
                CampaignPathFullResetCleanupObservationCode.Opened,
                Digest(0x11),
                Digest(0x22));

        Result<CovenantDigest> unavailable =
            FullInstallationResetMarkerPairResetDigests.CampaignObservation(
                CampaignPathFullResetCleanupObservationCode.Unavailable,
                Digest(0x11),
                null);

        Result<CovenantDigest> mismatch =
            FullInstallationResetMarkerPairResetDigests.CampaignObservation(
                CampaignPathFullResetCleanupObservationCode.Mismatch,
                Digest(0x11),
                null);

        Assert.True(openedPreimage.IsSuccess, openedPreimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e"
                + "43616d706169676e4d61726b65724f62736572766174696f6e2e7631000111"
                + "1111111111111111111111111111111111111111111111111111111111111122"
                + "22222222222222222222222222222222222222222222222222222222222222"),
            openedPreimage.Value);

        Assert.True(unavailablePreimage.IsSuccess, unavailablePreimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e"
                + "43616d706169676e4d61726b65724f62736572766174696f6e2e7631000211"
                + "11111111111111111111111111111111111111111111111111111111111111"),
            unavailablePreimage.Value);

        Assert.True(mismatchPreimage.IsSuccess, mismatchPreimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e"
                + "43616d706169676e4d61726b65724f62736572766174696f6e2e7631000311"
                + "11111111111111111111111111111111111111111111111111111111111111"),
            mismatchPreimage.Value);

        Assert.True(opened.IsSuccess, opened.Error.Message);

        Assert.True(unavailable.IsSuccess, unavailable.Error.Message);

        Assert.True(mismatch.IsSuccess, mismatch.Error.Message);

        Assert.Equal(
            "435A9E093DA1CE3E5BE1580EB97A4C8A5D6430EB9EDF2A5416E879DC231A98C9",
            opened.Value.ToString());

        Assert.Equal(
            "1250B0BCA0C274F5D99312674B18B4CE01A67404936D18B02F50E255C606E2A4",
            unavailable.Value.ToString());

        Assert.Equal(
            "FD9DAE3975CBF7D64EE94713B4320727E4AC64EFEC1CFC507EE68F5B145AF8B2",
            mismatch.Value.ToString());

        Assert.True(
            FullInstallationResetMarkerPairResetDigests.CampaignObservation(
                CampaignPathFullResetCleanupObservationCode.Opened,
                Digest(0x11),
                null).IsFailure);

        Assert.True(
            FullInstallationResetMarkerPairResetDigests.CampaignObservation(
                CampaignPathFullResetCleanupObservationCode.Unavailable,
                Digest(0x11),
                Digest(0x22)).IsFailure);

    }

    [Fact]

    public void Owner_effect_rejects_a_same_shaped_digest_from_another_lifecycle_domain()
    {

        Result<CovenantDigest> result =
            FullInstallationResetMarkerPairResetDigests.FullResetEffect(
                Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
                Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"),
                Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"),
                0x0102030405060708,
                Digest(0x11),
                Digest(0x22),
                Digest(0x33),
                Digest(0x44),
                Digest(0x55));

        Assert.True(result.IsFailure);

    }

    private static HostProcessToolsMatchedPair MatchedPair()
    {

        const string installation = "10213243-5465-7687-98a9-bacbdcedfe0f";

        Guid transition = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");

        CovenantDigest fingerprint = Digest(0x11);

        HostProcessToolsDatabaseMarkerEvidence database = new(
            installation,
            CovenantHostToolsState.HostToolsTainted,
            transition,
            0x0102030405060708,
            fingerprint);

        HostProcessToolsOsMarkerEvidence marker = new(
            installation,
            transition,
            0x0102030405060708,
            fingerprint,
            Digest(0x33),
            Digest(0x55));

        return new HostProcessToolsMatchedPair(database, marker);

    }

    private static CampaignMarkerInventoryEntryV1 InventoryEntry(
        Guid campaignId,
        long revision,
        byte firstDigest) =>
        new(
            campaignId,
            revision,
            Digest(firstDigest),
            Digest(checked((byte)(firstDigest + 0x11))),
            Digest(checked((byte)(firstDigest + 0x22))),
            Digest(checked((byte)(firstDigest + 0x33))));

    private static CampaignMarkerInventoryEntryV1 InventoryEntryConsecutive(
        Guid campaignId,
        long revision,
        byte firstDigest) =>
        new(
            campaignId,
            revision,
            Digest(firstDigest),
            Digest(checked((byte)(firstDigest + 1))),
            Digest(checked((byte)(firstDigest + 2))),
            Digest(checked((byte)(firstDigest + 3))));

    private static CovenantDigest Digest(byte value) =>
        new([.. Enumerable.Repeat(value, 32)]);

}
