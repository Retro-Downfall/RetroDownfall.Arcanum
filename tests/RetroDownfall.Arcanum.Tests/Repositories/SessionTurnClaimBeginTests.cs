using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

[Collection("Grimoire")]
public sealed class SessionTurnClaimBeginTests(GrimoireFixture fixture) : IAsyncLifetime
{
    private string _path = string.Empty;

    private ArcanumDbContext _db = null!;

    private GrimoireRepository _repository = null!;

    private SqliteConnection Connection => (SqliteConnection)_db.Database.GetDbConnection();

    public Task InitializeAsync()
    {
        _path = fixture.CopyDatabase();

        _db = fixture.CreateContext(_path);

        _repository = new GrimoireRepository(_db, new NoOpSessionAttachmentStore(), NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()), null, null,
            FixtureOrdinaryConnectionFactory.For(_db), FixtureLabeledArtifactGuard.For(_db));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();

        File.Delete(_path);
    }

    [SkippableFact]
    public async Task Claim_input_is_a_content_free_sequence_and_sensitivity_snapshot()
    {
        RequireSqlCipher();

        Guid session = await CreateSessionAsync();

        await ExecuteAsync($"INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('{Guid.NewGuid().ToString("D").ToUpperInvariant()}','{session.ToString("D").ToUpperInvariant()}',1,'private payload','','2026-10-01T00:00:00.0000000+00:00',7);");

        Result<SessionTurnClaimInputSnapshot> read = await _repository.ReadClaimInputAsync(session, CancellationToken.None);

        Assert.True(read.IsSuccess, read.Error.Message);

        Assert.Equal(7, read.Value.HistoryRevision);

        Assert.Equal(DateTimeOffset.Parse("2026-10-01T00:00:00Z"), read.Value.HistoryWatermarkUtc);

        Assert.Equal(0, read.Value.SensitivityRevision);
    }

    [SkippableFact]
    public async Task A_live_claim_consumes_its_reserved_identity_and_creates_both_entries_atomically()
    {
        RequireSqlCipher();

        SessionTurnClaimLease lease = await AcquireAsync();

        Result<AssistantReplyBeginReceipt> begin = await BeginAsync(lease);

        Assert.True(begin.IsSuccess, begin.Error.Message);

        Assert.Equal(lease.FutureAssistantEntryId, begin.Value.AssistantEntryId);

        Assert.Equal(2, await ScalarAsync("SELECT COUNT(*) FROM Entries;"));

        Assert.Equal(2, await ScalarAsync("SELECT StateCode FROM session_turn_claims;"));

        Assert.Equal(2, await ScalarAsync("SELECT StateCode FROM assistant_finalization_capacity_reservations;"));

        Assert.Equal(0, await ScalarAsync("SELECT ReservedFinalizationCount FROM installation_turn_quota_state;"));

        Assert.Equal(1, await ScalarAsync("SELECT ConsumedFinalizationCount FROM installation_turn_quota_state;"));

        await using SqliteCommand read = Connection.CreateCommand();

        read.CommandText = "SELECT UserEntryId,AssistantEntryId FROM session_turn_claims;";

        await using SqliteDataReader row = await read.ExecuteReaderAsync();

        Assert.True(await row.ReadAsync());

        Assert.Equal(begin.Value.UserEntryId, Guid.Parse(row.GetString(0)));

        Assert.Equal(begin.Value.AssistantEntryId, Guid.Parse(row.GetString(1)));
    }

    [SkippableTheory]
    [InlineData("LeaseDeadlineUtc = '2000-01-01T00:00:00.0000000+00:00'")]
    [InlineData("ExecutorId = 'C40B9B5E-F510-4690-968D-99997635EF58'")]
    [InlineData("OwnerBootId = 'C40B9B5E-F510-4690-968D-99997635EF58'")]
    public async Task A_stale_claim_writes_no_entries_and_does_not_consume_capacity(string mutation)
    {
        RequireSqlCipher();

        SessionTurnClaimLease lease = await AcquireAsync();

        await ExecuteAsync("UPDATE session_turn_claims SET " + mutation + ";");

        Result<AssistantReplyBeginReceipt> begin = await BeginAsync(lease);

        Assert.True(begin.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, begin.Error.Code);

        await AssertUnbegunAsync();
    }

    [SkippableFact]
    public async Task Changed_history_after_acquisition_writes_no_placeholder()
    {
        RequireSqlCipher();

        SessionTurnClaimLease lease = await AcquireAsync();

        await ExecuteAsync($"INSERT INTO Entries(Id,SessionId,Role,Content,ModelUsed,CreatedAt,Sequence) VALUES('{Guid.NewGuid().ToString("D").ToUpperInvariant()}','{lease.Claim.SessionId.ToString("D").ToUpperInvariant()}',1,'intervening','','2026-10-01T00:00:00.0000000+00:00',1);");

        Result<AssistantReplyBeginReceipt> begin = await BeginAsync(lease);

        Assert.True(begin.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, begin.Error.Code);

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM Entries;"));

        Assert.Equal(1, await ScalarAsync("SELECT StateCode FROM assistant_finalization_capacity_reservations;"));
    }

    [SkippableFact]
    public async Task Begin_checks_the_persisted_expected_sensitivity_revision_after_maintenance()
    {
        RequireSqlCipher();

        SessionTurnClaimLease lease = await AcquireAsync();

        await SetSensitivityAsync(lease.Claim.SessionId, 2);

        await ExecuteAsync("UPDATE session_turn_claims SET ExpectedCurrentSensitivityRevision = 2;");

        Result<AssistantReplyBeginReceipt> begin = await BeginAsync(lease);

        Assert.True(begin.IsSuccess, begin.Error.Message);

        Assert.Equal(2, await ScalarAsync("SELECT COUNT(*) FROM Entries;"));
    }

    [SkippableFact]
    public async Task An_unacknowledged_sensitivity_change_writes_no_entries()
    {
        RequireSqlCipher();

        SessionTurnClaimLease lease = await AcquireAsync();

        await SetSensitivityAsync(lease.Claim.SessionId, 1);

        Result<AssistantReplyBeginReceipt> begin = await BeginAsync(lease);

        Assert.True(begin.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, begin.Error.Code);

        await AssertUnbegunAsync();
    }

    [SkippableFact]
    public async Task A_forged_frozen_request_cannot_consume_the_real_claim()
    {
        RequireSqlCipher();

        SessionTurnClaimLease lease = await AcquireAsync();

        Result<AssistantReplyBeginReceipt> begin = await BeginAsync(lease with { Claim = lease.Claim with { RequestDigest = Digest(9) } });

        Assert.True(begin.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, begin.Error.Code);

        await AssertUnbegunAsync();
    }

    [SkippableFact]
    public async Task Failure_recording_begun_rolls_back_entries_and_capacity_consumption()
    {
        RequireSqlCipher();

        SessionTurnClaimLease lease = await AcquireAsync();

        await ExecuteAsync("CREATE TRIGGER fail_claim_begin BEFORE UPDATE ON session_turn_claims WHEN NEW.StateCode=2 BEGIN SELECT RAISE(ABORT,'test begin write failure'); END;");

        Result<AssistantReplyBeginReceipt> begin = await BeginAsync(lease);

        Assert.True(begin.IsFailure);

        await AssertUnbegunAsync();
    }

    [SkippableFact]
    public async Task Claimed_begin_and_commit_share_one_consumed_public_reservation()
    {
        RequireSqlCipher();

        SessionTurnClaimLease lease = await AcquireAsync();

        Result<AssistantReplyBeginReceipt> begin = await BeginAsync(lease);

        Assert.True(begin.IsSuccess, begin.Error.Message);

        Result<TurnCommitReceipt> committed = await _repository.CommitTurnAsync(new TurnCommitRequest(
            begin.Value.AssistantEntryId, begin.Value.SessionId, AssistantFinalizationOutcome.Committed,
            "the answer", lease.Claim.RequestDigest, ContentSensitivity.None, GenerationProvenance.CreateExact([])), CancellationToken.None);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM assistant_finalization_capacity_reservations;"));

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM assistant_entry_finalizations;"));

        Assert.Equal(1, await ScalarAsync("SELECT OriginCode FROM assistant_finalization_capacity_reservations;"));

        Assert.Equal(1, await ScalarAsync("SELECT ConsumedFinalizationCount FROM installation_turn_quota_state;"));
    }

    [SkippableTheory]
    [InlineData("the original answer")]
    [InlineData("")]
    public async Task A_clean_committed_claim_replays_its_authoritative_reply_including_a_valid_empty_answer(string content)
    {
        RequireSqlCipher();

        SessionTurnClaimLease terminal = await CommitClaimAsync(content);

        Result<GrimoireEntryDto> replay = await _repository.ReadCleanCommittedReplyAsync(terminal, CancellationToken.None);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(terminal.FutureAssistantEntryId, replay.Value.Id);

        Assert.Equal(content, replay.Value.Content);

        Assert.Equal("test-model", replay.Value.ModelUsed);
    }

    [SkippableFact]
    public async Task A_retained_partial_reply_replays_its_interruption_after_a_new_boot_without_a_second_turn()
    {
        RequireSqlCipher();

        SessionTurnClaimLease live = await AcquireAsync();

        SessionTurnClaimStore claims = new(new FixedCovenantConnectionSource(Connection), new CovenantQuotaGuard(), live.OwnerBootId!.Value);

        using GrimoireTurnWriter writer = CreateClaimedWriter(claims);

        Result<GrimoireTurnWriter.TurnHandle> begun = await writer.BeginStreamedAssistantReplyAsync(
            new("hello", SessionId: live.Claim.SessionId), InvocationContexts.AttendedGlobalOnlySession(), "hello", "test-model",
            CancellationToken.None, live);

        Assert.True(begun.IsSuccess, begun.Error.Message);

        GrimoireTurnWriter.TurnHandle handle = begun.Value;

        Assert.True(await writer.ResolveInterruptedAndMarkFinalizedAsync(handle, "paid partial answer", CancellationToken.None));

        Assert.Equal("paid partial answer", await TextAsync("SELECT Content FROM Entries WHERE Role = 2;"));

        Assert.Equal(1, await ScalarAsync("SELECT OutcomeCode FROM assistant_entry_finalizations;"));

        SessionTurnClaimStore restarted = new(new FixedCovenantConnectionSource(Connection), new CovenantQuotaGuard(), Guid.NewGuid());

        Result<SessionTurnClaimLease> retry = await restarted.AcquireAsync(Identity(live.Claim), CancellationToken.None);

        Assert.True(retry.IsSuccess, retry.Error.Message);

        Assert.Equal(SessionTurnClaimDisposition.Replayed, retry.Value.Disposition);

        Assert.Equal(SessionTurnClaimState.RestoredInterrupted, retry.Value.Claim.State);

        Assert.Equal(ErrorCodes.Hub.SessionTurnRestoredInterrupted, retry.Value.Claim.Outcome!.TerminalErrorCode);

        Assert.Null(retry.Value.ExecutorId);

        Assert.Equal(live.FutureAssistantEntryId, retry.Value.FutureAssistantEntryId);

        Assert.Equal(2, await ScalarAsync("SELECT COUNT(*) FROM Entries;"));

        Assert.Equal(1, await ScalarAsync("SELECT COUNT(*) FROM session_turn_claims;"));

        Assert.Equal(1, await ScalarAsync("SELECT ConsumedFinalizationCount FROM installation_turn_quota_state;"));
    }

    [SkippableFact]
    public async Task Failure_recording_the_interrupted_claim_rolls_back_partial_content_and_finalization_together()
    {
        RequireSqlCipher();

        SessionTurnClaimLease live = await AcquireAsync();

        SessionTurnClaimStore claims = new(new FixedCovenantConnectionSource(Connection), new CovenantQuotaGuard(), live.OwnerBootId!.Value);

        using GrimoireTurnWriter writer = CreateClaimedWriter(claims);

        Result<GrimoireTurnWriter.TurnHandle> begun = await writer.BeginStreamedAssistantReplyAsync(
            new("hello", SessionId: live.Claim.SessionId), InvocationContexts.AttendedGlobalOnlySession(), "hello", "test-model",
            CancellationToken.None, live);

        Assert.True(begun.IsSuccess, begun.Error.Message);

        GrimoireTurnWriter.TurnHandle handle = begun.Value;

        await ExecuteAsync("CREATE TRIGGER fail_claim_interruption BEFORE UPDATE ON session_turn_claims WHEN NEW.StateCode=6 BEGIN SELECT RAISE(ABORT,'test interruption write failure'); END;");

        Assert.False(await writer.ResolveInterruptedAndMarkFinalizedAsync(handle, "paid partial answer", CancellationToken.None));

        Assert.Equal(string.Empty, await TextAsync("SELECT Content FROM Entries WHERE Role = 2;"));

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM assistant_entry_finalizations;"));

        Assert.Equal(2, await ScalarAsync("SELECT StateCode FROM session_turn_claims;"));

        Assert.False(handle.IsFinalized);
    }

    private GrimoireTurnWriter CreateClaimedWriter(SessionTurnClaimStore claims) =>
        new(_repository, _repository, new SessionEventHub(NullLogger<SessionEventHub>.Instance), NullLogger<GrimoireTurnWriter>.Instance,
            turnCommitter: _repository, claimedBeginStore: _repository, claims: claims);

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_partial_reply_cannot_publish_from_a_forged_or_adopted_away_claim(bool adopted)
    {
        RequireSqlCipher();

        SessionTurnClaimLease live = await AcquireAsync();

        SessionTurnClaimStore claims = new(new FixedCovenantConnectionSource(Connection), new CovenantQuotaGuard(), live.OwnerBootId!.Value);

        using GrimoireTurnWriter writer = CreateClaimedWriter(claims);

        Result<GrimoireTurnWriter.TurnHandle> begun = await writer.BeginStreamedAssistantReplyAsync(
            new("hello", SessionId: live.Claim.SessionId), InvocationContexts.AttendedGlobalOnlySession(), "hello", "test-model",
            CancellationToken.None, live);

        Assert.True(begun.IsSuccess, begun.Error.Message);

        GrimoireTurnWriter.TurnHandle handle = begun.Value;

        if (adopted)
        {
            SessionTurnClaimStore restarted = new(new FixedCovenantConnectionSource(Connection), new CovenantQuotaGuard(), Guid.NewGuid());

            Result<SessionTurnClaimLease> takeover = await restarted.AcquireAsync(Identity(live.Claim), CancellationToken.None);

            Assert.True(takeover.IsSuccess, takeover.Error.Message);

            Assert.Equal(SessionTurnClaimDisposition.Adopted, takeover.Value.Disposition);
        }
        else
        {
            handle.ClaimLease = live with { Claim = live.Claim with { DependencyDigest = Digest(9) } };
        }

        Assert.False(await writer.ResolveInterruptedAndMarkFinalizedAsync(handle, "untrusted partial answer", CancellationToken.None));

        Assert.Equal(string.Empty, await TextAsync("SELECT Content FROM Entries WHERE Role = 2;"));

        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM assistant_entry_finalizations;"));

        Assert.Equal(2, await ScalarAsync("SELECT StateCode FROM session_turn_claims;"));
    }

    private static SessionTurnRequestIdentity Identity(SessionTurnClaim claim) =>
        new(claim.OriginInstallationId, claim.OriginRestoreEpoch, claim.ClientTurnId, claim.SessionId, claim.Surface,
            claim.RequestDigest, claim.DependencyDigest, claim.PreRequestHistoryWatermarkUtc,
            claim.PreRequestHistoryRevision, claim.InputSensitivityRevision);

    [SkippableTheory]
    [InlineData(false, "ArtifactId")]
    [InlineData(true, "ArtifactId")]
    [InlineData(true, "lower(ArtifactId)")]
    [InlineData(true, "lower(replace(ArtifactId,'-',''))")]
    public async Task A_committed_reply_payload_is_read_only_after_same_snapshot_clean_proof(bool protectedReply, string spelling)
    {
        RequireSqlCipher();

        SessionTurnClaimLease terminal = await CommitClaimAsync("protected original answer");

        if (protectedReply)
        {
            Result<LabeledArtifactWriteReceipt> labeled = await new ArtifactSensitivityLedger(new FixedCovenantConnectionSource(Connection)).LabelAsync(
            new DerivedArtifactWrite(SensitiveArtifactKind.AssistantEntry, terminal.FutureAssistantEntryId,
                terminal.Claim.SessionId, null, null, 0, DerivedArtifactContentDigest.ForText("protected original answer"),
                ContentSensitivity.CovenantDerived, GenerationProvenance.CreateExact([Guid.NewGuid()])), CancellationToken.None);

            Assert.True(labeled.IsSuccess, labeled.Error.Message);

            await ExecuteAsync("DROP TRIGGER artifact_sensitivity_guard_update; UPDATE artifact_sensitivity SET ArtifactId = " + spelling + ";");
        }

        int bodyReads = 0;

        Connection.CreateFunction("replay_content_probe", (string content) => { bodyReads++; return content; });

        await ExecuteAsync("""
            ALTER TABLE Entries RENAME TO replay_entries_data;
            CREATE VIEW Entries AS SELECT Id,SessionId,Role,replay_content_probe(Content) AS Content,
                ModelUsed,CreatedAt,Sequence,ToolCallId,ToolName,ToolArguments,IsPinned FROM replay_entries_data;
            """);

        Result<GrimoireEntryDto> replay = await _repository.ReadCleanCommittedReplyAsync(terminal, CancellationToken.None);

        if (protectedReply)
        {
            Assert.True(replay.IsFailure);

            Assert.Contains(replay.Error.Code, new[] { ErrorCodes.Covenant.OperatorAuthorityUnavailable, ErrorCodes.Covenant.Unavailable });

            Assert.Equal(0, bodyReads);
        }
        else
        {
            Assert.True(replay.IsSuccess, replay.Error.Message);

            Assert.Equal("protected original answer", replay.Value.Content);

            Assert.True(bodyReads > 0);
        }
    }

    [SkippableFact]
    public async Task A_forged_terminal_request_cannot_read_the_real_reply()
    {
        RequireSqlCipher();

        SessionTurnClaimLease terminal = await CommitClaimAsync("original answer");

        Result<GrimoireEntryDto> replay = await _repository.ReadCleanCommittedReplyAsync(
            terminal with { Claim = terminal.Claim with { RequestDigest = Digest(9) } }, CancellationToken.None);

        Assert.True(replay.IsFailure);
    }

    private async Task<SessionTurnClaimLease> CommitClaimAsync(string content)
    {
        SessionTurnClaimLease live = await AcquireAsync();

        AssistantReplyBeginReceipt begin = (await BeginAsync(live)).Value;

        Result<TurnCommitReceipt> committed = await _repository.CommitTurnAsync(new TurnCommitRequest(
            begin.AssistantEntryId, begin.SessionId, AssistantFinalizationOutcome.Committed,
            content, live.Claim.RequestDigest, ContentSensitivity.None, GenerationProvenance.CreateExact([])), CancellationToken.None);

        Assert.True(committed.IsSuccess, committed.Error.Message);

        SessionTurnClaimStore claims = new(new FixedCovenantConnectionSource(Connection), new CovenantQuotaGuard(), live.OwnerBootId!.Value);

        Result<SessionTurnClaim> completed = await claims.CompleteAsync(live, SessionTurnClaimOutcome.Committed(), CancellationToken.None);

        Assert.True(completed.IsSuccess, completed.Error.Message);

        return new SessionTurnClaimLease(completed.Value, SessionTurnClaimDisposition.Replayed,
            live.FutureAssistantEntryId, null, null, null);
    }

    private async Task AssertUnbegunAsync()
    {
        Assert.Equal(0, await ScalarAsync("SELECT COUNT(*) FROM Entries;"));

        Assert.Equal(1, await ScalarAsync("SELECT StateCode FROM session_turn_claims;"));

        Assert.Equal(1, await ScalarAsync("SELECT StateCode FROM assistant_finalization_capacity_reservations;"));

        Assert.Equal(1, await ScalarAsync("SELECT ReservedFinalizationCount FROM installation_turn_quota_state;"));

        Assert.Equal(0, await ScalarAsync("SELECT ConsumedFinalizationCount FROM installation_turn_quota_state;"));
    }

    private async Task<Guid> CreateSessionAsync()
    {
        Result<Guid> created = await _repository.CreateBoundSessionAsync(CanonicalCampaignContext.GlobalOnly, "claimed turn", CancellationToken.None);

        Assert.True(created.IsSuccess, created.Error.Message);

        return created.Value;
    }

    private async Task<SessionTurnClaimLease> AcquireAsync()
    {
        Guid session = await CreateSessionAsync();

        await ExecuteAsync($"INSERT OR IGNORE INTO session_turn_quota_state(SessionId,ClaimCount,ReservedFinalizationCount,ConsumedFinalizationCount) VALUES('{session.ToString("D").ToUpperInvariant()}',0,0,0);");

        SessionTurnClaimStore claims = new(new FixedCovenantConnectionSource(Connection), new CovenantQuotaGuard(), Guid.NewGuid());

        Result<SessionTurnClaimLease> acquired = await claims.AcquireAsync(new SessionTurnRequestIdentity(
            Guid.NewGuid(), 0, Guid.NewGuid(), session, SessionTurnSurface.Intelligence, Digest(1), Digest(2), null, 0, 0), CancellationToken.None);

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        return acquired.Value;
    }

    private ValueTask<Result<AssistantReplyBeginReceipt>> BeginAsync(SessionTurnClaimLease lease) =>
        _repository.BeginClaimedAssistantReplyAsync(lease, CanonicalCampaignContext.GlobalOnly, "hello", "test-model", CancellationToken.None);

    private async Task SetSensitivityAsync(Guid session, long revision)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = "INSERT INTO session_sensitivity_state(SessionId,TaintedArtifactCount,MaximumSensitivityCode,GenerationProvenanceDigest,Revision,UpdatedAtUtc) VALUES($session,0,0,$digest,$revision,'2026-10-01T00:00:00.0000000+00:00');";

        _ = command.Parameters.AddWithValue("$session", session.ToString("D").ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$digest", new byte[32]);

        _ = command.Parameters.AddWithValue("$revision", revision);

        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<string?> TextAsync(string sql)
    {
        await using SqliteCommand command = Connection.CreateCommand();

        command.CommandText = sql;

        return await command.ExecuteScalarAsync() as string;
    }

    private static CovenantDigest Digest(byte seed) => new(Enumerable.Repeat(seed, 32).ToArray());

    private static void RequireSqlCipher() => Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);
}
