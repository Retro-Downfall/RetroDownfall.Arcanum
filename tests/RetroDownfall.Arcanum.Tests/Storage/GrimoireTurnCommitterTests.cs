using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Storage;

/// <summary>
/// The one-shot finalization guard and the atomicity of assistant content beside a staged Covenant
/// batch (§10.13).
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class GrimoireTurnCommitterTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public GrimoireTurnCommitterTests(GrimoireFixture fixture) =>
        _fixture = fixture;

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [SkippableFact]
    public async Task CommitTurnAsync_PersistsContentAndTheOneShotGuardTogether()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();
        IGrimoireTurnCommitter committer = Committer();

        Result<TurnCommitReceipt> committed = await committer.CommitTurnAsync(
            Request(sessionId, assistantEntryId, "the answer"),
            CancellationToken.None);

        Assert.True(committed.IsSuccess, committed.Error.Message);
        Assert.False(committed.Value.Replayed);
        Assert.Equal("the answer", await ReadContentAsync(assistantEntryId));
        Assert.Equal(
            (long)AssistantFinalizationOutcome.Committed,
            await ReadGuardOutcomeAsync(assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_ReplaysTheDurableAnswerInsteadOfRunningASecondFinalization()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();
        IGrimoireTurnCommitter committer = Committer();

        Result<TurnCommitReceipt> committed = await committer.CommitTurnAsync(
            Request(sessionId, assistantEntryId, "the answer"),
            CancellationToken.None);

        Assert.Equal(2, committed.Value.ThroughEntrySequence);

        DateTimeOffset later = DateTimeOffset.UtcNow.AddTicks(1);

        _ = _db!.Entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = "a later turn",
            ModelUsed = "test-model",
            CreatedAt = later,
            Sequence = 3L,
        });

        _ = _db.Entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.Assistant,
            Content = "a later answer",
            ModelUsed = "test-model",
            CreatedAt = later,
            Sequence = 4L,
        });

        _ = await _db.SaveChangesAsync(CancellationToken.None);

        Result<TurnCommitReceipt> replay = await committer.CommitTurnAsync(
            Request(sessionId, assistantEntryId, "a second, different answer"),
            CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);
        Assert.True(replay.Value.Replayed);
        Assert.Equal(AssistantFinalizationOutcome.Committed, replay.Value.Outcome);
        Assert.Equal(2, replay.Value.ThroughEntrySequence);
        Assert.Equal("the answer", await ReadContentAsync(assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_TreatsAValidEmptyResponseAsAnOrdinaryCommit()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        Result<TurnCommitReceipt> committed = await Committer().CommitTurnAsync(
            Request(sessionId, assistantEntryId, string.Empty),
            CancellationToken.None);

        Assert.True(committed.IsSuccess, committed.Error.Message);
        Assert.Equal(
            (long)AssistantFinalizationOutcome.Committed,
            await ReadGuardOutcomeAsync(assistantEntryId));
        Assert.NotNull(await ReadContentAsync(assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_FailsClosedWhenTheSameEntryIsReplayedForADifferentRequest()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();
        IGrimoireTurnCommitter committer = Committer();

        _ = await committer.CommitTurnAsync(
            Request(sessionId, assistantEntryId, "the answer"),
            CancellationToken.None);

        Result<TurnCommitReceipt> conflict = await committer.CommitTurnAsync(
            Request(sessionId, assistantEntryId, "the answer", requestSeed: 44),
            CancellationToken.None);

        Assert.True(conflict.IsFailure);
        Assert.Equal(ErrorCodes.Grimoire.WriteFailed, conflict.Error.Code);
        Assert.Equal("the answer", await ReadContentAsync(assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_DiscardRemovesTheUntouchedPlaceholderAndStillWritesItsGuard()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        Result<TurnCommitReceipt> discarded = await Committer().CommitTurnAsync(
            new TurnCommitRequest(
                assistantEntryId,
                sessionId,
                AssistantFinalizationOutcome.Discarded,
                string.Empty,
                CovenantTask6Fixture.D(31),
                ContentSensitivity.None,
                GenerationProvenance.CreateExact([])),
            CancellationToken.None);

        Assert.True(discarded.IsSuccess, discarded.Error.Message);
        Assert.Null(await ReadContentAsync(assistantEntryId));
        Assert.Equal(
            (long)AssistantFinalizationOutcome.Discarded,
            await ReadGuardOutcomeAsync(assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_RefusesToDiscardAPlaceholderThatAlreadyCarriesContent()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        // Streamed text landed on the placeholder but the turn never reached its guard, which is
        // exactly the state an interrupted stream leaves behind.
        _ = await _db!.Entries
            .Where(entry => entry.Id == assistantEntryId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(entry => entry.Content, "partial streamed text"),
                CancellationToken.None);

        Result<TurnCommitReceipt> refused = await Committer().CommitTurnAsync(
            new TurnCommitRequest(
                assistantEntryId,
                sessionId,
                AssistantFinalizationOutcome.Discarded,
                string.Empty,
                CovenantTask6Fixture.D(31),
                ContentSensitivity.None,
                GenerationProvenance.CreateExact([])),
            CancellationToken.None);

        Assert.True(refused.IsFailure);
        Assert.Equal(ErrorCodes.Grimoire.WriteFailed, refused.Error.Code);
        Assert.Equal("partial streamed text", await ReadContentAsync(assistantEntryId));
        Assert.Null(await ReadGuardOutcomeAsync(assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_RollsBackAssistantContentWhenThePublicationArmCannotRun()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();
        CovenantTurnPlan plan = CovenantTask6Fixture.IntegrationPlan();

        Result<TurnCommitReceipt> refused = await Committer().CommitTurnAsync(
            new TurnCommitRequest(
                assistantEntryId,
                sessionId,
                AssistantFinalizationOutcome.Committed,
                "the answer",
                CovenantTask6Fixture.D(43),
                ContentSensitivity.None,
                GenerationProvenance.CreateExact([]),
                finalReceiptDigest: null,
                mutations: [Intent(plan)],
                mutationBinding: new CovenantMutationBatchBinding(
                    CovenantTask6Fixture.DatasetGeneration,
                    1,
                    1)),
            CancellationToken.None);

        Assert.True(refused.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.Unavailable, refused.Error.Code);
        Assert.Equal(string.Empty, await ReadContentAsync(assistantEntryId));
        Assert.Null(await ReadGuardOutcomeAsync(assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_LabelsACovenantDerivedResponseInTheSameTransaction()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        Guid generation = Guid.NewGuid();

        Result<TurnCommitReceipt> committed = await Committer().CommitTurnAsync(
            new TurnCommitRequest(
                assistantEntryId,
                sessionId,
                AssistantFinalizationOutcome.Committed,
                "the tainted answer",
                CovenantTask6Fixture.D(51),
                ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([generation])),
            CancellationToken.None);

        Assert.True(committed.IsSuccess, committed.Error.Message);
        Assert.Equal("the tainted answer", await ReadContentAsync(assistantEntryId));

        Assert.Equal(
            (long)ContentSensitivity.CovenantDerived,
            await ScalarAsync(
                "SELECT SensitivityCode FROM artifact_sensitivity WHERE ArtifactId = $id;",
                assistantEntryId));

        // The Session projection is what the response-cache filter reads, so it has to move in the
        // same transaction rather than being recomputed later from the labels.
        Assert.Equal(
            1L,
            await ScalarAsync(
                "SELECT TaintedArtifactCount FROM session_sensitivity_state WHERE SessionId = $id;",
                sessionId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_LeavesNoLabelForAnUntaintedResponse()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        _ = await Committer().CommitTurnAsync(
            Request(sessionId, assistantEntryId, "an ordinary answer"),
            CancellationToken.None);

        Assert.Null(await ScalarAsync(
            "SELECT SensitivityCode FROM artifact_sensitivity WHERE ArtifactId = $id;",
            assistantEntryId));

        Assert.Null(await ScalarAsync(
            "SELECT TaintedArtifactCount FROM session_sensitivity_state WHERE SessionId = $id;",
            sessionId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_DiscardsWithoutLabellingAnEntryThatNoLongerExists()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        Result<TurnCommitReceipt> discarded = await Committer().CommitTurnAsync(
            new TurnCommitRequest(
                assistantEntryId,
                sessionId,
                AssistantFinalizationOutcome.Discarded,
                string.Empty,
                CovenantTask6Fixture.D(52),
                ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Guid.NewGuid()])),
            CancellationToken.None);

        Assert.True(discarded.IsSuccess, discarded.Error.Message);

        // A label pointing at a deleted placeholder would keep the Session tainted for content
        // nobody can read.
        Assert.Null(await ScalarAsync(
            "SELECT SensitivityCode FROM artifact_sensitivity WHERE ArtifactId = $id;",
            assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_RollsBackTheResponseWhenItsLabelContradictsAnExistingOne()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        // Evidence from an earlier attempt against this same assistant identity, describing
        // different bytes. A crash between labelling and finalizing leaves exactly this.
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        ArtifactSensitivityLedger ledger = new(new FixedCovenantConnectionSource(connection));

        Result<LabeledArtifactWriteReceipt> seeded = await ledger.LabelAsync(
            new DerivedArtifactWrite(
                SensitiveArtifactKind.AssistantEntry,
                assistantEntryId,
                sessionId,
                null,
                null,
                artifactRevision: 1,
                DerivedArtifactContentDigest.ForText("an earlier tainted answer"),
                ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Guid.NewGuid()])),
            CancellationToken.None);

        Assert.True(seeded.IsSuccess, seeded.Error.Message);

        Result<TurnCommitReceipt> conflicting = await Committer().CommitTurnAsync(
            new TurnCommitRequest(
                assistantEntryId,
                sessionId,
                AssistantFinalizationOutcome.Committed,
                "a different answer",
                CovenantTask6Fixture.D(54),
                ContentSensitivity.CovenantDerived,
                GenerationProvenance.CreateExact([Guid.NewGuid()])),
            CancellationToken.None);

        Assert.True(conflicting.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, conflicting.Error.Code);
        Assert.Equal(string.Empty, await ReadContentAsync(assistantEntryId));
        Assert.Null(await ReadGuardOutcomeAsync(assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_PublishesExactSagaUseBesideSuccessfulReplyAndReplayAddsNothing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        SagaUseCoordinate coordinate = await SeedSagaUseAsync();

        var request = new TurnCommitRequest(assistantEntryId, sessionId, AssistantFinalizationOutcome.Committed,
            "answer", CovenantTask6Fixture.D(43), ContentSensitivity.None, GenerationProvenance.CreateExact([]), sagaUses: [coordinate, coordinate]);

        Assert.True((await Committer().CommitTurnAsync(request, CancellationToken.None)).IsSuccess);

        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));

        Assert.True((await Committer().CommitTurnAsync(request, CancellationToken.None)).Value.Replayed);

        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_OmitsStaleSagaUseWithoutLosingSuccessfulReply()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        SagaUseCoordinate coordinate = await SeedSagaUseAsync();

        await SagaStore().CorrectAsync(coordinate.MemoryId, coordinate.ContentDigest.Bytes, "corrected observation", new float[64], DateTimeOffset.UtcNow, CancellationToken.None);

        var request = new TurnCommitRequest(assistantEntryId, sessionId, AssistantFinalizationOutcome.Committed,
            "answer", CovenantTask6Fixture.D(43), ContentSensitivity.None, GenerationProvenance.CreateExact([]), sagaUses: [coordinate]);

        Assert.True((await Committer().CommitTurnAsync(request, CancellationToken.None)).IsSuccess);

        Assert.Equal("answer", await ReadContentAsync(assistantEntryId));

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_FailedPublicationLeavesNeitherSagaUseNorReply()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        SagaUseCoordinate coordinate = await SeedSagaUseAsync();

        await using SqliteCommand command = ((SqliteConnection)_db!.Database.GetDbConnection()).CreateCommand();

        command.CommandText = "CREATE TRIGGER refuse_saga_use BEFORE INSERT ON saga_committed_uses BEGIN SELECT RAISE(ABORT, 'injected publication failure'); END";

        await command.ExecuteNonQueryAsync();

        var request = new TurnCommitRequest(assistantEntryId, sessionId, AssistantFinalizationOutcome.Committed,
            "answer", CovenantTask6Fixture.D(43), ContentSensitivity.None, GenerationProvenance.CreateExact([]), sagaUses: [coordinate]);

        Assert.True((await Committer().CommitTurnAsync(request, CancellationToken.None)).IsFailure);

        Assert.Equal(string.Empty, await ReadContentAsync(assistantEntryId));

        Assert.Null(await ReadGuardOutcomeAsync(assistantEntryId));

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_SourceErasureRemovesUseEvenWithForeignKeysDisabled()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        SagaUseCoordinate coordinate = await SeedSagaUseAsync();

        var request = new TurnCommitRequest(assistantEntryId, sessionId, AssistantFinalizationOutcome.Committed,
            "answer", CovenantTask6Fixture.D(43), ContentSensitivity.None, GenerationProvenance.CreateExact([]), sagaUses: [coordinate]);

        Assert.True((await Committer().CommitTurnAsync(request, CancellationToken.None)).IsSuccess);

        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));

        await using SqliteCommand command = ((SqliteConnection)_db!.Database.GetDbConnection()).CreateCommand();

        command.CommandText = "PRAGMA foreign_keys = OFF";

        await command.ExecuteNonQueryAsync();

        Assert.True(await SagaStore().DeleteAsync(coordinate.MemoryId, CancellationToken.None));

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_DiscardedReplyPublishesNoSagaUse()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        SagaUseCoordinate coordinate = await SeedSagaUseAsync();

        var request = new TurnCommitRequest(assistantEntryId, sessionId, AssistantFinalizationOutcome.Discarded,
            string.Empty, CovenantTask6Fixture.D(43), ContentSensitivity.None, GenerationProvenance.CreateExact([]), sagaUses: [coordinate]);

        Assert.True((await Committer().CommitTurnAsync(request, CancellationToken.None)).IsSuccess);

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));
    }

    [SkippableFact]
    public async Task RestoreSanitationRemovesProductionUseAuthority()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        SagaUseCoordinate coordinate = await SeedSagaUseAsync();

        var request = new TurnCommitRequest(assistantEntryId, sessionId, AssistantFinalizationOutcome.Committed,
            "answer", CovenantTask6Fixture.D(43), ContentSensitivity.None, GenerationProvenance.CreateExact([]), sagaUses: [coordinate]);

        Assert.True((await Committer().CommitTurnAsync(request, CancellationToken.None)).IsSuccess);

        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));

        Assert.True((await BackupRestoreMemoryEvaluationSanitation.SanitizeStagedAsync((SqliteConnection)_db!.Database.GetDbConnection(), CancellationToken.None)).IsSuccess);

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));
    }

    [SkippableFact]
    public async Task ErasingAssistantContentRemovesItsSagaUseWhileReplayGuardSurvives()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        SagaUseCoordinate coordinate = await SeedSagaUseAsync();

        var request = new TurnCommitRequest(assistantEntryId, sessionId, AssistantFinalizationOutcome.Committed,
            "answer", CovenantTask6Fixture.D(43), ContentSensitivity.None, GenerationProvenance.CreateExact([]), sagaUses: [coordinate]);

        Assert.True((await Committer().CommitTurnAsync(request, CancellationToken.None)).IsSuccess);

        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));

        await using SqliteCommand command = ((SqliteConnection)_db!.Database.GetDbConnection()).CreateCommand();

        command.CommandText = "DELETE FROM Entries WHERE Id = $id";

        command.Parameters.AddWithValue("$id", assistantEntryId.ToString("D").ToUpperInvariant());

        await command.ExecuteNonQueryAsync();

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));

        Assert.Equal(1L, await ReadGuardOutcomeAsync(assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_SourceRelabeledAfterRetrievalEarnsNoCleanUseProof()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        Guid memoryId = Guid.NewGuid();

        SagaUseCoordinate coordinate = await SeedSagaUseAsync(memoryId.ToString("N"));

        using var connections = new CovenantConnectionSource(_db!, FixtureOrdinaryConnectionFactory.For(_db!));

        var ledger = new ArtifactSensitivityLedger(connections);

        Assert.True((await ledger.LabelAsync(new DerivedArtifactWrite(SensitiveArtifactKind.Saga, memoryId,
            null, null, null, 1, DerivedArtifactContentDigest.ForText("useful exact observation"), ContentSensitivity.CovenantDerived,
            GenerationProvenance.CreateExact([Guid.NewGuid()])), CancellationToken.None)).IsSuccess);

        var request = new TurnCommitRequest(assistantEntryId, sessionId, AssistantFinalizationOutcome.Committed,
            "answer", CovenantTask6Fixture.D(43), ContentSensitivity.None, GenerationProvenance.CreateExact([]), sagaUses: [coordinate]);

        Assert.True((await Committer().CommitTurnAsync(request, CancellationToken.None)).IsSuccess);

        Assert.Equal("answer", await ReadContentAsync(assistantEntryId));

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));
    }

    [SkippableTheory]
    [InlineData(true, 1L)]
    [InlineData(false, 0L)]
    public async Task CommitTurnAsync_CampaignUseRequiresTheSameCanonicalCampaign(bool sameCampaign, long expected)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sourceSession, _) = await SeedTurnAsync();

        Guid campaign = Guid.NewGuid();

        await BindCampaignAsync(sourceSession, campaign);

        SagaUseCoordinate coordinate = await SeedSagaUseAsync(ownerSession: sourceSession, historicalCampaign: campaign.ToString("D").ToLowerInvariant());

        (Guid targetSession, Guid assistantEntryId) = await SeedTurnAsync();

        await BindCampaignAsync(targetSession, sameCampaign ? campaign : Guid.NewGuid());

        var request = new TurnCommitRequest(assistantEntryId, targetSession, AssistantFinalizationOutcome.Committed,
            "answer", CovenantTask6Fixture.D(43), ContentSensitivity.None, GenerationProvenance.CreateExact([]), sagaUses: [coordinate]);

        Assert.True((await Committer().CommitTurnAsync(request, CancellationToken.None)).IsSuccess);

        Assert.Equal(expected, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));
    }

    [SkippableFact]
    public async Task CommitTurnAsync_InconsistentSourceOwnerBindingEarnsNoUseProof()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        await BindCampaignAsync(sessionId, Guid.NewGuid());

        SagaUseCoordinate coordinate = await SeedSagaUseAsync();

        await using SqliteCommand command = ((SqliteConnection)_db!.Database.GetDbConnection()).CreateCommand();

        command.CommandText = "UPDATE saga_memories SET SessionId = $session WHERE Id = $memory";

        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));

        command.Parameters.AddWithValue("$memory", coordinate.MemoryId);

        await command.ExecuteNonQueryAsync();

        var request = new TurnCommitRequest(assistantEntryId, sessionId, AssistantFinalizationOutcome.Committed,
            "answer", CovenantTask6Fixture.D(43), ContentSensitivity.None, GenerationProvenance.CreateExact([]), sagaUses: [coordinate]);

        Assert.True((await Committer().CommitTurnAsync(request, CancellationToken.None)).IsSuccess);

        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM saga_committed_uses WHERE AssistantEntryId = $id", assistantEntryId));
    }

    private async Task BindCampaignAsync(Guid sessionId, Guid campaignId)
    {
        await using SqliteCommand command = ((SqliteConnection)_db!.Database.GetDbConnection()).CreateCommand();

        command.CommandText = """
            INSERT OR IGNORE INTO Campaigns(Id,Name,NameLower,Path,Type,Settings,CreatedAt,UpdatedAt)
            VALUES($campaign,$campaign,$campaign,$path,0,'{}',$now,$now);
            UPDATE Sessions SET CampaignId = $campaign WHERE Id = $session;
            """;

        command.Parameters.AddWithValue("$campaign", campaignId.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$session", sessionId.ToString("D").ToUpperInvariant());

        command.Parameters.AddWithValue("$path", "/campaigns/" + campaignId.ToString("N"));

        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));

        await command.ExecuteNonQueryAsync();

        using CovenantSqliteAuthorizationScope scope = CovenantSqliteConnectionInitializer.Instance.Authorize(command.Connection!, CovenantSqliteAuthorizationKind.SessionBindingWrite);

        command.CommandText = "INSERT INTO session_campaign_bindings(SessionId,BindingKindCode,CampaignId,BoundAtUtc) VALUES($session,2,$campaign,$now)";

        await command.ExecuteNonQueryAsync();
    }

    private async Task<SagaUseCoordinate> SeedSagaUseAsync(string id = "saga-use", Guid? ownerSession = null, string? historicalCampaign = null)
    {
        SagaMemoryStore store = SagaStore(annalsEnabled: historicalCampaign is null);

        await store.InsertAsync(id, "useful exact observation", DateTimeOffset.UtcNow.AddMinutes(-1), ownerSession, null, "extraction", new float[64], CancellationToken.None);

        if (historicalCampaign is not null)
        {
            var connection = (SqliteConnection)_db!.Database.GetDbConnection();

            await using SqliteTransaction transaction = connection.BeginTransaction();

            Assert.True(await AnnalsClaimWriter.AppendAssertAsync(connection, transaction, AnnalSubjectStore.Saga, id,
                AnnalOrigin.AgentExtracted, SagaMemoryScopeKind.Campaign, historicalCampaign, ContentSensitivity.None,
                AnnalContentDigest.ForSagaMemory("useful exact observation"), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-1), ownerSession, CancellationToken.None));

            await transaction.CommitAsync();
        }

        await using SqliteCommand command = ((SqliteConnection)_db!.Database.GetDbConnection()).CreateCommand();

        command.CommandText = "SELECT c.ClaimId, v.VersionId, v.ContentHashFormatCode, v.ContentHash FROM annal_claims c JOIN annal_heads h ON h.ClaimId = c.ClaimId JOIN annal_versions v ON v.VersionId = h.CurrentVersionId WHERE c.SubjectStoreCode = 1 AND c.SubjectId = $memory";

        command.Parameters.AddWithValue("$memory", id);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        return new SagaUseCoordinate(id, reader.GetString(0), reader.GetString(1), reader.GetInt32(2), new CovenantDigest((byte[])reader[3]));
    }

    private SagaMemoryStore SagaStore(bool annalsEnabled = true)
    {
        var options = new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings
        {
            Features = new FeatureSettings { Annals = annalsEnabled },
            Integrations = new IntegrationSettings { Embeddings = new EmbeddingIntegrationSettings { Dimensions = 64 } },
        });

        return new SagaMemoryStore(_db!, new WeaveIndexAvailability(), options, MemoryErasureTestKeys.Isolated());

    }

    private async Task<long?> ScalarAsync(string sql, Guid id)
    {
        await using SqliteCommand command = ((SqliteConnection)_db!.Database.GetDbConnection()).CreateCommand();

        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync(CancellationToken.None);
        }

        command.CommandText = sql;

        _ = command.Parameters.AddWithValue("$id", id.ToString().ToUpperInvariant());

        object? value = await command.ExecuteScalarAsync(CancellationToken.None);

        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static TurnCommitRequest Request(
        Guid sessionId,
        Guid assistantEntryId,
        string finalText,
        byte requestSeed = 43) =>
        new(
            assistantEntryId,
            sessionId,
            AssistantFinalizationOutcome.Committed,
            finalText,
            CovenantTask6Fixture.D(requestSeed),
            ContentSensitivity.None,
            GenerationProvenance.CreateExact([]));

    private static CovenantMutationIntent Intent(CovenantTurnPlan plan) =>
        new(
            Guid.NewGuid(),
            CovenantMutationKind.AgentPropose,
            CovenantOperation.Set,
            CovenantOrigin.AgentProposed,
            new CovenantMutationTarget(
                CovenantOperationScope.ForCampaign(CovenantTask6Fixture.CampaignId),
                new CovenantKey("tests.output"),
                "tests.output",
                CovenantLane.Proposed,
                CovenantTask6Fixture.D(11)),
            expectedLaneRevision: 0,
            reactivate: false,
            expectedKeyEpoch: 0,
            new CovenantMutationArtifact(
                "explain failures",
                "- tests.output: \"explain failures\"\n",
                CovenantTask6Fixture.D(12),
                CovenantTask6Fixture.D(13),
                "- tests.output: \"explain failures\"\n".Length,
                3,
                CovenantCompiler.CompilerPolicyVersion,
                CovenantCompiler.RendererPolicyVersion),
            [],
            new CovenantMutationAuthorization(
                CovenantTask6Fixture.D(14),
                CovenantTask6Fixture.D(15),
                CovenantTask6Fixture.D(16),
                CovenantTask6Fixture.D(17),
                CovenantAuthorizationMode.None,
                null,
                null),
            Guid.NewGuid(),
            "call-1",
            plan.Digest,
            CovenantTask6Fixture.D(21));

    private IGrimoireTurnCommitter Committer() =>
        new GrimoireRepository(
            _db!,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot(new ArcanumSettings()),
            covenantKernel: null,
            availabilityRepublisher: null,
            FixtureOrdinaryConnectionFactory.For(_db!),
            FixtureLabeledArtifactGuard.For(_db!));

    private async Task<(Guid SessionId, Guid AssistantEntryId)> SeedTurnAsync()
    {
        Guid sessionId = Guid.NewGuid();

        Guid assistantEntryId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = _db!.Sessions.Add(new Session
        {
            Id = sessionId,
            CreatedAt = now,
            UpdatedAt = now,
            Status = "active",
            Title = "committer",
            UnsummarizedEntryCount = 2,
        });

        _ = _db.Entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = "hello",
            ModelUsed = "test-model",
            CreatedAt = now,
            Sequence = 1L,
        });

        _ = _db.Entries.Add(new Entry
        {
            Id = assistantEntryId,
            SessionId = sessionId,
            Role = MessageRole.Assistant,
            Content = string.Empty,
            ModelUsed = "test-model",
            CreatedAt = now,
            Sequence = 2L,
        });

        _ = await _db.SaveChangesAsync(CancellationToken.None);

        return (sessionId, assistantEntryId);
    }

    private async Task<string?> ReadContentAsync(Guid assistantEntryId) =>
        await _db!.Entries
            .AsNoTracking()
            .Where(entry => entry.Id == assistantEntryId)
            .Select(entry => entry.Content)
            .FirstOrDefaultAsync(CancellationToken.None);

    private async Task<long?> ReadGuardOutcomeAsync(Guid assistantEntryId)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT OutcomeCode
            FROM assistant_entry_finalizations
            WHERE AssistantEntryId = $assistantEntryId;
            """;

        _ = command.Parameters.AddWithValue("$assistantEntryId", assistantEntryId);

        object? value = await command.ExecuteScalarAsync(CancellationToken.None);

        return value is null or DBNull ? null : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class TestOptionsSnapshot(ArcanumSettings value) : IOptionsSnapshot<ArcanumSettings>
    {

        public ArcanumSettings Value => value;

        public ArcanumSettings Get(string? name) => value;

    }

}
