using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildQuestStoreTests
{
    [Fact]
    public async Task Concurrent_same_key_admission_commits_one_complete_identity_and_restart_adopts_it()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Result<GuildQuestStored>[] results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error.Message));

        Assert.Single(results.Select(result => result.Value.Quest.Id).Distinct());

        foreach (string table in new[] { "guild_quests", "LongRunningOperations", "guild_signets", "guild_quest_start_intents", "fixture_allocations", "Sessions", "guild_agent_cards" })
        {
            Assert.Equal(1L, await f.CountAsync(table));
        }

        Assert.Equal(2L, await f.CountAsync("guild_content"));

        Assert.Equal(1, f.Host.Resolutions);

        f.Restart();

        f.Host.RefuseResolution = true;

        Result<GuildQuestStored> replay = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(results[0].Value.Quest.Id, replay.Value.Quest.Id);

        Assert.Equal(results[0].Value.Quest.SessionId, replay.Value.Quest.SessionId);

        Assert.True(replay.Value.Quest.Receipt!.Replayed);
    }

    [Fact]
    public async Task Changed_intent_conflicts_without_new_resolution_or_generated_owners()
    {
        await using Fixture f = await Fixture.CreateAsync();

        Assert.True((await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default)).IsSuccess);

        Result<GuildQuestStored> result = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request with { Goal = "Different work" }, default);

        Assert.Equal(ErrorCodes.Guild.IdempotencyConflict, result.Error.Code);

        Assert.Equal(1, f.Host.Resolutions);

        Assert.Equal(1L, await f.CountAsync("Sessions"));
    }

    [Fact]
    public async Task Failed_allocation_rolls_back_generated_session_card_goal_operation_and_signet()
    {
        await using Fixture f = await Fixture.CreateAsync();

        f.Budget.Refuse = true;

        Result<GuildQuestStored> result = await f.Store.IssueAsync(f.Caller, f.Identity, f.Request, default);

        Assert.Equal("fixture.allocation_refused", result.Error.Code);

        foreach (string table in new[] { "guild_quests", "LongRunningOperations", "guild_signets", "guild_content", "guild_quest_start_intents", "fixture_allocations", "Sessions", "guild_agent_cards" })
        {
            Assert.Equal(0L, await f.CountAsync(table));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly EvolutionScratchDatabase _file;

        internal readonly GrimoireConnectionAdmissionGate Gate = new(TimeProvider.System);

        internal readonly FixtureOrdinaryConnectionFactory Connections;

        internal readonly HostPolicy Host = new();

        internal readonly BudgetParticipant Budget = new();

        internal SignetAuthority Authority;

        internal GuildQuestStore Store;

        internal readonly GuildCallerContext Caller;

        internal readonly GuildIssuanceIdentity Identity;

        internal readonly GuildIssueRootRequest Request;

        private Fixture(EvolutionScratchDatabase file)
        {
            _file = file;

            Connections = new(file.ConnectionString);

            Caller = new(GuildCallerKind.Operator, Guid.NewGuid(), Guid.NewGuid(), Host.InstallationGeneration);

            Host.Caller = Caller;

            Guid key = Guid.NewGuid();

            Identity = new(Host.InstallationGeneration, null, Caller.InvocationId, Guid.NewGuid(), key);

            Request = new(key, null, "Child", "Summarize the selected material", Host.Grant);

            Authority = new(new(Connections), Gate, Host, TimeProvider.System);

            Store = NewStore();
        }

        internal static async Task<Fixture> CreateAsync()
        {
            EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            await using SqliteConnection connection = await file.OpenAsync(default);

            _ = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = """
                CREATE TABLE fixture_allocations(AccountId TEXT PRIMARY KEY,QuestId TEXT UNIQUE NOT NULL);
                CREATE TABLE fixture_exposures(DispatchId TEXT PRIMARY KEY,QuestId TEXT NOT NULL,AccountingId TEXT NOT NULL);
                CREATE TABLE fixture_settlements(QuestId TEXT PRIMARY KEY,ResultId TEXT NOT NULL);
                """;

            _ = await command.ExecuteNonQueryAsync();

            return new(file);
        }

        private IGuildQuestAllocationParticipant? _allocationParticipant;

        private IGuildLocalExecutionAdmission? _localAdmission;

        internal void UseLocalAdmission(IGuildLocalExecutionAdmission participant)
        {
            _localAdmission = participant;

            Store = NewStore();
        }

        internal void UseBudgetParticipant(IGuildQuestAllocationParticipant participant)
        {
            _allocationParticipant = participant;

            Store = NewStore();
        }

        private GuildQuestStore NewStore() => new(new(Connections, Gate, TimeProvider.System), Authority, Host,
            _allocationParticipant ?? Budget, TimeProvider.System, _localAdmission, new TestGuildLocalHostReadiness());

        internal void Restart()
        {
            Authority.Dispose();

            Authority = new(new(Connections), Gate, Host, TimeProvider.System);

            Store = NewStore();
        }

        internal Task<SqliteConnection> OpenAsync() => _file.OpenAsync(default);

        internal async Task ExecuteAsync(string sql)
        {
            await using SqliteConnection connection = await OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            _ = await command.ExecuteNonQueryAsync();
        }

        internal async Task<GuildQuestDriver> DriverAsync(GuildQuestStored row, string owner = "fixture-driver")
        {
            Result<GuildExecutionHandle> rebound = await Authority.RebindAsync(new(row.Quest.Id, row.Quest.OperationId,
                row.Quest.SignetRevision), default);

            Assert.True(rebound.IsSuccess, rebound.Error.Message);

            await using SqliteConnection connection = await OpenAsync();

            await using ArcanumDbContext db = GrimoireFixture.CreateContext(connection);

            LongRunningOperationStore ledger = new(db, Connections);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            LongRunningOperationLeaseResult leased = await ledger.TryAcquireLeaseAsync(row.Quest.OperationId, owner, now, now.AddMinutes(5));

            Assert.True(leased.Acquired);

            return new(rebound.Value, owner, leased.Operation.Revision);
        }

        internal async Task<GuildQuestStored> ChildAsync(GuildQuestStored parent, GuildQuestDriver driver)
        {
            GuildCallerContext caller = new(GuildCallerKind.Run, Caller.PrincipalId!.Value, Guid.NewGuid(), Host.InstallationGeneration, driver.Execution);

            Guid key = Guid.NewGuid();

            Result<GuildQuestStored> issued = await Store.IssueAsync(caller, new(Host.InstallationGeneration, parent.Quest.Id, null, Guid.NewGuid(), key),
                parent.Quest.Id, new(key, parent.Quest.Revision, null, "Child edge", "Compute the child contribution", new()), default);

            Assert.True(issued.IsSuccess, issued.Error.Message);

            return issued.Value;
        }

        internal async Task<long> CountAsync(string table)
        {
            await using SqliteConnection connection = await OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = $"SELECT count(*) FROM \"{table}\"";

            return (long)(await command.ExecuteScalarAsync())!;
        }

        public ValueTask DisposeAsync()
        {
            Authority.Dispose();

            _file.Dispose();

            return ValueTask.CompletedTask;
        }
    }

    private sealed class HostPolicy : IGuildQuestHostPolicy, IGuildSignetHostPolicy
    {
        internal GuildGrant Grant { get; set; } = new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [GuildDelegationKind.Local], null);

        public Guid InstallationGeneration { get; } = Guid.NewGuid();

        internal GuildCallerContext Caller = null!;

        internal bool RefuseResolution;

        internal int Resolutions;

        private readonly Dictionary<GuildAdmissionContext, GuildSignetOwnerBinding> _admissions = [];

        public Task<Result> AuthorizeLocateAsync(GuildCallerContext caller, SqliteConnection connection, SqliteTransaction transaction,
            Guid? questId, Guid? sessionId, Guid? campaignId, CancellationToken cancellationToken) =>
            Task.FromResult(ReferenceEquals(caller, Caller) || caller.Execution is not null
                ? Result.Success() : Result.Failure(new(ErrorCodes.Guild.Forbidden, "Unrecognized caller.")));

        public Task<Result<GuildQuestAdmissionPlan>> ResolveAsync(GuildCallerContext caller, GuildQuestProposal proposal,
            SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
        {
            Resolutions++;

            if (RefuseResolution)
            {
                return Task.FromResult(Result<GuildQuestAdmissionPlan>.Failure(new(ErrorCodes.Guild.RevisionConflict, "Resolution changed.")));
            }

            GuildAdmissionContext admission = new(caller, Guid.NewGuid(), proposal.Mutation);

            GuildSignetOwnerBinding owners = new(proposal.SessionId, proposal.CampaignId, proposal.WorkspaceId);

            _admissions.Add(admission, owners);

            GuildAgentCard card = new(Guid.Empty, Guid.Empty, 0, "Derived", "Host template", "1", [], ["text/plain"], ["text/plain"],
                new(false, false, false), new(GuildAgentSourceKind.Derived, null, null, true, null));

            return Task.FromResult(Result<GuildQuestAdmissionPlan>.Success(new(admission, Grant, owners,
                Caller.PrincipalId!.Value, new(card, "", []))));
        }

        public Task<Result<GuildSignetAdmissionEvidence>> AuthorizeAdmissionAsync(GuildAdmissionContext admission, GuildSignetBinding binding, CancellationToken cancellationToken) =>
            Task.FromResult(_admissions.TryGetValue(admission, out GuildSignetOwnerBinding? owners)
                ? Result<GuildSignetAdmissionEvidence>.Success(new(owners, binding.IssuerSignetId is null ? GuildSignetIssuanceEdge.Root : GuildSignetIssuanceEdge.Local))
                : Result<GuildSignetAdmissionEvidence>.Failure(new(ErrorCodes.Guild.Forbidden, "Unrecognized admission.")));

        public Task<Result<GuildSignetPolicyEvidence>> ResolveCurrentAsync(ImmutableArray<GuildSignetRecord> lineage, CancellationToken cancellationToken) =>
            Task.FromResult(Result<GuildSignetPolicyEvidence>.Success(new(Grant, new([], []))));

        public Task<Result> AuthorizeRevocationAsync(GuildCallerContext caller, GuildSignetRecord target, CancellationToken cancellationToken) =>
            Task.FromResult(ReferenceEquals(caller, Caller) ? Result.Success() : Result.Failure(new(ErrorCodes.Guild.Forbidden, "Unrecognized caller.")));

        public Task<Result> AuthorizeRecoveryAsync(GuildRecoveryContext recovery, GuildSignetRecord target, CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success());
    }

    private sealed class BudgetParticipant : IGuildQuestAllocationParticipant
    {
        internal bool Refuse;

        internal bool RefuseExposure = false;

        internal bool RefuseSettlement = false;

        internal GuildAccountingState SettlementState = GuildAccountingState.Settled;

        public async Task<Result<Guid>> AllocateAsync(SqliteConnection connection, SqliteTransaction transaction, Guid questId,
            Guid? parentAccountId, GuildGrant grant, CancellationToken cancellationToken)
        {
            Guid account = Guid.NewGuid();

            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = "INSERT INTO fixture_allocations VALUES($account,$quest)";

            command.Parameters.AddWithValue("$account", account.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$quest", questId.ToString("D").ToUpperInvariant());

            _ = await command.ExecuteNonQueryAsync(cancellationToken);

            return Refuse ? new Error("fixture.allocation_refused", "Allocation failed after its write.") : account;
        }

        public async Task<Result> ExposeAsync(SqliteConnection connection, SqliteTransaction transaction, Guid questId,
            Guid accountId, Guid dispatchId, Guid accountingAssociationId, CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = "INSERT INTO fixture_exposures VALUES($dispatch,$quest,$accounting)";

            command.Parameters.AddWithValue("$dispatch", dispatchId.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$quest", questId.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$accounting", accountingAssociationId.ToString("D").ToUpperInvariant());

            _ = await command.ExecuteNonQueryAsync(cancellationToken);

            return RefuseExposure ? Result.Failure(new("fixture.exposure_refused", "Exposure refused after its write.")) : Result.Success();
        }

        public async Task<Result<GuildAccountingState>> SettleAsync(SqliteConnection connection, SqliteTransaction transaction,
            Guid questId, Guid accountId, GuildResultReceipt receipt, CancellationToken cancellationToken)
        {
            await using SqliteCommand command = connection.CreateCommand();

            command.Transaction = transaction;

            command.CommandText = "INSERT INTO fixture_settlements VALUES($quest,$result)";

            command.Parameters.AddWithValue("$quest", questId.ToString("D").ToUpperInvariant());

            command.Parameters.AddWithValue("$result", receipt.ResultId.ToString("D").ToUpperInvariant());

            _ = await command.ExecuteNonQueryAsync(cancellationToken);

            return RefuseSettlement ? new Error("fixture.settlement_refused", "Settlement refused after its write.") : SettlementState;
        }
    }
}
