using System.Globalization;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Repositories;

/// <summary>
/// The one path a Session graph takes across a protected boundary, against real SQLCipher databases.
/// </summary>
/// <remarks>
/// Two real encrypted databases and a real attachment tree, because every property under test is a
/// property of what the store reads back rather than of what the caller claimed. A faked source could
/// only ever agree with the request that described it (§10.13).
/// </remarks>
public sealed class ProtectedArtifactTransferStoreTests : IAsyncLifetime, IDisposable
{

    private static readonly string[] SourceObjects =
    [
        "Sessions",
        "Entries",
        "SessionAttachments",
        "artifact_sensitivity",
        "assistant_entry_finalizations",
    ];

    private static readonly string[] DestinationObjects =
    [
        "Sessions",
        "Entries",
        "SessionAttachments",
        "assistant_entry_finalizations",
        "protected_session_transfer_intents",
        "protected_session_transfer_blobs",
        "protected_session_transfer_intents_guard_update",
        "protected_session_transfer_intents_guard_delete",
        "protected_session_transfer_blobs_guard_update",
        "protected_session_transfer_blobs_guard_delete",

        // What an imported Entry has to be erasable through afterwards. The store is the only
        // production writer that puts a lowercase identity into "Entries"."Id", so the destination is
        // the one place a purge of such a row can be exercised without a test choosing the spelling.
        "entry_embeddings",
        "artifact_sensitivity",
        "session_sensitivity_state",
        "assistant_entry_erasure_receipts",
        "artifact_sensitivity_guard_delete",
        "artifact_sensitivity_guard_update",
    ];

    private readonly string _root =
        Directory.CreateTempSubdirectory("arcanum-transfer-store-").FullName;

    private readonly Guid _sourceSessionId = Guid.Parse("6f0a1b2c-3d4e-4f50-8192-a3b4c5d6e7f8");

    private CovenantSchemaScratchDatabase _source = null!;

    private CovenantSchemaScratchDatabase _destination = null!;

    private ProtectedArtifactTransferStore _store = null!;

    private string _sourceAttachments = string.Empty;

    private string _destinationAttachments = string.Empty;

    public async Task InitializeAsync()
    {

        _source = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await _source.InstallCoreObjectsAsync(SourceObjects, CancellationToken.None);

        _destination = await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        await _destination.InstallCoreObjectsAsync(DestinationObjects, CancellationToken.None);

        _sourceAttachments = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;

        _destinationAttachments = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;

        _store = new ProtectedArtifactTransferStore(
            CovenantSqliteConnectionInitializer.Instance,
            TimeProvider.System);

        await SeedSourceSessionAsync();

    }

    [Fact]
    public async Task A_substituted_effect_digest_is_refused_before_any_destination_write()
    {

        Guid operationId = Guid.NewGuid();

        ImportedSessionTransferRequest genuine = await BuildRequestAsync(operationId, null);

        ImportedSessionTransferRequest tampered = new(
            genuine.OperationId,
            genuine.SourceSessionId,
            genuine.DestinationSessionId,
            genuine.SourceEvidenceDigest,
            genuine.SourceManifestDigest,
            genuine.ManifestCounts,
            genuine.CampaignMapping,
            genuine.DestinationBindingDigest,
            Digest(0x5A));

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(tampered);

        Assert.True(completion.Result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, completion.Result.Error.Code);
        Assert.Equal(CovenantExclusiveLeaseDisposition.RollbackAndReopen, completion.Disposition);

        Assert.Equal(0, await CountDestinationAsync("Sessions"));
        Assert.Equal(0, await CountDestinationAsync("protected_session_transfer_intents"));

    }

    [Fact]
    public async Task A_source_session_carrying_a_covenant_label_can_never_be_transferred()
    {

        await AddSensitivityLabelAsync();

        ImportedSessionTransferRequest request = await BuildRequestAsync(Guid.NewGuid(), null);

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(request);

        Assert.True(completion.Result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, completion.Result.Error.Code);

        // The refusal is the store's own scan over the source's label rows, so a caller that omitted
        // the tainted artifact from its manifest still cannot get the graph committed.
        Assert.Equal(0, await CountDestinationAsync("Sessions"));

    }

    [Fact]
    public async Task A_manifest_that_does_not_match_the_source_graph_is_refused()
    {

        Guid operationId = Guid.NewGuid();

        ImportedSessionTransferRequest genuine = await BuildRequestAsync(operationId, null);

        // One fewer Entry than the source actually holds. The store counts for itself.
        ProtectedSessionTransferCounts understated = genuine.ManifestCounts with
        {
            Entries = genuine.ManifestCounts.Entries - 1,
        };

        CovenantDigest effect = ProtectedSessionTransferDigests.Effect(
            ProtectedSessionTransferKind.Import,
            operationId,
            genuine.SourceSessionId,
            null,
            genuine.DestinationSessionId,
            genuine.DestinationBindingDigest,
            genuine.SourceEvidenceDigest,
            genuine.SourceManifestDigest,
            understated).Value;

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(new ImportedSessionTransferRequest(
                operationId,
                genuine.SourceSessionId,
                genuine.DestinationSessionId,
                genuine.SourceEvidenceDigest,
                genuine.SourceManifestDigest,
                understated,
                null,
                genuine.DestinationBindingDigest,
                effect));

        Assert.True(completion.Result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, completion.Result.Error.Code);

        Assert.Equal(0, await CountDestinationAsync("Sessions"));

    }

