using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Reflection;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

public sealed class FullInstallationResetCleanupAuthorityTests
{

    [Fact]
    public void Runtime_contract_comparers_reject_same_instance_forged_invalid_objects_without_reference_identity()
    {

        CampaignPathFullInstallationResetInventory inventory =
            (CampaignPathFullInstallationResetInventory)RuntimeHelpers.GetUninitializedObject(
                typeof(CampaignPathFullInstallationResetInventory));

        CampaignPathFullInstallationResetCleanupPreparation preparation =
            (CampaignPathFullInstallationResetCleanupPreparation)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(CampaignPathFullInstallationResetCleanupPreparation));

        CampaignPathFullInstallationResetCleanupReceipt receipt =
            (CampaignPathFullInstallationResetCleanupReceipt)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(CampaignPathFullInstallationResetCleanupReceipt));

        Assert.False(CampaignPathFullInstallationResetContractComparer.InventoryEquals(
            inventory,
            inventory));

        Assert.False(CampaignPathFullInstallationResetContractComparer.PreparationEquals(
            preparation,
            preparation));

        Assert.False(CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
            receipt,
            receipt));

        Assert.True(CampaignPathFullInstallationResetContractComparer.InventoryEquals(
            null,
            null));

        Assert.True(CampaignPathFullInstallationResetContractComparer.PreparationEquals(
            null,
            null));

        Assert.True(CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
            null,
            null));

    }

    [Fact]
    public void Runtime_contract_comparers_reject_every_field_and_position_substitution()
    {

        Guid owner = Guid.Parse("60000000-0000-4000-8000-000000000001");

        CampaignMarkerInventoryEntryV1 first = Entry(
            Guid.Parse("60000000-0000-4000-8000-000000000010"),
            0x61);

        CampaignMarkerInventoryEntryV1 second = Entry(
            Guid.Parse("60000000-0000-4000-8000-000000000020"),
            0x71);

        CampaignPathFullInstallationResetInventory inventory = Inventory(
            owner,
            [first, second]);

        Assert.False(CampaignPathFullInstallationResetContractComparer.InventoryEquals(
            inventory,
            Inventory(Guid.NewGuid(), [first, second])));

        CampaignMarkerInventoryEntryV1[] substitutedEntries =
        [
            first with
            {
                CampaignId = Guid.Parse("60000000-0000-4000-8000-000000000015"),
            },
            first with { PriorPathRevision = 2 },
            first with { MarkerDigest = Digest(0x81) },
            first with { IndexedPhysicalIdentityDigest = Digest(0x82) },
            first with { CanonicalDisplayPathDigest = Digest(0x83) },
            first with { SameHandleOwnershipEvidenceDigest = Digest(0x84) },
        ];

        foreach (CampaignMarkerInventoryEntryV1 substituted in substitutedEntries)
        {

            Assert.False(CampaignPathFullInstallationResetContractComparer.InventoryEquals(
                inventory,
                Inventory(owner, [substituted, second])));

        }

        Assert.False(CampaignPathFullInstallationResetContractComparer.InventoryEquals(
            inventory,
            ForgeInventory(
                owner,
                [second, first],
                inventory.InventoryDigest)));

        CampaignPathFullInstallationResetCleanupPreparation preparation = Value(
            CampaignPathFullInstallationResetCleanupPreparation.Create(
                owner,
                Digest(0x91),
                inventory));

        Guid otherOwner = Guid.NewGuid();

        Assert.False(CampaignPathFullInstallationResetContractComparer.PreparationEquals(
            preparation,
            Value(CampaignPathFullInstallationResetCleanupPreparation.Create(
                otherOwner,
                Digest(0x91),
                Inventory(otherOwner)))));

        Assert.False(CampaignPathFullInstallationResetContractComparer.PreparationEquals(
            preparation,
            Value(CampaignPathFullInstallationResetCleanupPreparation.Create(
                owner,
                Digest(0x92),
                inventory))));

        Assert.False(CampaignPathFullInstallationResetContractComparer.PreparationEquals(
            preparation,
            Value(CampaignPathFullInstallationResetCleanupPreparation.Create(
                owner,
                Digest(0x91),
                Inventory(owner)))));

        ImmutableArray<Guid> ids =
        [
            Guid.Parse("60000000-0000-4000-8000-000000000030"),
            Guid.Parse("60000000-0000-4000-8000-000000000040"),
        ];

        CampaignPathFullInstallationResetCleanupReceipt receipt = PreparedReceipt(
            owner,
            Digest(0xA1),
            ids);

        Assert.False(CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
            receipt,
            PreparedReceipt(Guid.NewGuid(), Digest(0xA1), ids)));

        Assert.False(CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
            receipt,
            PreparedReceipt(owner, Digest(0xA2), ids)));

        Assert.False(CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
            receipt,
            PreparedReceipt(owner, Digest(0xA1), [ids[1], ids[0]])));

        CampaignPathFullInstallationResetCleanupReceipt terminal = Value(
            CampaignPathFullInstallationResetCleanupReceipt.CreateTerminal(
                owner,
                Digest(0xA1),
                ids,
                receipt.MarkerIntentVectorDigest,
                deletedCount: 1,
                orphanCount: 1));

        Assert.False(CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
            receipt,
            terminal));

        CampaignPathFullInstallationResetCleanupReceipt otherCounts = Value(
            CampaignPathFullInstallationResetCleanupReceipt.CreateTerminal(
                owner,
                Digest(0xA1),
                ids,
                receipt.MarkerIntentVectorDigest,
                deletedCount: 2,
                orphanCount: 0));

        Assert.False(CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
            terminal,
            otherCounts));

        Assert.False(CampaignPathFullInstallationResetContractComparer.InventoryEquals(
            inventory,
            null));

        Assert.False(CampaignPathFullInstallationResetContractComparer.PreparationEquals(
            preparation,
            null));

        Assert.False(CampaignPathFullInstallationResetContractComparer.ReceiptEquals(
            receipt,
            null));

    }

    [Fact]
    public void Inventory_factory_rejects_noncanonical_inputs_and_detaches_every_accepted_value()
    {

        Guid owner = Guid.Parse("10000000-0000-4000-8000-000000000001");

        CampaignMarkerInventoryEntryV1 first = Entry(
            Guid.Parse("10000000-0000-4000-8000-000000000002"),
            0x10);

        CampaignMarkerInventoryEntryV1 second = Entry(
            Guid.Parse("20000000-0000-4000-8000-000000000002"),
            0x20);

        ImmutableArray<CampaignMarkerInventoryEntryV1> valid = [first, second];

        CovenantDigest validDigest = Value(
            FullInstallationResetMarkerPairResetDigests.CampaignInventory(valid));

        Assert.True(CampaignPathFullInstallationResetInventory.Create(
            Guid.Empty,
            valid,
            validDigest).IsFailure);

        Assert.True(CampaignPathFullInstallationResetInventory.Create(
            owner,
            default,
            validDigest).IsFailure);

        Assert.True(CampaignPathFullInstallationResetInventory.Create(
            owner,
            valid,
            default).IsFailure);

        Assert.True(CampaignPathFullInstallationResetInventory.Create(
            owner,
            valid,
            Digest(0xEE)).IsFailure);

        CampaignMarkerInventoryEntryV1[] invalidEntries =
        [
            null!,
            first with { CampaignId = Guid.Empty },
            first with { PriorPathRevision = 0 },
            first with { MarkerDigest = default },
            first with { IndexedPhysicalIdentityDigest = default },
            first with { CanonicalDisplayPathDigest = default },
            first with { SameHandleOwnershipEvidenceDigest = default },
        ];

        foreach (CampaignMarkerInventoryEntryV1 invalidEntry in invalidEntries)
        {

            Assert.True(CampaignPathFullInstallationResetInventory.Create(
                owner,
                [invalidEntry],
                validDigest).IsFailure);

        }

        Assert.True(CampaignPathFullInstallationResetInventory.Create(
            owner,
            [second, first],
            validDigest).IsFailure);

        Assert.True(CampaignPathFullInstallationResetInventory.Create(
            owner,
            [first, first],
            validDigest).IsFailure);

        ImmutableArray<CampaignMarkerInventoryEntryV1> oversized =
            Enumerable.Repeat(first, 4_097).ToImmutableArray();

        Assert.True(CampaignPathFullInstallationResetInventory.Create(
            owner,
            oversized,
            validDigest).IsFailure);

        ImmutableArray<CampaignMarkerInventoryEntryV1> empty = [];

        CampaignPathFullInstallationResetInventory positiveEmpty = Value(
            CampaignPathFullInstallationResetInventory.Create(
                owner,
                empty,
                Value(FullInstallationResetMarkerPairResetDigests.CampaignInventory(
                    empty))));

        Assert.Empty(positiveEmpty.Entries);

        CampaignMarkerInventoryEntryV1[] aliasedArray = [first, second];

        ImmutableArray<CampaignMarkerInventoryEntryV1> aliased =
            ImmutableCollectionsMarshal.AsImmutableArray(aliasedArray);

        CampaignPathFullInstallationResetInventory detached = Value(
            CampaignPathFullInstallationResetInventory.Create(
                owner,
                aliased,
                validDigest));

        aliasedArray[0] = second;

        Assert.Equal(first.CampaignId, detached.Entries[0].CampaignId);

        Assert.NotSame(first, detached.Entries[0]);

        Assert.NotSame(
            DigestBacking(first.MarkerDigest),
            DigestBacking(detached.Entries[0].MarkerDigest));

        Assert.NotSame(
            DigestBacking(first.IndexedPhysicalIdentityDigest),
            DigestBacking(detached.Entries[0].IndexedPhysicalIdentityDigest));

        Assert.NotSame(
            DigestBacking(first.CanonicalDisplayPathDigest),
            DigestBacking(detached.Entries[0].CanonicalDisplayPathDigest));

        Assert.NotSame(
            DigestBacking(first.SameHandleOwnershipEvidenceDigest),
            DigestBacking(detached.Entries[0].SameHandleOwnershipEvidenceDigest));

        Assert.NotSame(
            DigestBacking(validDigest),
            DigestBacking(detached.InventoryDigest));

        Assert.True(CampaignPathFullInstallationResetContractComparer.InventoryEquals(
            detached,
            Value(CampaignPathFullInstallationResetInventory.Create(
                owner,
                valid,
                validDigest))));

    }

    [Fact]
    public void Preparation_factory_rejects_substitution_and_recopies_the_inventory()
    {

        Guid owner = Guid.Parse("30000000-0000-4000-8000-000000000001");

        CampaignPathFullInstallationResetInventory inventory = Inventory(owner);

        Assert.True(CampaignPathFullInstallationResetCleanupPreparation.Create(
            Guid.Empty,
            Digest(0x31),
            inventory).IsFailure);

        Assert.True(CampaignPathFullInstallationResetCleanupPreparation.Create(
            owner,
            default,
            inventory).IsFailure);

        Assert.True(CampaignPathFullInstallationResetCleanupPreparation.Create(
            owner,
            Digest(0x31),
            null!).IsFailure);

        Assert.True(CampaignPathFullInstallationResetCleanupPreparation.Create(
            Guid.Parse("30000000-0000-4000-8000-000000000002"),
            Digest(0x31),
            inventory).IsFailure);

        CampaignPathFullInstallationResetCleanupPreparation accepted = Value(
            CampaignPathFullInstallationResetCleanupPreparation.Create(
                owner,
                Digest(0x31),
                inventory));

        Assert.NotSame(inventory, accepted.Inventory);

        Assert.True(CampaignPathFullInstallationResetContractComparer.InventoryEquals(
            inventory,
            accepted.Inventory));

    }

    [Fact]
    public void Receipt_factory_enforces_vector_order_detachment_derived_counts_and_checked_terminal_sum()
    {

        Guid owner = Guid.Parse("40000000-0000-4000-8000-000000000001");

        Guid first = Guid.Parse("40000000-0000-4000-8000-000000000010");

        Guid second = Guid.Parse("40000000-0000-4000-8000-000000000020");

        Guid[] aliasedArray = [second, first];

        ImmutableArray<Guid> ordered =
            ImmutableCollectionsMarshal.AsImmutableArray(aliasedArray);

        CovenantDigest vectorDigest = Value(
            FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(ordered));

        Assert.True(CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
            Guid.Empty,
            Digest(0x41),
            ordered,
            vectorDigest).IsFailure);

        Assert.True(CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
            owner,
            default,
            ordered,
            vectorDigest).IsFailure);

        Assert.True(CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
            owner,
            Digest(0x41),
            default,
            vectorDigest).IsFailure);

        Assert.True(CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
            owner,
            Digest(0x41),
            [Guid.Empty],
            vectorDigest).IsFailure);

        Assert.True(CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
            owner,
            Digest(0x41),
            [first, first],
            vectorDigest).IsFailure);

        Assert.True(CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
            owner,
            Digest(0x41),
            ordered,
            Digest(0xFF)).IsFailure);

        Assert.True(CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
            owner,
            Digest(0x41),
            ordered,
            default).IsFailure);

        Assert.True(CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
            owner,
            Digest(0x41),
            Enumerable.Repeat(first, 4_097).ToImmutableArray(),
            vectorDigest).IsFailure);

        CampaignPathFullInstallationResetCleanupReceipt prepared = Value(
            CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
                owner,
                Digest(0x41),
                ordered,
                vectorDigest));

        aliasedArray[0] = Guid.NewGuid();

        Assert.Equal(second, prepared.OrderedMarkerIntentIds[0]);

        Assert.Equal((ulong)2, prepared.MarkerIntentCount);

        Assert.Equal((ulong)0, prepared.DeletedCount);

        Assert.Equal((ulong)0, prepared.OrphanCount);

        Assert.True(CampaignPathFullInstallationResetCleanupReceipt.CreateTerminal(
            owner,
            Digest(0x41),
            prepared.OrderedMarkerIntentIds,
            prepared.MarkerIntentVectorDigest,
            1,
            0).IsFailure);

        Assert.True(CampaignPathFullInstallationResetCleanupReceipt.CreateTerminal(
            owner,
            Digest(0x41),
            prepared.OrderedMarkerIntentIds,
            prepared.MarkerIntentVectorDigest,
            ulong.MaxValue,
            1).IsFailure);

        CampaignPathFullInstallationResetCleanupReceipt terminal = Value(
            CampaignPathFullInstallationResetCleanupReceipt.CreateTerminal(
                owner,
                Digest(0x41),
                prepared.OrderedMarkerIntentIds,
                prepared.MarkerIntentVectorDigest,
                1,
                1));

        Assert.Equal((ulong)2, terminal.MarkerIntentCount);

        ImmutableArray<Guid> empty = [];

        CampaignPathFullInstallationResetCleanupReceipt emptyPrepared = Value(
            CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
                owner,
                Digest(0x41),
                empty,
                Value(FullInstallationResetMarkerPairResetDigests
                    .FullResetIntentVector(empty))));

        Assert.Equal(
            "26B63BE668FE309ADD01922EA6DD3FEFE222C7833FF9DFA379BDA0275CF98574",
            Convert.ToHexString(emptyPrepared.MarkerIntentVectorDigest.Bytes));

    }

    [Fact]
    public async Task Pair_absence_verified_mints_one_operation_revision_effect_inventory_and_lock_bound_proof()
    {

        MethodInfo? mint = typeof(HostToolsMarkerPairResetCoordinator).GetMethod(
            "MintCleanupAuthorityAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(mint);

        Assert.True(mint.IsPrivate);

        Assert.Equal(
            typeof(Task<Result<
                HostToolsMarkerPairResetCoordinator
                    .FullInstallationResetMarkerCleanupAuthority>>),
            mint.ReturnType);

        Type proof = Assert.Single(
            typeof(HostToolsMarkerPairResetCoordinator).GetNestedTypes(
                BindingFlags.NonPublic),
            type => type.Name == "AuthenticatedFullInstallationResetJournalProof");

        Assert.True(proof.IsSealed);

        Assert.All(
            proof.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic),
            constructor => Assert.True(constructor.IsPrivate));

        using AuthorityHarness harness = new();

        HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority
            authority = Value(await harness.MintAsync());

        FieldInfo authorityProof = Assert.Single(
            authority.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name == "_proof");

        object? boundProofValue = authorityProof.GetValue(authority);

        Assert.NotNull(boundProofValue);

        object boundProof = boundProofValue;

        PropertyInfo? heldLockProperty = boundProof.GetType().GetProperty(
            "HeldInstallationLock",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(heldLockProperty);

        Assert.Same(
            harness.HeldLock,
            heldLockProperty.GetValue(boundProof));

        PropertyInfo? publicationProperty = boundProof.GetType().GetProperty(
            "Publication",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(publicationProperty);

        Assert.Same(
            harness.Current,
            publicationProperty.GetValue(boundProof));

        Assert.Equal(1, harness.Store.RecoverCalls);

    }

    [Fact]
    public void Cleanup_authority_cannot_be_constructed_from_a_digest_path_lock_or_public_attestation()
    {

        Type authority = typeof(HostToolsMarkerPairResetCoordinator
            .FullInstallationResetMarkerCleanupAuthority);

        Assert.True(authority.IsSealed);

        Assert.Empty(authority.GetConstructors(BindingFlags.Instance | BindingFlags.Public));

        ConstructorInfo constructor = Assert.Single(
            authority.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));

        Assert.True(constructor.IsPrivate);

        MethodInfo bridge = Assert.Single(
            authority.GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            method => method.Name == "Create");

        using AuthorityHarness harness = new();

        object[] nonauthorityInputs =
        [
            Digest(0xB1),
            harness.GuardedRoot,
            harness.HeldLock,
            Attestation(harness.Current.Payload.OperationId),
        ];

        foreach (object input in nonauthorityInputs)
        {

            TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(() =>
                bridge.Invoke(null, [input, harness.Subject, input]));

            Assert.IsType<InvalidOperationException>(thrown.InnerException);

        }

    }

    [Fact]
    public async Task Cleanup_authority_revalidates_envelope_anchor_phase_and_exact_lock_before_every_use()
    {

        using AuthorityHarness harness = new();

        HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority
            authority = Value(await harness.MintAsync());

        CampaignPathFullInstallationResetCleanupPreparation preparation =
            harness.Preparation();

        Result first = await authority.RevalidatePreparationAsync(
            preparation,
            expectedReceipt: null,
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Message);

        Assert.Equal(2, harness.Store.RecoverCalls);

        InstallationResetActivePublication original = harness.Current;

        harness.Store.Current = original with
        {
            Envelope = original.Envelope with
            {
                Revision = original.Envelope.Revision + 1,
            },
        };

        Assert.True((await authority.RevalidatePreparationAsync(
            preparation,
            null,
            CancellationToken.None)).IsFailure);

        harness.Store.Current = original with
        {
            Anchor = original.Anchor with
            {
                Revision = original.Anchor.Revision + 1,
            },
        };

        Assert.True((await authority.RevalidatePreparationAsync(
            preparation,
            null,
            CancellationToken.None)).IsFailure);

        harness.Store.Current = ChangePhase(
            original,
            HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted);

        Assert.True((await authority.RevalidatePreparationAsync(
            preparation,
            null,
            CancellationToken.None)).IsFailure);

        using AuthorityHarness wrongPhase = new(
            HostToolsMarkerPairResetPhase.OsMarkerCompareDeleted);

        Assert.True((await wrongPhase.MintAsync()).IsFailure);

    }

    [Fact]
    public async Task Preparation_authority_rejects_owner_effect_or_inventory_input_substitution()
    {

        using AuthorityHarness harness = new();

        HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority
            authority = Value(await harness.MintAsync());

        Guid owner = harness.Current.Payload.OperationId;

        Guid otherOwner = Guid.NewGuid();

        CampaignPathFullInstallationResetCleanupPreparation[] substitutions =
        [
            Value(CampaignPathFullInstallationResetCleanupPreparation.Create(
                otherOwner,
                harness.Checkpoint.OwnerEffectDigest,
                Inventory(otherOwner))),
            Value(CampaignPathFullInstallationResetCleanupPreparation.Create(
                owner,
                Digest(0xB2),
                harness.Inventory)),
            Value(CampaignPathFullInstallationResetCleanupPreparation.Create(
                owner,
                harness.Checkpoint.OwnerEffectDigest,
                Inventory(owner, [Entry(
                    Guid.Parse("70000000-0000-4000-8000-000000000099"),
                    0x72)]))),
        ];

        foreach (CampaignPathFullInstallationResetCleanupPreparation substitution
            in substitutions)
        {

            Assert.True((await authority.RevalidatePreparationAsync(
                substitution,
                null,
                CancellationToken.None)).IsFailure);

        }

        Assert.Equal(1 + substitutions.Length, harness.Store.RecoverCalls);

    }

    [Fact]
    public async Task Reconciliation_authority_rejects_owner_effect_intent_vector_or_count_input_substitution()
    {

        using AuthorityHarness harness = new(withPreparedReceipt: true);

        HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority
            authority = Value(await harness.MintAsync());

        CampaignPathFullInstallationResetCleanupReceipt receipt =
            Assert.IsType<CampaignPathFullInstallationResetCleanupReceipt>(
                harness.Receipt);

        Guid owner = harness.Current.Payload.OperationId;

        ImmutableArray<Guid> ids = receipt.OrderedMarkerIntentIds;

        CampaignPathFullInstallationResetCleanupReceipt[] substitutions =
        [
            PreparedReceipt(Guid.NewGuid(), receipt.OwnerEffectDigest, ids),
            PreparedReceipt(owner, Digest(0xB3), ids),
            PreparedReceipt(owner, receipt.OwnerEffectDigest, [ids[1], ids[0]]),
            Value(CampaignPathFullInstallationResetCleanupReceipt.CreateTerminal(
                owner,
                receipt.OwnerEffectDigest,
                ids,
                receipt.MarkerIntentVectorDigest,
                deletedCount: 1,
                orphanCount: 1)),
        ];

        foreach (CampaignPathFullInstallationResetCleanupReceipt substitution
            in substitutions)
        {

            Assert.True((await authority.RevalidateReceiptAsync(
                substitution,
                CancellationToken.None)).IsFailure);

        }

        Assert.Equal(1 + substitutions.Length, harness.Store.RecoverCalls);

    }

    [Fact]
    public async Task Cleanup_authority_rejects_released_wrong_or_stale_lock_and_changed_revision()
    {

        using AuthorityHarness harness = new();

        HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority
            authority = Value(await harness.MintAsync());

        string otherRoot = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-cleanup-authority-wrong-{Guid.NewGuid():N}");

        Directory.CreateDirectory(otherRoot);

        try
        {

            using ArcanumMaintenanceLock wrongLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(otherRoot));

            await Assert.ThrowsAnyAsync<Exception>(() => harness.MintAsync(
                wrongLock,
                harness.Current));

        }
        finally
        {

            Directory.Delete(otherRoot, recursive: true);

        }

        harness.Store.Current = NextPublication(harness.Current);

        Assert.True((await authority.RevalidatePreparationAsync(
            harness.Preparation(),
            null,
            CancellationToken.None)).IsFailure);

        harness.HeldLock.Dispose();

        Assert.True((await authority.RevalidatePreparationAsync(
            harness.Preparation(),
            null,
            CancellationToken.None)).IsFailure);

        using ArcanumMaintenanceLock replacement = Assert.IsType<ArcanumMaintenanceLock>(
            ArcanumMaintenanceLock.TryAcquire(harness.GuardedRoot));

        Assert.True((await authority.RevalidatePreparationAsync(
            harness.Preparation(),
            null,
            CancellationToken.None)).IsFailure);

    }

    [Fact]
    public async Task Prepared_receipt_publication_invalidates_old_authority_and_fresh_revision_authority_succeeds()
    {

        using AuthorityHarness harness = new();

        HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority
            oldAuthority = Value(await harness.MintAsync());

        CampaignPathFullInstallationResetCleanupReceipt prepared = PreparedReceipt(
            harness.Current.Payload.OperationId,
            harness.Checkpoint.OwnerEffectDigest,
            AuthorityIntentIds());

        harness.Store.Current = NextPublication(harness.Current, prepared);

        Assert.True((await oldAuthority.RevalidatePreparationAsync(
            harness.Preparation(),
            null,
            CancellationToken.None)).IsFailure);

        HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority
            freshAuthority = Value(await harness.MintAsync());

        Result fresh = await freshAuthority.RevalidatePreparationAsync(
            harness.Preparation(),
            prepared,
            CancellationToken.None);

        Assert.True(fresh.IsSuccess, fresh.Error.Message);

    }

    [Fact]
    public async Task Terminal_receipt_publication_invalidates_the_last_reconciliation_authority()
    {

        using AuthorityHarness harness = new(withPreparedReceipt: true);

        HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority
            oldAuthority = Value(await harness.MintAsync());

        CampaignPathFullInstallationResetCleanupReceipt prepared =
            Assert.IsType<CampaignPathFullInstallationResetCleanupReceipt>(
                harness.Receipt);

        CampaignPathFullInstallationResetCleanupReceipt terminal = Value(
            CampaignPathFullInstallationResetCleanupReceipt.CreateTerminal(
                prepared.OwnerOperationId,
                prepared.OwnerEffectDigest,
                prepared.OrderedMarkerIntentIds,
                prepared.MarkerIntentVectorDigest,
                deletedCount: 1,
                orphanCount: 1));

        harness.Store.Current = NextPublication(harness.Current, terminal);

        Assert.True((await oldAuthority.RevalidateReceiptAsync(
            prepared,
            CancellationToken.None)).IsFailure);

        HostToolsMarkerPairResetCoordinator.FullInstallationResetMarkerCleanupAuthority
            terminalAuthority = Value(await harness.MintAsync());

        Result current = await terminalAuthority.RevalidateReceiptAsync(
            terminal,
            CancellationToken.None);

        Assert.True(current.IsSuccess, current.Error.Message);

    }

    [Fact]
    public void Cleanup_authority_is_nonserializable_and_absent_from_all_json_contexts()
    {

        string repositoryRoot = RepositoryRoot();

        string[] productionSources = Directory.GetFiles(
            Path.Combine(repositoryRoot, "src"),
            "*.cs",
            SearchOption.AllDirectories);

        string[] jsonContexts = productionSources
            .Where(file => File.ReadAllText(file).Contains(
                "[JsonSerializable",
                StringComparison.Ordinal))
            .ToArray();

        Assert.All(
            jsonContexts,
            file =>
            {

                string source = File.ReadAllText(file);

                Assert.DoesNotContain(
                    "FullInstallationResetMarkerCleanupAuthority",
                    source,
                    StringComparison.Ordinal);

                Assert.DoesNotContain(
                    "CampaignPathFullInstallationResetInventory",
                    source,
                    StringComparison.Ordinal);

                Assert.DoesNotContain(
                    "CampaignPathFullInstallationResetCleanupPreparation",
                    source,
                    StringComparison.Ordinal);

                Assert.DoesNotContain(
                    "CampaignPathFullInstallationResetCleanupReceipt",
                    source,
                    StringComparison.Ordinal);

            });

        Type authority = typeof(HostToolsMarkerPairResetCoordinator
            .FullInstallationResetMarkerCleanupAuthority);

        Assert.DoesNotContain(
            authority.GetCustomAttributes(inherit: false),
            attribute => attribute.GetType().Name.Contains(
                "Json",
                StringComparison.Ordinal)
                || attribute.GetType().Name.Contains(
                    "Serializable",
                    StringComparison.Ordinal));

        string coordinatorSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "InstallationReset",
            "HostToolsMarkerPairResetCoordinator.cs"));

        Assert.Equal(1, Count(
            coordinatorSource,
            "FullInstallationResetMarkerCleanupAuthority.Create("));

        Assert.DoesNotContain(
            "ReferenceEquals",
            File.ReadAllText(Path.Combine(
                repositoryRoot,
                "src",
                "RetroDownfall.Arcanum.Infrastructure",
                "Covenant",
                "CampaignPathFullInstallationResetContracts.cs")),
            StringComparison.Ordinal);

    }

    [Fact]
    public void Runtime_contract_and_authority_surfaces_are_internal_sealed_factory_only_and_bridge_guarded()
    {

        Type[] contracts =
        [
            typeof(CampaignPathFullInstallationResetInventory),
            typeof(CampaignPathFullInstallationResetCleanupPreparation),
            typeof(CampaignPathFullInstallationResetCleanupReceipt),
        ];

        foreach (Type contract in contracts)
        {

            Assert.True(contract.IsNotPublic);

            Assert.True(contract.IsSealed);

            ConstructorInfo constructor = Assert.Single(
                contract.GetConstructors(
                    BindingFlags.Instance | BindingFlags.NonPublic));

            Assert.True(constructor.IsPrivate);

            Assert.Empty(contract.GetConstructors(
                BindingFlags.Instance | BindingFlags.Public));

        }

        Assert.Equal(
            ["Create"],
            FactoryNames(typeof(CampaignPathFullInstallationResetInventory)));

        Assert.Equal(
            ["Create"],
            FactoryNames(typeof(CampaignPathFullInstallationResetCleanupPreparation)));

        Assert.Equal(
            ["CreatePrepared", "CreateTerminal"],
            FactoryNames(typeof(CampaignPathFullInstallationResetCleanupReceipt)));

        Type authority = typeof(HostToolsMarkerPairResetCoordinator
            .FullInstallationResetMarkerCleanupAuthority);

        Assert.Equal(
            ["RevalidatePreparationAsync", "RevalidateReceiptAsync"],
            authority.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(method => method.IsAssembly)
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());

        MethodInfo bridge = Assert.Single(
            authority.GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            method => method.Name == "Create");

        Assert.True(bridge.IsAssembly);

        Assert.Equal([typeof(object), typeof(HostToolsMarkerPairResetCoordinator), typeof(object)],
            bridge.GetParameters().Select(parameter => parameter.ParameterType).ToArray());

        string contractSource = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "Covenant",
            "CampaignPathFullInstallationResetContracts.cs"));

        Assert.DoesNotContain("ReferenceEquals", contractSource, StringComparison.Ordinal);

        Assert.DoesNotContain("ImmutableArray.Equals", contractSource, StringComparison.Ordinal);

        Assert.DoesNotContain("object.Equals", contractSource, StringComparison.Ordinal);

        string coordinatorSource = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure",
            "InstallationReset",
            "HostToolsMarkerPairResetCoordinator.cs"));

        Assert.Contains(
            "CampaignPathFullInstallationResetContractComparer.PreparationEquals(",
            coordinatorSource,
            StringComparison.Ordinal);

        Assert.Contains(
            "CampaignPathFullInstallationResetContractComparer.ReceiptEquals(",
            coordinatorSource,
            StringComparison.Ordinal);

    }

    private static CampaignPathFullInstallationResetInventory Inventory(Guid owner)
    {

        ImmutableArray<CampaignMarkerInventoryEntryV1> entries = [Entry(
            Guid.Parse("50000000-0000-4000-8000-000000000001"),
            0x51)];

        return Value(CampaignPathFullInstallationResetInventory.Create(
            owner,
            entries,
            Value(FullInstallationResetMarkerPairResetDigests.CampaignInventory(entries))));

    }

    private static CampaignPathFullInstallationResetInventory Inventory(
        Guid owner,
        ImmutableArray<CampaignMarkerInventoryEntryV1> entries) =>
        Value(CampaignPathFullInstallationResetInventory.Create(
            owner,
            entries,
            Value(FullInstallationResetMarkerPairResetDigests.CampaignInventory(entries))));

    private static CampaignPathFullInstallationResetInventory ForgeInventory(
        Guid owner,
        ImmutableArray<CampaignMarkerInventoryEntryV1> entries,
        CovenantDigest digest)
    {

        ConstructorInfo constructor = Assert.Single(
            typeof(CampaignPathFullInstallationResetInventory).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic));

        return Assert.IsType<CampaignPathFullInstallationResetInventory>(
            constructor.Invoke([owner, entries, digest]));

    }

    private static CampaignPathFullInstallationResetCleanupReceipt PreparedReceipt(
        Guid owner,
        CovenantDigest ownerEffect,
        ImmutableArray<Guid> ids) =>
        Value(CampaignPathFullInstallationResetCleanupReceipt.CreatePrepared(
            owner,
            ownerEffect,
            ids,
            Value(FullInstallationResetMarkerPairResetDigests.FullResetIntentVector(ids))));

    private static ImmutableArray<Guid> AuthorityIntentIds() =>
    [
        Guid.Parse("70000000-0000-4000-8000-000000000010"),
        Guid.Parse("70000000-0000-4000-8000-000000000020"),
    ];

    private static InstallationResetActivePublication CheckpointPublication(
        HostToolsMarkerPairResetPhase phase,
        CampaignPathFullInstallationResetCleanupReceipt? receipt = null)
    {

        Guid operation = Guid.NewGuid();

        Guid installation = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");

        DateTimeOffset acceptedAtUtc = new(
            2026,
            8,
            22,
            12,
            0,
            0,
            TimeSpan.Zero);

        FullInstallationResetExternalRemediationAttestation attestation =
            Attestation(operation);

        CovenantDigest signedDigest = Value(
            FullInstallationResetRemediationAttestationDigest.Calculate(attestation));

        FullInstallationResetRemediationClaimV1 claim = new(
            1,
            operation,
            installation,
            signedDigest,
            Digest(0x45),
            Digest(0x46),
            acceptedAtUtc);

        HostProcessToolsMatchedPair pair = new(
            TaintedDatabaseEvidence(),
            MatchedOsEvidence());

        CampaignPathFullInstallationResetInventory inventory = Inventory(operation);

        CovenantDigest pairDigest = Value(
            FullInstallationResetMarkerPairResetDigests.PairEvidence(pair));

        CovenantDigest ownerEffect = Value(
            FullInstallationResetMarkerPairResetDigests.FullResetEffect(
                operation,
                installation,
                pair.Database.TransitionId!.Value,
                pair.Database.TaintMasterKeyVersion!.Value,
                pair.Database.TaintFingerprint!.Value,
                pair.Database.DatabaseMarkerDigest,
                pair.OsMarker.MarkerBytesDigest,
                attestation.RemediationActionDigest,
                inventory.InventoryDigest));

        HostToolsMarkerPairResetCheckpointV1 checkpoint = new(
            1,
            phase,
            new FullInstallationResetRestartProofV1(
                1,
                FullInstallationResetSignedAttestationProjectionV1.FromAttestation(
                    attestation),
                acceptedAtUtc,
                signedDigest,
                pair.Database,
                pair.OsMarker,
                pairDigest),
            inventory.Entries,
            inventory.InventoryDigest,
            ownerEffect,
            receipt?.MarkerIntentCount,
            receipt?.OrderedMarkerIntentIds,
            receipt?.MarkerIntentVectorDigest,
            receipt?.DeletedCount,
            receipt?.OrphanCount);

        InstallationResetActiveRecord record = new(
            InstallationResetActiveStore.CurrentVersion,
            operation,
            "full-reset-plan",
            InstallationResetScope.All,
            new DataRetentionWorkspaceBinding(Guid.NewGuid(), "/workspace"),
            new InstallationResetAcceptedBinding("binding", [], [], [], [], []),
            InstallationResetPhase.Prepared,
            PointOfNoReturn: false,
            RowsDeleted: 0,
            FilesDeleted: 0,
            EstimatedBytesDeleted: 0,
            CredentialResults: [],
            LastErrorCode: ErrorCodes.Data.RecoveryRequired,
            FullInstallationResetRemediationClaim: claim,
            HostToolsMarkerPairReset: checkpoint);

        InstallationResetActivePayloadV3 payload =
            InstallationResetActivePayloadV3.FromRecord(record);

        InstallationResetActiveLocation location = new(
            "/active",
            Digest(0x10),
            Digest(0x11),
            "reset.active",
            Digest(0x12));

        InstallationResetActiveEnvelopeV2 envelope = new(
            2,
            location.ProfileNamespaceDigest,
            installation,
            operation,
            2,
            Digest(0x13),
            location.Digest,
            InstallationResetScope.All,
            record.PlanId,
            "nonce",
            "ciphertext",
            "tag");

        CovenantDigest envelopeDigest = Digest(0x14);

        InstallationResetActiveAnchorV1 anchor = new(
            1,
            InstallationResetActiveAnchorState.Active,
            location.ProfileNamespaceDigest,
            installation,
            operation,
            2,
            envelopeDigest,
            location.Digest);

        return new InstallationResetActivePublication(
            location,
            envelope,
            envelopeDigest,
            payload,
            anchor);

    }

    private static InstallationResetActivePublication NextPublication(
        InstallationResetActivePublication current,
        CampaignPathFullInstallationResetCleanupReceipt? receipt = null)
    {

        HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
            HostToolsMarkerPairResetCheckpointV1>(
                current.Payload.HostToolsMarkerPairReset);

        HostToolsMarkerPairResetCheckpointV1 nextCheckpoint = checkpoint with
        {
            MarkerIntentCount = receipt?.MarkerIntentCount
                ?? checkpoint.MarkerIntentCount,
            OrderedMarkerIntentIds = receipt?.OrderedMarkerIntentIds
                ?? checkpoint.OrderedMarkerIntentIds,
            MarkerIntentVectorDigest = receipt?.MarkerIntentVectorDigest
                ?? checkpoint.MarkerIntentVectorDigest,
            DeletedCount = receipt?.DeletedCount ?? checkpoint.DeletedCount,
            OrphanCount = receipt?.OrphanCount ?? checkpoint.OrphanCount,
        };

        InstallationResetActiveRecord nextRecord = current.Payload.ToRecord() with
        {
            HostToolsMarkerPairReset = nextCheckpoint,
        };

        CovenantDigest envelopeDigest = Digest(
            checked((byte)(0x20 + current.Envelope.Revision)));

        return new InstallationResetActivePublication(
            current.Location,
            current.Envelope with
            {
                Revision = current.Envelope.Revision + 1,
                PreviousEnvelopeDigest = current.EnvelopeDigest,
            },
            envelopeDigest,
            InstallationResetActivePayloadV3.FromRecord(nextRecord),
            current.Anchor with
            {
                Revision = current.Anchor.Revision + 1,
                EnvelopeDigest = envelopeDigest,
            });

    }

    private static InstallationResetActivePublication ChangePhase(
        InstallationResetActivePublication publication,
        HostToolsMarkerPairResetPhase phase)
    {

        HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
            HostToolsMarkerPairResetCheckpointV1>(
                publication.Payload.HostToolsMarkerPairReset);

        return publication with
        {
            Payload = InstallationResetActivePayloadV3.FromRecord(
                publication.Payload.ToRecord() with
                {
                    HostToolsMarkerPairReset = checkpoint with { Phase = phase },
                }),
        };

    }

    private static FullInstallationResetExternalRemediationAttestation Attestation(
        Guid operationId) =>
        new(
            1,
            operationId,
            Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee"),
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            7,
            Digest(0x5A),
            TaintedDatabaseEvidence().DatabaseMarkerDigest,
            Digest(0x23),
            new CovenantDigest(Convert.FromHexString(
                "761e8536128080d5936070524da90a6558b8901ea46d93194646b413bb27a1d9")),
            Base64Url.EncodeToString(Enumerable.Repeat((byte)0x33, 16).ToArray()),
            "RetroDownfall.Remediation.v1",
            new DateTimeOffset(2026, 8, 22, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 22, 13, 0, 0, TimeSpan.Zero),
            Base64Url.EncodeToString(Enumerable.Repeat((byte)0x44, 64).ToArray()));

    private static HostProcessToolsDatabaseMarkerEvidence TaintedDatabaseEvidence() =>
        new(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            RetroDownfall.Arcanum.Core.Security.CovenantHostToolsState.HostToolsTainted,
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            7,
            Digest(0x5A));

    private static HostProcessToolsOsMarkerEvidence MatchedOsEvidence() =>
        new(
            "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee",
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            7,
            Digest(0x5A),
            Digest(0x23),
            Digest(0x25));

    private static FullInstallationResetRemediationAuthorization Authorization(
        FullInstallationResetRemediationClaimV1 claim) =>
        new(
            claim.OperationId,
            claim.InstallationId,
            claim.AttestationDigest,
            claim.NonceDigest,
            claim.IssuerDigest,
            claim.AcceptedAtUtc);

    private static string RepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
            && !File.Exists(Path.Combine(
                directory.FullName,
                "RetroDownfall.Arcanum.slnx")))
        {

            directory = directory.Parent;

        }

        return Assert.IsType<DirectoryInfo>(directory).FullName;

    }

    private static int Count(string source, string value)
    {

        int count = 0;

        int offset = 0;

        while ((offset = source.IndexOf(
            value,
            offset,
            StringComparison.Ordinal)) >= 0)
        {

            count++;

            offset += value.Length;

        }

        return count;

    }

    private static string[] FactoryNames(Type contract) =>
        contract.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.IsAssembly)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static byte[] DigestBacking(CovenantDigest digest)
    {

        FieldInfo? backing = typeof(CovenantDigest).GetField(
            "_bytes",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(backing);

        return Assert.IsType<byte[]>(backing.GetValue(digest));

    }

    private sealed class AuthorityHarness : IDisposable
    {

        internal AuthorityHarness(
            HostToolsMarkerPairResetPhase phase =
                HostToolsMarkerPairResetPhase.PairAbsenceVerified,
            bool withPreparedReceipt = false)
        {

            GuardedRoot = Path.Combine(
                Path.GetTempPath(),
                $"arcanum-cleanup-authority-{Guid.NewGuid():N}");

            Directory.CreateDirectory(GuardedRoot);

            HeldLock = Assert.IsType<ArcanumMaintenanceLock>(
                ArcanumMaintenanceLock.TryAcquire(GuardedRoot));

            InstallationResetActivePublication current =
                CheckpointPublication(phase);

            HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
                HostToolsMarkerPairResetCheckpointV1>(
                    current.Payload.HostToolsMarkerPairReset);

            if (withPreparedReceipt)
            {

                Receipt = PreparedReceipt(
                    current.Payload.OperationId,
                    checkpoint.OwnerEffectDigest,
                    AuthorityIntentIds());

                current = ReplaceReceipt(current, Receipt);

            }

            Store = new RecordingActiveStore(GuardedRoot, current);

            Subject = new HostToolsMarkerPairResetCoordinator(
                Store,
                new InertDatabase(),
                new InertReadiness(),
                new HostProcessToolsMarkerPairJoiner(),
                new AcceptingVerifier(() => Store.Current),
                new InertLifecycle(),
                new InertOsPort());

        }

        internal string GuardedRoot { get; }

        internal ArcanumMaintenanceLock HeldLock { get; }

        internal RecordingActiveStore Store { get; }

        internal HostToolsMarkerPairResetCoordinator Subject { get; }

        internal InstallationResetActivePublication Current => Store.Current;

        internal HostToolsMarkerPairResetCheckpointV1 Checkpoint => Assert.IsType<
            HostToolsMarkerPairResetCheckpointV1>(
                Current.Payload.HostToolsMarkerPairReset);

        internal CampaignPathFullInstallationResetInventory Inventory => Value(
            CampaignPathFullInstallationResetInventory.Create(
                Current.Payload.OperationId,
                Checkpoint.CampaignInventory,
                Checkpoint.CampaignMarkerInventoryDigest));

        internal CampaignPathFullInstallationResetCleanupReceipt? Receipt { get; }

        internal CampaignPathFullInstallationResetCleanupPreparation Preparation() => Value(
            CampaignPathFullInstallationResetCleanupPreparation.Create(
                Current.Payload.OperationId,
                Checkpoint.OwnerEffectDigest,
                Inventory));

        internal async Task<Result<HostToolsMarkerPairResetCoordinator
            .FullInstallationResetMarkerCleanupAuthority>> MintAsync(
                ArcanumMaintenanceLock? heldLock = null,
                InstallationResetActivePublication? publication = null)
        {

            MethodInfo? reflectedMethod =
                typeof(HostToolsMarkerPairResetCoordinator).GetMethod(
                    "MintCleanupAuthorityAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(reflectedMethod);

            MethodInfo method = reflectedMethod;

            Task<Result<HostToolsMarkerPairResetCoordinator
                .FullInstallationResetMarkerCleanupAuthority>> task = Assert.IsType<
                    Task<Result<HostToolsMarkerPairResetCoordinator
                        .FullInstallationResetMarkerCleanupAuthority>>>(
                            method.Invoke(
                                Subject,
                                [
                                    heldLock ?? HeldLock,
                                    publication ?? Current,
                                    CancellationToken.None,
                                ]));

            return await task;

        }

        public void Dispose()
        {

            HeldLock.Dispose();

            Directory.Delete(GuardedRoot, recursive: true);

            // The maintenance lock lives beside the directory it guards, not inside it, so removing
            // the guarded root leaves the lock file behind at the temp root. Enough of those and the
            // suite stalls on lock contention rather than failing.
            File.Delete(RetroDownfall.Arcanum.Infrastructure.Backup.ArcanumMaintenanceLock.LockPathFor(GuardedRoot));

        }

    }

    private static InstallationResetActivePublication ReplaceReceipt(
        InstallationResetActivePublication publication,
        CampaignPathFullInstallationResetCleanupReceipt receipt)
    {

        HostToolsMarkerPairResetCheckpointV1 checkpoint = Assert.IsType<
            HostToolsMarkerPairResetCheckpointV1>(
                publication.Payload.HostToolsMarkerPairReset);

        return publication with
        {
            Payload = InstallationResetActivePayloadV3.FromRecord(
                publication.Payload.ToRecord() with
                {
                    HostToolsMarkerPairReset = checkpoint with
                    {
                        MarkerIntentCount = receipt.MarkerIntentCount,
                        OrderedMarkerIntentIds = receipt.OrderedMarkerIntentIds,
                        MarkerIntentVectorDigest = receipt.MarkerIntentVectorDigest,
                        DeletedCount = receipt.DeletedCount,
                        OrphanCount = receipt.OrphanCount,
                    },
                }),
        };

    }

    private sealed class RecordingActiveStore(
        string guardedRoot,
        InstallationResetActivePublication current) : IInstallationResetActiveStore
    {

        public string GuardedRoot { get; } = guardedRoot;

        internal InstallationResetActivePublication Current { get; set; } = current;

        internal int RecoverCalls { get; private set; }

        public Task<Result<InstallationResetActiveRecoveryState>> RecoverAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default)
        {

            RecoverCalls++;

            return Task.FromResult(Result<InstallationResetActiveRecoveryState>.Success(
                new InstallationResetActiveRecoveryState(
                    InstallationResetActiveRecoveryOutcome.AuthenticatedV2,
                    Current,
                    LegacyRecord: null)));

        }

        public Task<Result<InstallationResetActivePublication>> BeginAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord record,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetActivePublication>> AdvanceAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            InstallationResetActivePublication current,
            InstallationResetActiveRecord next,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetActiveRecoveryState>> InspectAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<InstallationResetActivePublication>> MigrateLegacyV1Async(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid installationId,
            InstallationResetActiveRecord expectedRecord,
            FileHandleIdentity expectedIdentity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> RetireAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> CompleteStartupCleanupAsync(
            ArcanumMaintenanceLock heldInstallationLock,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class AcceptingVerifier(
        Func<InstallationResetActivePublication> current)
        : IFullInstallationResetRemediationAttestationVerifier
    {

        public bool MatchesAuthenticatedClaim(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair,
            Guid acceptedOperationId,
            Guid acceptedInstallationId,
            CovenantDigest acceptedAttestationDigest,
            CovenantDigest acceptedNonceDigest,
            CovenantDigest acceptedIssuerDigest) =>
            throw new NotSupportedException();

        public Result<FullInstallationResetRemediationAuthorization> Verify(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid currentInstallationId,
            HostProcessToolsMatchedPair matchedPair) =>
            throw new NotSupportedException();

        public Result<FullInstallationResetRemediationAuthorization> VerifyAtAcceptedTime(
            FullInstallationResetExternalRemediationAttestation attestation,
            Guid authenticatedInstallationId,
            HostProcessToolsMatchedPair persistedPair,
            DateTimeOffset acceptedAtUtc)
        {

            FullInstallationResetRemediationClaimV1 claim = Assert.IsType<
                FullInstallationResetRemediationClaimV1>(
                    current().Payload.FullInstallationResetRemediationClaim);

            return Result<FullInstallationResetRemediationAuthorization>.Success(
                Authorization(claim));

        }

    }

    private sealed class InertDatabase : IHostToolsMarkerPairResetDatabase
    {

        public Task<Result<HostToolsMarkerPairResetDatabaseSession>>
            OpenHostToolsMarkerPairResetDatabaseSessionAsync(
            IStoppedHostGrimoireConnectionAuthority authority,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class InertReadiness : IFullInstallationResetCampaignSchemaReadiness
    {

        public Task<Result> RequireExactAsync(
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class InertOsPort : IHostToolsMarkerPairResetOsPort
    {

        public HostToolsMarkerPairResetOsOpenResult OpenExact() =>
            throw new NotSupportedException();

        public HostToolsMarkerPairResetOsOpenResult ReopenExact(
            HostProcessToolsOsMarkerEvidence expectedEvidence) =>
            throw new NotSupportedException();

        public Task<HostToolsMarkerPairResetOsDeleteStatus> CompareDeleteExactAsync(
            IHostToolsMarkerPairResetOsCapability capability,
            HostProcessToolsOsMarkerEvidence expectedEvidence,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<HostToolsMarkerPairResetOsAbsenceStatus> ProveExactAbsenceAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class InertLifecycle : ICampaignPathMarkerLifecycle
    {

        public Task<Result<CampaignPathFullInstallationResetInventory>>
            InventoryFullInstallationResetCleanupAsync(
                Guid ownerOperationId,
                SqliteConnection liveCoreConnection,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result> RevalidateFullInstallationResetInventoryAsync(
            CampaignPathFullInstallationResetInventory inventory,
            SqliteConnection liveCoreConnection,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
            PrepareFullInstallationResetCleanupAsync(
                CampaignPathFullInstallationResetCleanupPreparation preparation,
                CampaignPathFullInstallationResetCleanupReceipt? expectedReceipt,
                HostToolsMarkerPairResetCoordinator
                    .FullInstallationResetMarkerCleanupAuthority authority,
                SqliteConnection liveCoreConnection,
                SqliteTransaction liveCoreTransaction,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<CampaignPathFullInstallationResetCleanupReceipt>>
            ReconcileFullInstallationResetCleanupAsync(
                CampaignPathFullInstallationResetCleanupReceipt prepared,
                HostToolsMarkerPairResetCoordinator
                    .FullInstallationResetMarkerCleanupAuthority authority,
                SqliteConnection liveCoreConnection,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<CampaignPathRestoreCleanupInventory>> InventoryRestoreCleanupAsync(
            CovenantExclusiveRecoveryOwner owner,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<CampaignPathRestoreCleanupPreparationReceipt>>
            PrepareRestoreCleanupInStagedDatabaseAsync(
                CampaignPathRestoreCleanupPreparation preparation,
                SqliteConnection stagedConnection,
                SqliteTransaction stagedTransaction,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<CampaignPathMarkerGateCompletion>> ReconcileGateOwnedAsync(
            CampaignPathMarkerGateReconcileRequest request,
            ICovenantExclusiveOperationLease exclusiveLease,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask ReleaseRetainedRootsAsync(Guid ownerOperationId) =>
            throw new NotSupportedException();

    }

    private static CampaignMarkerInventoryEntryV1 Entry(Guid campaignId, byte seed) =>
        new(
            campaignId,
            1,
            Digest(seed),
            Digest(checked((byte)(seed + 1))),
            Digest(checked((byte)(seed + 2))),
            Digest(checked((byte)(seed + 3))));

    private static CovenantDigest Digest(byte value) =>
        new(Enumerable.Repeat(value, 32).ToArray());

    private static T Value<T>(Result<T> result)
    {

        Assert.True(result.IsSuccess, result.Error.Message);

        return result.Value;

    }

}
