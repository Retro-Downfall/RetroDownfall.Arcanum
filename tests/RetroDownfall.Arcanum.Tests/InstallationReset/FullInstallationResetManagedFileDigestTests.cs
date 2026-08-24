using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class FullInstallationResetManagedFileDigestTests
{

    private static readonly Guid FirstByNetworkOrder =
        Guid.Parse("00000001-0000-0000-0000-000000000000");

    private static readonly Guid SecondByNetworkOrder =
        Guid.Parse("00000100-0000-0000-0000-000000000000");

    private static readonly Guid FirstWorkItem =
        Guid.Parse("0a0b0c0d-0e0f-1011-1213-141516171819");

    private static readonly Guid SecondWorkItem =
        Guid.Parse("1a1b1c1d-1e1f-2021-2223-242526272829");

    [Fact]

    public void Source_write_intent_vector_freezes_its_domain_count_and_rfc4122_order()
    {

        Result<byte[]> emptyPreimage =
            FullInstallationResetManagedFileDigests.SourceWriteIntentVectorPreimage([]);

        Assert.True(emptyPreimage.IsSuccess, emptyPreimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e"
                + "4d616e616765645772697465496e74656e74566563746f722e7631000000"
                + "000000000000"),
            emptyPreimage.Value);

        Result<byte[]> manyPreimage =
            FullInstallationResetManagedFileDigests.SourceWriteIntentVectorPreimage(
                [FirstByNetworkOrder, SecondByNetworkOrder]);

        Assert.True(manyPreimage.IsSuccess, manyPreimage.Error.Message);

        const int domainAndCountBytes = 66;

        Assert.Equal(
            Convert.FromHexString("00000001000000000000000000000000"),
            manyPreimage.Value.AsSpan(domainAndCountBytes, 16).ToArray());

        Assert.Equal(
            Convert.FromHexString("00000100000000000000000000000000"),
            manyPreimage.Value.AsSpan(domainAndCountBytes + 16, 16).ToArray());

        Result<CovenantDigest> zero =
            FullInstallationResetManagedFileDigests.SourceWriteIntentVector([]);

        Result<CovenantDigest> single =
            FullInstallationResetManagedFileDigests.SourceWriteIntentVector(
                [Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")]);

        Result<CovenantDigest> multiple =
            FullInstallationResetManagedFileDigests.SourceWriteIntentVector(
                [FirstByNetworkOrder, SecondByNetworkOrder]);

        Assert.True(zero.IsSuccess, zero.Error.Message);

        Assert.True(single.IsSuccess, single.Error.Message);

        Assert.True(multiple.IsSuccess, multiple.Error.Message);

        Assert.Equal(
            "E61C4FB05091CDACC8DE0EB780B9F8EC6A6309E25342C2045D5A289E7E27C762",
            zero.Value.ToString());

        Assert.Equal(
            "83147CA4CFFA367EDF2DDF22931C9B2092EBFCEEE4ED041ECD0BCB5FC5ABEB47",
            single.Value.ToString());

        Assert.Equal(
            "FCC0654D965316BF5D23AA2C42D8E2365854F602B175B65AAFD8E6555AECD9DF",
            multiple.Value.ToString());

    }

    [Fact]

    public void Local_erasure_work_item_vector_uses_its_own_domain_and_never_collides_with_the_source_vector()
    {

        Result<byte[]> emptyPreimage =
            FullInstallationResetManagedFileDigests.LocalErasureWorkItemVectorPreimage([]);

        Assert.True(emptyPreimage.IsSuccess, emptyPreimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e"
                + "4c6f63616c45726173757265576f726b4974656d566563746f722e763100"
                + "0000000000000000"),
            emptyPreimage.Value);

        Result<CovenantDigest> zero =
            FullInstallationResetManagedFileDigests.LocalErasureWorkItemVector([]);

        Result<CovenantDigest> single =
            FullInstallationResetManagedFileDigests.LocalErasureWorkItemVector(
                [Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")]);

        Result<CovenantDigest> multiple =
            FullInstallationResetManagedFileDigests.LocalErasureWorkItemVector(
                [FirstByNetworkOrder, SecondByNetworkOrder]);

        Assert.True(zero.IsSuccess, zero.Error.Message);

        Assert.True(single.IsSuccess, single.Error.Message);

        Assert.True(multiple.IsSuccess, multiple.Error.Message);

        Assert.Equal(
            "EB0C1CAB2B2E30BBAE9255B192BEF082213BE61C8C0182D8621EE724C8AD3A73",
            zero.Value.ToString());

        Assert.Equal(
            "F1F4EC3D2A98E4F81AF360659775004A93C98DE24C97FF07802DCDCA92109DE5",
            single.Value.ToString());

        Assert.Equal(
            "F46DD3426AFA7F824EDDE0324E56CF547A2F8A9555A9DA2DB957950BED7CB564",
            multiple.Value.ToString());

        // The two vectors carry the same identities in the same order, so only the domain separator
        // keeps a source inventory from authenticating as a work-item inventory.
        Result<CovenantDigest> sameIdentitiesAsSources =
            FullInstallationResetManagedFileDigests.SourceWriteIntentVector(
                [FirstByNetworkOrder, SecondByNetworkOrder]);

        Assert.NotEqual(sameIdentitiesAsSources.Value, multiple.Value);

    }

    [Fact]

    public void Both_vectors_reject_a_default_empty_duplicate_reordered_or_oversized_identity_list()
    {

        Assert.True(
            FullInstallationResetManagedFileDigests
                .SourceWriteIntentVector(default)
                .IsFailure);

        Assert.True(
            FullInstallationResetManagedFileDigests
                .LocalErasureWorkItemVector(default)
                .IsFailure);

        Assert.True(
            FullInstallationResetManagedFileDigests
                .SourceWriteIntentVector([Guid.Empty])
                .IsFailure);

        Assert.True(
            FullInstallationResetManagedFileDigests
                .LocalErasureWorkItemVector([Guid.Empty])
                .IsFailure);

        Assert.True(
            FullInstallationResetManagedFileDigests
                .SourceWriteIntentVector([FirstByNetworkOrder, FirstByNetworkOrder])
                .IsFailure);

        Assert.True(
            FullInstallationResetManagedFileDigests
                .LocalErasureWorkItemVector([FirstByNetworkOrder, FirstByNetworkOrder])
                .IsFailure);

        // Descending by RFC-4122 network order. A vector that accepted this would let two runs over
        // the same inventory authenticate against different digests.
        Assert.True(
            FullInstallationResetManagedFileDigests
                .SourceWriteIntentVector([SecondByNetworkOrder, FirstByNetworkOrder])
                .IsFailure);

        Assert.True(
            FullInstallationResetManagedFileDigests
                .LocalErasureWorkItemVector([SecondByNetworkOrder, FirstByNetworkOrder])
                .IsFailure);

        ImmutableArray<Guid> oversized =
            [.. Enumerable
                .Range(1, FullInstallationResetManagedFileBounds.MaximumVectorCount + 1)
                .Select(static index => new Guid(index, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))];

        Assert.True(
            FullInstallationResetManagedFileDigests
                .SourceWriteIntentVector(oversized)
                .IsFailure);

        Assert.True(
            FullInstallationResetManagedFileDigests
                .LocalErasureWorkItemVector(oversized)
                .IsFailure);

        ImmutableArray<Guid> atTheCap = oversized[..^1];

        Assert.True(
            FullInstallationResetManagedFileDigests
                .SourceWriteIntentVector(atTheCap)
                .IsSuccess);

    }

    [Fact]

    public void Blocker_evidence_is_content_free_and_separated_by_arm()
    {

        Result<CovenantDigest> writeOrphan =
            FullInstallationResetManagedFileDigests.BlockerEvidence(
                SecondByNetworkOrder,
                FullInstallationResetManagedFileBlockerArm.ManualWriteOrphan,
                CovenantErasureBlocker.ManualOwnershipMismatch);

        Result<CovenantDigest> workItemOrphan =
            FullInstallationResetManagedFileDigests.BlockerEvidence(
                SecondWorkItem,
                FullInstallationResetManagedFileBlockerArm.ManualWorkItemOrphan,
                CovenantErasureBlocker.ManualOwnershipMismatch);

        Assert.True(writeOrphan.IsSuccess, writeOrphan.Error.Message);

        Assert.True(workItemOrphan.IsSuccess, workItemOrphan.Error.Message);

        Assert.Equal(
            "05D2708B7105D12E6F0DC6C900D0A97F705BCA5F693FD3C5F081EA12C526F73C",
            writeOrphan.Value.ToString());

        Assert.Equal(
            "1CC478D7E6B804A17026A5D5813C57E6B5640589DE183DEA51B355892B4ACDA4",
            workItemOrphan.Value.ToString());

        // The same identity under the other arm is a different commitment, so a refused write cannot
        // be replayed as a refused work item.
        Result<CovenantDigest> sameIdentityOtherArm =
            FullInstallationResetManagedFileDigests.BlockerEvidence(
                SecondByNetworkOrder,
                FullInstallationResetManagedFileBlockerArm.ManualWorkItemOrphan,
                CovenantErasureBlocker.ManualOwnershipMismatch);

        Assert.NotEqual(writeOrphan.Value, sameIdentityOtherArm.Value);

        Assert.True(
            FullInstallationResetManagedFileDigests
                .BlockerEvidence(
                    Guid.Empty,
                    FullInstallationResetManagedFileBlockerArm.ManualWriteOrphan,
                    CovenantErasureBlocker.ManualOwnershipMismatch)
                .IsFailure);

    }

    [Fact]

    public void Terminal_classification_freezes_its_layout_for_the_empty_safe_and_manual_shapes()
    {

        Result<byte[]> emptyPreimage =
            FullInstallationResetManagedFileDigests.TerminalClassificationPreimage([], []);

        Assert.True(emptyPreimage.IsSuccess, emptyPreimage.Error.Message);

        Assert.Equal(
            Convert.FromHexString(
                "417263616e756d2e46756c6c496e7374616c6c6174696f6e52657365742e"
                + "4d616e6167656446696c655465726d696e616c436c617373696669636174"
                + "696f6e2e76310000000000000000000000000000000000"),
            emptyPreimage.Value);

        Result<CovenantDigest> empty =
            FullInstallationResetManagedFileDigests.TerminalClassification([], []);

        Assert.True(empty.IsSuccess, empty.Error.Message);

        Assert.Equal(
            "1E1051AB0B4B305F68865D1F4ADDA2F885AB3B54746F89708E53260E2242B69F",
            empty.Value.ToString());

        Result<CovenantDigest> safe =
            FullInstallationResetManagedFileDigests.TerminalClassification(
                [
                    Source(FirstByNetworkOrder, ManagedFileWriteIntentPhase.Erased),
                    Source(SecondByNetworkOrder, ManagedFileWriteIntentPhase.Cleaned),
                ],
                [
                    Completed(
                        FirstWorkItem,
                        LocalErasureDeletionEvidenceCode.SameHandleDeletedAndParentFsynced),
                ]);

        Assert.True(safe.IsSuccess, safe.Error.Message);

        Assert.Equal(
            "7355B057248519AFBBC232C7CEDB64CF3B902AF31F39588366D8FE48E45E4AC8",
            safe.Value.ToString());

        Result<CovenantDigest> sourceBlocker =
            FullInstallationResetManagedFileDigests.BlockerEvidence(
                SecondByNetworkOrder,
                FullInstallationResetManagedFileBlockerArm.ManualWriteOrphan,
                CovenantErasureBlocker.ManualOwnershipMismatch);

        Result<CovenantDigest> workItemBlocker =
            FullInstallationResetManagedFileDigests.BlockerEvidence(
                SecondWorkItem,
                FullInstallationResetManagedFileBlockerArm.ManualWorkItemOrphan,
                CovenantErasureBlocker.ManualOwnershipMismatch);

        Result<CovenantDigest> manual =
            FullInstallationResetManagedFileDigests.TerminalClassification(
                [
                    Source(FirstByNetworkOrder, ManagedFileWriteIntentPhase.Erased),
                    new FullInstallationResetManagedSourceClassificationV1(
                        SecondByNetworkOrder,
                        ManagedFileWriteIntentPhase.ManualNonrevocable,
                        sourceBlocker.Value),
                ],
                [
                    Completed(
                        FirstWorkItem,
                        LocalErasureDeletionEvidenceCode.SameHandleDeletedAndParentFsynced),
                    new FullInstallationResetManagedWorkItemClassificationV1(
                        SecondWorkItem,
                        LocalErasureWorkItemState.ManualBlocker,
                        DeletionEvidence: null,
                        workItemBlocker.Value),
                ]);

        Assert.True(manual.IsSuccess, manual.Error.Message);

        Assert.Equal(
            "BEE9ED9CAFC80A06BC7E45C173A8F25DEA686457F6650604B658CF0A8FC951CA",
            manual.Value.ToString());

    }

    [Fact]

    public void Terminal_classification_refuses_a_nonterminal_or_incoherent_entry()
    {

        // AdoptedAndLabeled is not a terminal outcome. Accepting it would let a source that still
        // owns a live file be counted as reconciled.
        Assert.True(
            FullInstallationResetManagedFileDigests
                .TerminalClassification(
                    [Source(FirstByNetworkOrder, ManagedFileWriteIntentPhase.AdoptedAndLabeled)],
                    [])
                .IsFailure);

        Assert.True(
            FullInstallationResetManagedFileDigests
                .TerminalClassification(
                    [],
                    [
                        new FullInstallationResetManagedWorkItemClassificationV1(
                            FirstWorkItem,
                            LocalErasureWorkItemState.Prepared,
                            DeletionEvidence: null,
                            BlockerEvidenceDigest: null),
                    ])
                .IsFailure);

        // A safely terminal source may not carry blocker evidence, and a manual one may not omit it.
        Assert.True(
            FullInstallationResetManagedFileDigests
                .TerminalClassification(
                    [
                        new FullInstallationResetManagedSourceClassificationV1(
                            FirstByNetworkOrder,
                            ManagedFileWriteIntentPhase.Erased,
                            new CovenantDigest(new byte[CovenantLimits.DigestBytes])),
                    ],
                    [])
                .IsFailure);

        Assert.True(
            FullInstallationResetManagedFileDigests
                .TerminalClassification(
                    [
                        new FullInstallationResetManagedSourceClassificationV1(
                            FirstByNetworkOrder,
                            ManagedFileWriteIntentPhase.ManualNonrevocable,
                            BlockerEvidenceDigest: null),
                    ],
                    [])
                .IsFailure);

        // A completed work item must record which of the two absence proofs it has, and a refused one
        // must record none.
        Assert.True(
            FullInstallationResetManagedFileDigests
                .TerminalClassification(
                    [],
                    [
                        new FullInstallationResetManagedWorkItemClassificationV1(
                            FirstWorkItem,
                            LocalErasureWorkItemState.Completed,
                            DeletionEvidence: null,
                            BlockerEvidenceDigest: null),
                    ])
                .IsFailure);

        Assert.True(
            FullInstallationResetManagedFileDigests
                .TerminalClassification(
                    [],
                    [
                        new FullInstallationResetManagedWorkItemClassificationV1(
                            FirstWorkItem,
                            LocalErasureWorkItemState.ManualBlocker,
                            LocalErasureDeletionEvidenceCode.AlreadyAbsent,
                            new CovenantDigest(new byte[CovenantLimits.DigestBytes])),
                    ])
                .IsFailure);

        // Both vectors keep the strict ascending RFC-4122 order the inventory was authenticated in.
        Assert.True(
            FullInstallationResetManagedFileDigests
                .TerminalClassification(
                    [
                        Source(SecondByNetworkOrder, ManagedFileWriteIntentPhase.Erased),
                        Source(FirstByNetworkOrder, ManagedFileWriteIntentPhase.Erased),
                    ],
                    [])
                .IsFailure);

    }

    private static FullInstallationResetManagedSourceClassificationV1 Source(
        Guid sourceWriteOperationId,
        ManagedFileWriteIntentPhase terminalPhase) =>
        new(sourceWriteOperationId, terminalPhase, BlockerEvidenceDigest: null);

    private static FullInstallationResetManagedWorkItemClassificationV1 Completed(
        Guid workItemId,
        LocalErasureDeletionEvidenceCode deletionEvidence) =>
        new(
            workItemId,
            LocalErasureWorkItemState.Completed,
            deletionEvidence,
            BlockerEvidenceDigest: null);

}