    [Fact]
    public async Task A_campaign_mapped_import_requires_a_lease_that_closes_that_exact_scope()
    {

        Guid destinationCampaign = Guid.NewGuid();

        ImportedSessionTransferRequest request = await BuildRequestAsync(
            Guid.NewGuid(),
            new BackupSessionCampaignMapping(Guid.NewGuid(), destinationCampaign));

        // A Global lease for a Campaign-mapped import. The destination scope the operator chose is
        // not the scope the drain closed, so nothing may be written.
        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(request, ProtectedTransferScope.Global);

        Assert.True(completion.Result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.InvalidScope, completion.Result.Error.Code);

        Assert.Equal(0, await CountDestinationAsync("Sessions"));

    }

    [Fact]
    public async Task A_committed_import_writes_the_graph_and_its_imported_guards()
    {

        Guid destinationCampaign = Guid.NewGuid();

        ImportedSessionTransferRequest request = await BuildRequestAsync(
            Guid.NewGuid(),
            new BackupSessionCampaignMapping(Guid.NewGuid(), destinationCampaign));

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(request, ProtectedTransferScope.ForCampaign(destinationCampaign));

        Assert.True(
            completion.Result.IsSuccess,
            completion.Result.IsFailure ? completion.Result.Error.Message : string.Empty);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, completion.Disposition);

        Assert.Equal(1, await CountDestinationAsync("Sessions"));
        Assert.Equal(2, await CountDestinationAsync("Entries"));
        Assert.Equal(1, await CountDestinationAsync("SessionAttachments"));

        // The explicit mapping, not a guess and not a dropped binding.
        Assert.Equal(
            destinationCampaign.ToString("D"),
            await _destination.ScalarStringAsync(
                "SELECT \"CampaignId\" FROM \"Sessions\";",
                CancellationToken.None));

        // CommittedImported, and each guard names the evidence it was copied from. Without that
        // digest an imported finalization would look like a turn this installation actually ran.
        Assert.Equal(
            1,
            await _destination.ScalarLongAsync(
                "SELECT COUNT(*) FROM assistant_entry_finalizations "
                + "WHERE OutcomeCode = 3 AND SourceEvidenceDigest IS NOT NULL;",
                CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(
            _destinationAttachments,
            request.DestinationSessionId.ToString("N"),
            "note.txt")));

        // Pending until the caller spends its lease decision.
        Assert.Equal(4, await DestinationPhaseAsync());

        Assert.True((await completion.Finalizer.FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            CancellationToken.None)).IsSuccess);

        Assert.Equal(5, await DestinationPhaseAsync());

    }

    /// <summary>
    /// Every import re-owns its blobs onto the Session id the destination row is written under.
    /// </summary>
    /// <remarks>
    /// A remap that never matches is not a cosmetic defect: the bytes land under the SOURCE Session's
    /// directory in the destination tree, the row stores that leaf, and
    /// <c>TryDeleteSessionDirectory(importedSessionId)</c> then reclaims a directory that was never
    /// created — so every non-colliding import leaks its attachments permanently.
    /// </remarks>
    [Fact]
    public async Task An_import_never_writes_its_blobs_under_the_source_sessions_owner_segment()
    {

        ImportedSessionTransferRequest request = await BuildRequestAsync(Guid.NewGuid(), null);

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(request);

        Assert.True(
            completion.Result.IsSuccess,
            completion.Result.IsFailure ? completion.Result.Error.Message : string.Empty);

        Assert.False(
            Directory.Exists(Path.Combine(_destinationAttachments, _sourceSessionId.ToString("N"))),
            "The destination tree must carry no directory named after the source Session.");

        Assert.True(File.Exists(Path.Combine(
            _destinationAttachments,
            request.DestinationSessionId.ToString("N"),
            "note.txt")));

        // The stored leaf has to agree with the bytes, or the row points at nothing.
        Assert.Equal(
            request.DestinationSessionId.ToString("N") + "/note.txt",
            await _destination.ScalarStringAsync(
                "SELECT \"RelativePath\" FROM \"SessionAttachments\";",
                CancellationToken.None));

    }

    [Fact]
    public async Task An_import_copies_no_turn_claim_and_fabricates_no_replay_authority()
    {

        ImportedSessionTransferRequest request = await BuildRequestAsync(Guid.NewGuid(), null);

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(request);

        Assert.True(completion.Result.IsSuccess);

        // No claim table was installed in the destination at all, so a store that tried to copy one
        // would have failed loudly. The guard rows that do exist carry no final receipt digest.
        Assert.Equal(
            0,
            await _destination.ScalarLongAsync(
                "SELECT COUNT(*) FROM assistant_entry_finalizations WHERE FinalReceiptDigest IS NOT NULL;",
                CancellationToken.None));

    }

    [Fact]
    public async Task A_repeated_import_is_idempotent_by_its_operation_identity()
    {

        ImportedSessionTransferRequest request = await BuildRequestAsync(Guid.NewGuid(), null);

        Assert.True((await CommitAsync(request)).Result.IsSuccess);

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> replay =
            await CommitAsync(request);

        Assert.True(
            replay.Result.IsSuccess,
            replay.Result.IsFailure ? replay.Result.Error.Message : string.Empty);

        // One destination Session, not two. Idempotency is keyed only to the import operation.
        Assert.Equal(1, await CountDestinationAsync("Sessions"));
        Assert.Equal(1, await CountDestinationAsync("protected_session_transfer_intents"));

    }

    [Fact]
    public async Task The_same_operation_identity_cannot_be_reused_for_a_different_destination()
    {

        Guid operationId = Guid.NewGuid();

        ImportedSessionTransferRequest first = await BuildRequestAsync(operationId, null);

        Assert.True((await CommitAsync(first)).Result.IsSuccess);

        ImportedSessionTransferRequest retargeted = await BuildRequestAsync(operationId, null);

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> conflict =
            await CommitAsync(retargeted);

        Assert.True(conflict.Result.IsFailure);
        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, conflict.Result.Error.Code);

        Assert.Equal(1, await CountDestinationAsync("Sessions"));

    }

    [Fact]
    public async Task The_transfer_finalizer_runs_once_and_only_for_a_reopening_disposition()
    {

        ImportedSessionTransferRequest request = await BuildRequestAsync(Guid.NewGuid(), null);

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion =
            await CommitAsync(request);

        Assert.True(completion.Result.IsSuccess);

        Assert.True((await completion.Finalizer.FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition.KeepClosed,
            CancellationToken.None)).IsFailure);

        Assert.Equal(4, await DestinationPhaseAsync());

    }

    /// <summary>
    /// An imported Entry is still Covenant-erasable, over the identity this store chose for it.
    /// </summary>
    /// <remarks>
    /// Lives here rather than beside the other erasure suites because the precondition is this store's
    /// own destination write: it spells a new Entry identity lowercase, while the object-relational
    /// writer that fills the same column everywhere else spells it uppercase. The equivalent assertion
    /// over an object-relational Entry passes whether or not the purge compares identity correctly,
    /// because that spelling is the one the label ledger also uses — so it would be evidence of
    /// nothing. Nothing here seeds an Entry, an identity, or a label row: the store writes the graph,
    /// the ledger writes the label, and the kernel is entered at its outermost method.
    /// </remarks>
    [Fact]
    public async Task An_imported_entry_is_erased_by_the_protected_artifact_erasure_kernel()
    {

        ImportedSessionTransferRequest request = await BuildRequestAsync(Guid.NewGuid(), null);

        Assert.True((await CommitAsync(request)).Result.IsSuccess);

        // Read back rather than remembered. The identity under test is the one the store chose for the
        // destination row, and a value supplied here would prove nothing about the spelling it stores.
        string? importedEntryId = await _destination.ScalarStringAsync(
            "SELECT \"Id\" FROM \"Entries\" WHERE \"Role\" = 1;",
            CancellationToken.None);

        Assert.NotNull(importedEntryId);

        // The precondition this case exists for, pinned rather than assumed. Everything below would
        // still pass if the store switched to the uppercase spelling the label ledger uses — and would
        // then be proving nothing, because that spelling matches under either comparison. A silent
        // degradation into a no-evidence test is worse than a loud failure here.
        Assert.Equal(importedEntryId.ToLowerInvariant(), importedEntryId);

        Guid entryId = Guid.Parse(importedEntryId, CultureInfo.InvariantCulture);

        ArtifactSensitivityLedger ledger = new(new FixedCovenantConnectionSource(_destination.Connection));

        Result<LabeledArtifactWriteReceipt> labelled = await ledger.LabelAsync(
            new DerivedArtifactWrite(
                SensitiveArtifactKind.AssistantEntry,
                entryId,

                // Deliberately no Session. The property under test is which "Entries" row the purge
                // matches, and that comparison never reads the Session, so naming one would only add a
                // second reason this case could fail. The Session projection an imported artifact's
                // label does reach is proven by its own case below.
                sessionId: null,
                campaignId: null,
                turnId: null,
                artifactRevision: 1,
                DerivedArtifactContentDigest.ForText("answer"),
                ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([CovenantOperationGateFixture.DatasetGeneration])),
            CancellationToken.None);

        Assert.True(labelled.IsSuccess, labelled.IsFailure ? labelled.Error.Message : string.Empty);

        ArtifactSensitivityLabel label = (await ledger.TryReadLabelAsync(
            SensitiveArtifactKind.AssistantEntry,
            entryId,
            CancellationToken.None)).Value!;

        CovenantProtectedArtifactErasureKernel kernel = new(
            new FixedCovenantConnectionSource(_destination.Connection),
            CovenantSqliteConnectionInitializer.Instance,
            TimeProvider.System);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease lease = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
            CancellationToken.None)).Value;

        Result<CovenantArtifactErasureProgress> erased = await kernel.ErasePageAsync(
            new CovenantProtectedArtifactErasurePage(
                CovenantOperationGateFixture.DatasetGeneration,
                [
                    new CovenantProtectedArtifactErasureItem(
                        label.ArtifactId,
                        label.ArtifactKind,
                        label.SessionId,
                        label.LabelId,
                        label,
                        label.ArtifactContentDigest,
                        label.ArtifactRevision),
                ]),
            CovenantArtifactErasureAuthority
                .ForExclusive(lease, CovenantExclusiveOperation.CovenantFamilyReinitialize)
                .Value,
            CancellationToken.None);

        Assert.True(erased.IsSuccess);

        Assert.Equal(CovenantErasureBlocker.None, erased.Value.Blocker);

        Assert.Equal(1UL, erased.Value.ErasedCount);

        // The assistant Entry is gone and the user Entry beside it is untouched, so the purge matched
        // one row rather than every row or none.
        Assert.Equal(1, await CountDestinationAsync("Entries"));

        Assert.Equal(
            0,
            await _destination.ScalarLongAsync(
                "SELECT COUNT(*) FROM \"Entries\" WHERE \"Role\" = 1;",
                CancellationToken.None));

        Assert.Equal(0, await CountDestinationAsync("artifact_sensitivity"));

    }

    /// <summary>
    /// Labelling an artifact of an imported Session reaches that Session's taint projection.
    /// </summary>
    /// <remarks>
    /// <c>session_sensitivity_state.SessionId</c> declares <c>REFERENCES "Sessions" ("Id")</c>, and
    /// SQLite resolves a foreign key by byte equality rather than through a predicate. The ledger
    /// spelled that identity uppercase while this store spells a Session it created lowercase, so
    /// every attempt to label any artifact of an imported Session failed on the constraint and the
    /// Session was never recorded as tainted at all. Nothing here seeds a Session or a projection row:
    /// the store writes the Session, the ledger writes the label, and the assertion reads the
    /// projection back through the ledger's own production reader — the one the dispatch gate consults
    /// on every session-backed turn.
    /// </remarks>
    [Fact]
    public async Task A_label_on_an_imported_sessions_artifact_reaches_that_sessions_projection()
    {

        ImportedSessionTransferRequest request = await BuildRequestAsync(Guid.NewGuid(), null);

        Assert.True((await CommitAsync(request)).Result.IsSuccess);

        ImportedArtifact imported = await ReadImportedArtifactAsync();

        ArtifactSensitivityLedger ledger = new(new FixedCovenantConnectionSource(_destination.Connection));

        Result<LabeledArtifactWriteReceipt> labelled = await ledger.LabelAsync(
            ImportedEntryLabel(imported),
            CancellationToken.None);

        Assert.True(labelled.IsSuccess, labelled.IsFailure ? labelled.Error.Message : string.Empty);

        // The projection row exists and agrees with the Session row it references, byte for byte.
        // Asserting the text rather than only the count is what distinguishes "the foreign key
        // resolved" from "the constraint happened not to be enforced".
        Assert.Equal(
            imported.StoredSessionId,
            await _destination.ScalarStringAsync(
                "SELECT SessionId FROM session_sensitivity_state;",
                CancellationToken.None));

        Result<SessionSensitivityProjection> projection =
            await ledger.ReadSessionProjectionAsync(imported.SessionId, CancellationToken.None);

        Assert.True(projection.IsSuccess, projection.IsFailure ? projection.Error.Message : string.Empty);

        Assert.True(projection.Value.IsTainted);

        Assert.Equal(1, projection.Value.TaintedArtifactCount);

    }

    /// <summary>
    /// A Session this installation imported and then labelled can never leave it in plaintext.
    /// </summary>
    /// <remarks>
    /// The guard this proves is the only member of its defect family that failed open. The taint scan
    /// bound a lowercase identity against <c>artifact_sensitivity.SessionId</c>, which the label ledger
    /// writes uppercase, so the count was zero for every labelled Session and the refusal never fired.
    /// The suite's existing coverage could not see it: that case seeds the label row itself, in the
    /// spelling the broken comparison already matched.
    ///
    /// <para>Nothing here seeds a label. A Session is imported, one of its artifacts is labelled
    /// through the real ledger, and the onward transfer is a genuine request built from the graph the
    /// store itself wrote — so without the refusal it would commit, which is exactly what the
    /// companion case asserts.</para>
    /// </remarks>
    [Fact]
    public async Task An_imported_session_carrying_a_covenant_label_can_never_be_transferred_onward()
    {

        Assert.True((await CommitAsync(await BuildRequestAsync(Guid.NewGuid(), null))).Result.IsSuccess);

        ImportedArtifact imported = await ReadImportedArtifactAsync();

        ArtifactSensitivityLedger ledger = new(new FixedCovenantConnectionSource(_destination.Connection));

        Result<LabeledArtifactWriteReceipt> labelled = await ledger.LabelAsync(
            ImportedEntryLabel(imported),
            CancellationToken.None);

        Assert.True(labelled.IsSuccess, labelled.IsFailure ? labelled.Error.Message : string.Empty);

        await using CovenantSchemaScratchDatabase onward = await CreateOnwardDestinationAsync();

        string onwardAttachments = Directory.CreateDirectory(Path.Combine(_root, "onward")).FullName;

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion = await CommitAsync(
            await BuildRequestAsync(Guid.NewGuid(), null, _destination, imported.SessionId),
            _destination,
            _destinationAttachments,
            onward,
            onwardAttachments);

        Assert.True(completion.Result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, completion.Result.Error.Code);

        Assert.Contains("Covenant-derived", completion.Result.Error.Message, StringComparison.Ordinal);

        // Refused before the graph was read, so nothing of the labelled Session reached the plaintext
        // destination at all.
        Assert.Equal(
            0,
            await onward.ScalarLongAsync("SELECT COUNT(*) FROM \"Sessions\";", CancellationToken.None));

        Assert.Equal(
            0,
            await onward.ScalarLongAsync("SELECT COUNT(*) FROM \"Entries\";", CancellationToken.None));

    }

    /// <summary>
    /// The same onward request commits when nothing about the imported Session is labelled.
    /// </summary>
    /// <remarks>
    /// The control for the refusal above. Without it, "the transfer failed" would be consistent with a
    /// request the onward hop could never have satisfied for some unrelated reason, and the refusal
    /// would be evidence of nothing.
    /// </remarks>
    [Fact]
    public async Task An_unlabelled_imported_session_transfers_onward()
    {

        Assert.True((await CommitAsync(await BuildRequestAsync(Guid.NewGuid(), null))).Result.IsSuccess);

        ImportedArtifact imported = await ReadImportedArtifactAsync();

        await using CovenantSchemaScratchDatabase onward = await CreateOnwardDestinationAsync();

        string onwardAttachments = Directory.CreateDirectory(Path.Combine(_root, "onward")).FullName;

        ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt> completion = await CommitAsync(
            await BuildRequestAsync(Guid.NewGuid(), null, _destination, imported.SessionId),
            _destination,
            _destinationAttachments,
            onward,
            onwardAttachments);

        Assert.True(
            completion.Result.IsSuccess,
            completion.Result.IsFailure ? completion.Result.Error.Message : string.Empty);

        Assert.Equal(
            1,
            await onward.ScalarLongAsync("SELECT COUNT(*) FROM \"Sessions\";", CancellationToken.None));

    }

    public async Task DisposeAsync()
    {

        if (_source is not null)
        {

            await _source.DisposeAsync();

        }

        if (_destination is not null)
        {

            await _destination.DisposeAsync();

        }

    }

    public void Dispose()
    {

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover scratch directory is not worth failing a suite over.
        }

    }

    private static CovenantDigest Digest(byte seed) => new([.. Enumerable.Repeat(seed, 32)]);

    /// <summary>
    /// A Bloom the schema will accept: an overflow always has bits set, so an all-zero bitset is
    /// refused as a provenance nothing could have produced.
    /// </summary>
    private static byte[] NonZeroBloom() => [.. Enumerable.Repeat((byte)0x0F, 32)];

    /// <summary>The label an imported assistant Entry carries, naming the Session it belongs to.</summary>
    /// <remarks>
    /// Naming the Session is the point. It is what makes the label reach
    /// <c>session_sensitivity_state</c> and what makes the plaintext-export taint scan able to find it,
    /// and both of those cross the identity spelling this store chose for the destination Session.
    /// </remarks>
    private static DerivedArtifactWrite ImportedEntryLabel(ImportedArtifact imported) =>
        new(
            SensitiveArtifactKind.AssistantEntry,
            imported.EntryId,
            imported.SessionId,
            campaignId: null,
            turnId: null,
            artifactRevision: 1,
            DerivedArtifactContentDigest.ForText("answer"),
            ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([CovenantOperationGateFixture.DatasetGeneration]));

    /// <summary>
    /// The imported Session and assistant Entry, read out of the rows the store wrote.
    /// </summary>
    /// <remarks>
    /// Read back rather than remembered, and the lowercase spelling is pinned rather than assumed. If
    /// this store ever switched to the uppercase spelling the label ledger uses, every case built on
    /// this would keep passing while proving nothing, because that spelling is the one the defects
    /// under test already matched.
    /// </remarks>
    private async Task<ImportedArtifact> ReadImportedArtifactAsync()
    {

        string? sessionId = await _destination.ScalarStringAsync(
            "SELECT \"Id\" FROM \"Sessions\";",
            CancellationToken.None);

        Assert.NotNull(sessionId);

        Assert.Equal(sessionId.ToLowerInvariant(), sessionId);

        string? entryId = await _destination.ScalarStringAsync(
            "SELECT \"Id\" FROM \"Entries\" WHERE \"Role\" = 1;",
            CancellationToken.None);

        Assert.NotNull(entryId);

        Assert.Equal(entryId.ToLowerInvariant(), entryId);

        return new ImportedArtifact(
            sessionId,
            Guid.Parse(sessionId, CultureInfo.InvariantCulture),
            Guid.Parse(entryId, CultureInfo.InvariantCulture));

    }

    /// <summary>A third database, to carry a Session onward out of the one this suite imports into.</summary>
    private static async Task<CovenantSchemaScratchDatabase> CreateOnwardDestinationAsync()
    {

        CovenantSchemaScratchDatabase onward =
            await CovenantSchemaScratchDatabase.CreateAsync(CancellationToken.None);

        try
        {

            await onward.InstallCoreObjectsAsync(DestinationObjects, CancellationToken.None);

            return onward;

        }
        catch
        {

            await onward.DisposeAsync();

            throw;

        }

    }

    private Task<ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>> CommitAsync(
        ImportedSessionTransferRequest request,
        ProtectedTransferScope? scope = null) =>
        CommitAsync(request, _source, _sourceAttachments, _destination, _destinationAttachments, scope);

    private async Task<ProtectedSessionTransferCompletion<ImportedSessionCommitReceipt>> CommitAsync(
        ImportedSessionTransferRequest request,
        CovenantSchemaScratchDatabase source,
        string sourceAttachments,
        CovenantSchemaScratchDatabase destination,
        string destinationAttachments,
        ProtectedTransferScope? scope = null)
    {

        ProtectedTransferScope resolved = scope
            ?? (request.CampaignMapping is { } mapping
                ? ProtectedTransferScope.ForCampaign(mapping.DestinationCampaignId)
                : ProtectedTransferScope.Global);

        await using ImportedSessionSourceLease sourceLease =
            ImportedSessionSourceLease.Adopt(
                await source.OpenAdditionalConnectionAsync(CancellationToken.None),
                sourceAttachments);

        await using CovenantProtectedTransferLease transferLease = new(
            new StubTransferRegistration(
                new CovenantExclusiveRecoveryOwner(
                    request.OperationId,
                    CovenantExclusiveOperation.ProtectedSessionTransfer,
                    request.TransferEffectDigest),
                resolved.ToOperationScope()));

        return await _store.CommitImportedSessionAsync(
            request,
            sourceLease,
            transferLease,
            new ProtectedSessionImportDestination(destination.Connection, destinationAttachments),
            CancellationToken.None);

    }

    private Task<ImportedSessionTransferRequest> BuildRequestAsync(
        Guid operationId,
        BackupSessionCampaignMapping? mapping) =>
        BuildRequestAsync(operationId, mapping, _source, _sourceSessionId);

    /// <summary>
    /// Builds the request exactly as the importer must: every digest and count derived from the source
    /// the store will read, never invented.
    /// </summary>
    private static async Task<ImportedSessionTransferRequest> BuildRequestAsync(
        Guid operationId,
        BackupSessionCampaignMapping? mapping,
        CovenantSchemaScratchDatabase source,
        Guid sourceSessionId)
    {

        Guid destinationSessionId = Guid.NewGuid();

        CovenantDigest sourceEvidence = Digest(0x11);

        ProtectedSessionTransferCounts counts = await CountSourceGraphAsync(source, sourceSessionId);

        CovenantDigest manifest = await ComputeSourceManifestAsync(source, sourceSessionId);

        CovenantDigest binding = ProtectedSessionTransferDigests.DestinationBinding(
            mapping is null ? CovenantScope.Global : CovenantScope.Campaign,
            mapping?.DestinationCampaignId,
            mapping?.SourceCampaignId);

        CovenantDigest effect = ProtectedSessionTransferDigests.Effect(
            ProtectedSessionTransferKind.Import,
            operationId,
            sourceSessionId,
            null,
            destinationSessionId,
            binding,
            sourceEvidence,
            manifest,
            counts).Value;

        return new ImportedSessionTransferRequest(
            operationId,
            sourceSessionId,
            destinationSessionId,
            sourceEvidence,
            manifest,
            counts,
            mapping,
            binding,
            effect);

    }

    /// <summary>
    /// Counts the source graph the way the store counts it: owned by the Session under transfer, and
    /// over the finalization outcome the store copies.
    /// </summary>
    /// <remarks>
    /// Owner-scoped rather than whole-table, because an onward transfer reads a database this store
    /// itself wrote, and the finalizations it wrote there carry the imported outcome the source reader
    /// deliberately skips. A whole-table count would claim a finalization the graph does not contain.
    /// </remarks>
    private static async Task<ProtectedSessionTransferCounts> CountSourceGraphAsync(
        CovenantSchemaScratchDatabase source,
        Guid sourceSessionId)
    {

        long attachments = await OwnedCountAsync(
            source,
            "SELECT COUNT(*) FROM \"SessionAttachments\" WHERE \"SessionId\" = $id;",
            sourceSessionId);

        return new(
            1,
            (ulong)await OwnedCountAsync(
                source,
                "SELECT COUNT(*) FROM \"Entries\" WHERE \"SessionId\" = $id;",
                sourceSessionId),
            (ulong)attachments,
            (ulong)attachments,
            (ulong)await OwnedCountAsync(
                source,
                "SELECT COUNT(*) FROM assistant_entry_finalizations WHERE SessionId = $id AND OutcomeCode = 1;",
                sourceSessionId),
            0);

    }

    private static async Task<long> OwnedCountAsync(
        CovenantSchemaScratchDatabase database,
        string sql,
        Guid sessionId)
    {

        await using SqliteCommand command = database.Connection.CreateCommand();

        command.CommandText = sql;

        // The identity as the row holds it, not a spelling this test chose: the graph under count may
        // have been written by this store, which spells a Session lowercase, or seeded uppercase.
        _ = command.Parameters.AddWithValue("$id", await StoredSessionIdAsync(database, sessionId));

        return Convert.ToInt64(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);

    }

    /// <summary>
    /// The exact text <c>"Sessions"."Id"</c> holds for one Session in one database.
    /// </summary>
    private static async Task<string> StoredSessionIdAsync(
        CovenantSchemaScratchDatabase database,
        Guid sessionId)
    {

        await using SqliteCommand command = database.Connection.CreateCommand();

        command.CommandText =
            "SELECT \"Id\" FROM \"Sessions\" WHERE lower(replace(\"Id\", '-', '')) = $key;";

        _ = command.Parameters.AddWithValue("$key", sessionId.ToString("N"));

        return await command.ExecuteScalarAsync(CancellationToken.None) as string
            ?? throw new InvalidOperationException($"No Session row carries the identity {sessionId}.");

    }

    /// <summary>
    /// Reproduces the manifest the store computes, from the same source rows and the same order.
    /// </summary>
    private static async Task<CovenantDigest> ComputeSourceManifestAsync(
        CovenantSchemaScratchDatabase source,
        Guid sourceSessionId)
    {

        string session = await StoredSessionIdAsync(source, sourceSessionId);

        List<byte[]> items = [];

        await using (SqliteCommand entries = source.Connection.CreateCommand())
        {

            entries.CommandText =
                "SELECT \"Id\", \"Sequence\" FROM \"Entries\" WHERE \"SessionId\" = $id ORDER BY \"Sequence\", \"Id\";";

            _ = entries.Parameters.AddWithValue("$id", session);

            await using SqliteDataReader reader = await entries.ExecuteReaderAsync(CancellationToken.None);

            while (await reader.ReadAsync(CancellationToken.None))
            {

                items.Add(System.Text.Encoding.UTF8.GetBytes(
                    $"entry:{reader.GetString(0)}:{reader.GetInt32(1)}"));

            }

        }

        await using (SqliteCommand attachments = source.Connection.CreateCommand())
        {

            attachments.CommandText = """
                SELECT "RelativePath", "ContentSha256", "ByteLength"
                FROM "SessionAttachments" WHERE "SessionId" = $id ORDER BY "Id";
                """;

            _ = attachments.Parameters.AddWithValue("$id", session);

            await using SqliteDataReader reader =
                await attachments.ExecuteReaderAsync(CancellationToken.None);

            while (await reader.ReadAsync(CancellationToken.None))
            {

                items.Add(System.Text.Encoding.UTF8.GetBytes(
                    $"attachment:{reader.GetString(0)}:"
                    + $"{Convert.ToHexString(Convert.FromHexString(reader.GetString(1)))}:{reader.GetInt64(2)}"));

            }

        }

        await using (SqliteCommand finalizations = source.Connection.CreateCommand())
        {

            finalizations.CommandText = """
                SELECT AssistantEntryId FROM assistant_entry_finalizations
                WHERE SessionId = $id AND OutcomeCode = 1 ORDER BY AssistantEntryId;
                """;

            _ = finalizations.Parameters.AddWithValue("$id", session);

            await using SqliteDataReader reader =
                await finalizations.ExecuteReaderAsync(CancellationToken.None);

            while (await reader.ReadAsync(CancellationToken.None))
            {

                items.Add(System.Text.Encoding.UTF8.GetBytes($"finalization:{reader.GetString(0)}"));

            }

        }

        return ProtectedSessionTransferDigests.Manifest([.. items]);

    }

    private async Task SeedSourceSessionAsync()
    {

        string session = _sourceSessionId.ToString("D");

        string now = DateTimeOffset.UnixEpoch.UtcDateTime.ToString(
            "yyyy-MM-ddTHH:mm:ss.fffffffZ",
            CultureInfo.InvariantCulture);

        await _source.ExecuteAsync(
            $"""
             INSERT INTO "Sessions" ("Id", "CampaignId", "Title", "Status", "CreatedAt", "UpdatedAt",
                                     "Summary", "LastSummarizedMessageAt", "TotalTokensUsed",
                                     "TotalCostUsd", "UnsummarizedEntryCount", "ForkedFromSessionId")
             VALUES ('{session}', NULL, 'Imported', 0, '{now}', '{now}', NULL, NULL, 0, 0, 0, NULL);
             """,
            CancellationToken.None);

        string assistantEntryId = Guid.Parse("aaaaaaaa-1111-4222-8333-444444444444").ToString("D");

        await _source.ExecuteAsync(
            $"""
             INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                    "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
             VALUES ('{Guid.Parse("bbbbbbbb-1111-4222-8333-444444444444")}', '{session}', 0, 'ask',
                     '', '{now}', 1, NULL, NULL, NULL, 0);

             INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt",
                                    "Sequence", "ToolCallId", "ToolName", "ToolArguments", "IsPinned")
             VALUES ('{assistantEntryId}', '{session}', 1, 'answer', 'model', '{now}', 2,
                     NULL, NULL, NULL, 0);
             """,
            CancellationToken.None);

        // The owner segment is seeded exactly as SessionAttachmentStore.BuildRelativePath writes it —
        // ToString("N") — because the archive under import was produced by that writer. Seeding the
        // dashed form instead would agree with the remap's own spelling and hide whether the remap
        // ever matches a real attachment tree.
        string owner = _sourceSessionId.ToString("N");

        string payload = Path.Combine(_sourceAttachments, owner, "note.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(payload)!);

        byte[] bytes = "attached"u8.ToArray();

        await File.WriteAllBytesAsync(payload, bytes, CancellationToken.None);

        string hash = Convert.ToHexString(SHA256.HashData(bytes));

        await _source.ExecuteAsync(
            $"""
             INSERT INTO "SessionAttachments"
                 ("Id", "SessionId", "EntryId", "PendingTurnId", "State", "LogicalKey",
                  "OriginalFileName", "Version", "RelativePath", "ContentSha256", "MimeType",
                  "ByteLength", "Kind", "CreatedAt", "SourceKind", "SourceStatus", "EncryptionVersion")
             VALUES ('{Guid.Parse("cccccccc-1111-4222-8333-444444444444")}', '{session}', NULL, NULL,
                     0, 'note', 'note.txt', 1, '{owner}/note.txt', '{hash}', 'text/plain',
                     {bytes.Length}, 0, '{now}', 'SnapshotOnly', 'NotApplicable', 0);
             """,
            CancellationToken.None);

        await using SqliteCommand finalization = _source.Connection.CreateCommand();

        finalization.CommandText = """
            INSERT INTO assistant_entry_finalizations (
                AssistantEntryId, SessionId, OutcomeCode, ContentSensitivityCode,
                ContentSensitivityDigest, RequestDigest, FinalReceiptDigest, SourceEvidenceDigest,
                FinalizedAtUtc)
            VALUES ($entry, $session, 1, 0, $sensitivity, $request, NULL, NULL, $now);
            """;

        _ = finalization.Parameters.AddWithValue("$entry", assistantEntryId);

        _ = finalization.Parameters.AddWithValue("$session", session);

        _ = finalization.Parameters.AddWithValue("$sensitivity", Digest(0x22).Bytes);

        _ = finalization.Parameters.AddWithValue("$request", Digest(0x33).Bytes);

        _ = finalization.Parameters.AddWithValue("$now", now);

        _ = await finalization.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task AddSensitivityLabelAsync()
    {

        await using SqliteCommand command = _source.Connection.CreateCommand();

        command.CommandText = """
            INSERT INTO artifact_sensitivity (
                LabelId, ArtifactKindCode, ArtifactId, SensitivityCode, ProvenanceModeCode,
                ExactGenerationIds, GenerationBloom, SessionId, CampaignId, TurnId, ArtifactRevision,
                ArtifactContentDigest, SensitivityDigest, ProducingPlanDigest,
                ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest,
                CreatedAtUtc)
            VALUES ($label, 1, $artifact, 1, 2, NULL, $bloom, $session, NULL, NULL, 1,
                    $content, $sensitivity, NULL, NULL, NULL, $labelDigest, $now);
            """;

        _ = command.Parameters.AddWithValue("$label", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue("$artifact", Guid.NewGuid().ToString("D"));

        _ = command.Parameters.AddWithValue("$bloom", NonZeroBloom());

        _ = command.Parameters.AddWithValue("$session", _sourceSessionId.ToString("D"));

        _ = command.Parameters.AddWithValue("$content", Digest(0x44).Bytes);

        _ = command.Parameters.AddWithValue("$sensitivity", Digest(0x55).Bytes);

        _ = command.Parameters.AddWithValue("$labelDigest", Digest(0x66).Bytes);

        _ = command.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UnixEpoch.UtcDateTime.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffffffZ",
                CultureInfo.InvariantCulture));

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private Task<long> CountDestinationAsync(string table) =>
        _destination.ScalarLongAsync($"SELECT COUNT(*) FROM \"{table}\";", CancellationToken.None);

    private Task<long> DestinationPhaseAsync() =>
        _destination.ScalarLongAsync(
            "SELECT PhaseCode FROM protected_session_transfer_intents;",
            CancellationToken.None);

    /// <summary>
    /// One imported Session and one of its imported Entries, as the destination rows spell them.
    /// </summary>
    /// <remarks>
    /// Carries the Session's stored text alongside the parsed identity because the two are the whole
    /// point: a foreign key into <c>"Sessions"."Id"</c> has to agree with the text, while every caller
    /// hands the store a <see cref="Guid"/>.
    /// </remarks>
    private sealed record ImportedArtifact(string StoredSessionId, Guid SessionId, Guid EntryId);

    /// <summary>
    /// The compound lease a caller already acquired. Only its snapshot matters here: the store must
    /// prove the lease closes the destination scope before it writes anything.
    /// </summary>
    private sealed class StubTransferRegistration(
        CovenantExclusiveRecoveryOwner owner,
        CovenantOperationScope scope) : ICovenantExclusiveLeaseRegistration
    {

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.NewGuid(),
            1,
            CovenantLeaseKind.ProtectedTransfer,
            CovenantLeaseCoverage.Scoped,
            scope,
            Guid.NewGuid(),
            1,
            1,
            1,
            null,
            null,
            null,
            null,
            owner,
            false);

        public CancellationToken Revocation => CancellationToken.None;

        public Result ExecuteWhileHeld(Func<Result> callback) => callback();

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask<Result> CompleteAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;

    }

}
