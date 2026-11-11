using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed partial class GuildLocalHostPolicyTests
{
    [Fact]
    public void Guild_defaults_off_and_configuration_binding_can_enable_it()
    {
        Assert.False(new FeatureSettings().Guild);

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Guild"] = "true" }).Build();

        Assert.True(config.Get<FeatureSettings>()!.Guild);
    }

    [Fact]
    public async Task Native_identity_is_durable_but_copied_context_ids_never_authenticate()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        GuildLocalHostPolicy restarted = f.NewPolicy();

        Result<GuildCallerContext> again = await restarted.CreateNativeOperatorAsync(Guid.NewGuid(), default);

        Assert.True(again.IsSuccess, again.Error.Message);

        Assert.Equal(f.Generation, again.Value.InstallationGeneration);

        Assert.Equal(caller.PrincipalId, again.Value.PrincipalId);

        Assert.True(f.Policy.Authenticates(caller));

        Assert.False(restarted.Authenticates(caller));

        Assert.False(f.Policy.Authenticates(new(GuildCallerKind.Operator, caller.PrincipalId!.Value, caller.InvocationId, caller.InstallationGeneration)));

        GuildCallerContext peer = new("peer", "1", Guid.NewGuid(), f.Generation);

        Assert.False(f.Policy.Authenticates(peer));

        Assert.True((await f.IssueAsync(peer)).IsFailure);
    }

    [Fact]
    public async Task Missing_installation_evidence_refuses_without_minting_a_parallel_generation()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync("DELETE FROM covenant_authority_state");

        Assert.True((await f.Policy.CreateNativeOperatorAsync(Guid.NewGuid(), default)).IsFailure);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_workspace_authority_state"));
    }

    [Fact]
    public async Task Real_root_preserves_explicit_grant_and_replay_authenticates_before_disclosure()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        Result<GuildQuestStored> issued = await f.IssueAsync(caller);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        Assert.Equal(JsonSerializer.Serialize(f.Request.Grant, GuildJsonContext.Default.GuildGrant), JsonSerializer.Serialize(issued.Value.Quest.ResolvedGrant, GuildJsonContext.Default.GuildGrant));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM guild_budget_accounts"));

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM session_campaign_bindings WHERE BindingKindCode=1 AND CampaignId IS NULL"));

        f.Settings.CurrentValue.Features.Guild = false;

        Result<GuildQuestStored> replay = await f.IssueAsync(caller);

        Assert.True(replay.IsSuccess, replay.Error.Message);

        Assert.Equal(issued.Value.Quest.Id, replay.Value.Quest.Id);

        Assert.True(replay.Value.Quest.Receipt!.Replayed);

        GuildCallerContext forged = new(GuildCallerKind.Operator, caller.PrincipalId!.Value, caller.InvocationId, f.Generation);

        Assert.True((await f.IssueAsync(forged)).IsFailure);

        Assert.True((await f.IssueAsync(caller, f.Request with { IdempotencyKey = Guid.NewGuid() })).IsFailure);
    }

    [Fact]
    public async Task Canonical_session_requires_binding_and_exact_campaign_agreement()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        Guid campaign = Guid.NewGuid();

        Guid session = Guid.NewGuid();

        await f.AddCampaignAsync(campaign);

        await f.AddSessionAsync(session, campaign, binding: false);

        Assert.True((await f.IssueAsync(caller, f.Request with { SessionId = session, CampaignId = campaign })).IsFailure);

        await f.BindSessionAsync(session, campaign);

        Result<GuildQuestStored> issued = await f.IssueAsync(caller, f.Request with { SessionId = session, CampaignId = campaign });

        Assert.True(issued.IsSuccess, issued.Error.Message);

        Assert.Equal(campaign, issued.Value.Quest.CampaignId);

        Guid other = Guid.NewGuid();

        await f.AddCampaignAsync(other);

        Assert.True((await f.IssueAsync(caller, f.Request with { IdempotencyKey = Guid.NewGuid(), SessionId = session, CampaignId = other })).IsFailure);
    }

    [Fact]
    public async Task Forged_admission_and_recovery_claims_are_refused_even_with_matching_ids()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        Result<GuildQuestStored> issued = await f.IssueAsync(caller);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        GuildAdmissionContext forged = new(caller, Guid.NewGuid(), new(Guid.NewGuid(), new string('0', 64)));

        GuildSignetBinding binding = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, f.Request.Grant, DateTimeOffset.UtcNow, caller.PrincipalId!.Value);

        Assert.True((await f.Policy.AuthorizeAdmissionAsync(forged, binding, default)).IsFailure);

        Assert.True((await f.Authority.RebindAsync(new(issued.Value.Quest.Id, issued.Value.Quest.OperationId, issued.Value.Quest.SignetRevision), default)).IsFailure);

        Result<GuildRecoveryContext> recovery = await f.Policy.CreateRecoveryAsync(caller, issued.Value.Quest.Id, issued.Value.Quest.OperationId, issued.Value.Quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        Result<GuildExecutionHandle> execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        Result<GuildCallerContext> run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, Guid.NewGuid(), default);

        Assert.True(run.IsSuccess, run.Error.Message);

        GuildExecutionHandle copied = new(execution.Value.SignetId, execution.Value.AgentId, execution.Value.RunId, execution.Value.QuestId, execution.Value.RootQuestId, execution.Value.CredentialGeneration, execution.Value.RevocationGeneration, execution.Value.IssuerSignetId);

        Assert.True((await f.Policy.CreateRunCallerAsync(f.Authority, copied, Guid.NewGuid(), default)).IsFailure);
    }

    [Fact]
    public async Task Workspace_root_resolves_canonical_campaign_and_live_physical_registration()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        Guid workspace = await f.AddWorkspaceAsync();

        GuildIssueRootRequest request = f.Request with { WorkspaceId = workspace, Grant = f.Request.Grant with { Workspaces = [new(workspace, ".", GuildWorkspaceAccess.ReadWrite)] } };

        Result<GuildQuestStored> issued = await f.IssueAsync(caller, request);

        Assert.True(issued.IsSuccess, issued.Error.Message);

        Assert.Equal(workspace, issued.Value.Quest.CampaignId);

        Assert.Equal(1L, await f.ScalarAsync("SELECT count(*) FROM session_campaign_bindings WHERE BindingKindCode=2"));

        Assert.True((await f.Policy.ResolveAsync(workspace, default)).IsSuccess);

        await f.ExecuteAsync("UPDATE campaign_path_identities SET Revision=Revision+1");

        Result<GuildRecoveryContext> recovery = await f.Policy.CreateRecoveryAsync(caller, issued.Value.Quest.Id, issued.Value.Quest.OperationId, issued.Value.Quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        Assert.True((await f.Authority.RebindAsync(recovery.Value, default)).IsFailure);

        Directory.Move(f.WorkspaceRoot, f.WorkspaceRoot + "-moved");

        Directory.CreateDirectory(f.WorkspaceRoot);

        Assert.True((await f.Policy.ResolveAsync(workspace, default)).IsFailure);
    }

    [Fact]
    public async Task Local_child_uses_canonical_campaign_and_narrows_the_explicit_parent_grant()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        Guid campaign = Guid.NewGuid();

        await f.AddCampaignAsync(campaign);

        Result<GuildQuestStored> root = await f.IssueAsync(caller, f.Request with { CampaignId = campaign });

        Assert.True(root.IsSuccess, root.Error.Message);

        Result<GuildRecoveryContext> recovery = await f.Policy.CreateRecoveryAsync(caller, root.Value.Quest.Id, root.Value.Quest.OperationId, root.Value.Quest.SignetRevision, default);

        Assert.True(recovery.IsSuccess, recovery.Error.Message);

        Result<GuildExecutionHandle> execution = await f.Authority.RebindAsync(recovery.Value, default);

        Assert.True(execution.IsSuccess, execution.Error.Message);

        Result<GuildCallerContext> run = await f.Policy.CreateRunCallerAsync(f.Authority, execution.Value, Guid.NewGuid(), default);

        Assert.True(run.IsSuccess, run.Error.Message);

        Guid key = Guid.NewGuid();

        GuildRequestedGrant narrowed = new(Budget: new(true, new(new(GuildBudgetMode.Finite, 1m), new(GuildBudgetMode.Finite, 100))));

        Result<GuildQuestStored> child = await f.Store.IssueAsync(run.Value, new(f.Generation, root.Value.Quest.Id, null, key, key), root.Value.Quest.Id,
            new(key, root.Value.Quest.Revision, null, "Child", "Compute a contribution", narrowed), default);

        Assert.True(child.IsSuccess, child.Error.Message);

        Assert.Equal(campaign, child.Value.Quest.CampaignId);

        Assert.NotEqual(root.Value.Quest.SessionId, child.Value.Quest.SessionId);

        Assert.Equal(100L, child.Value.Quest.ResolvedGrant.Budget.Tokens.Value);

        Assert.Equal(2L, await f.ScalarAsync("SELECT count(*) FROM session_campaign_bindings WHERE BindingKindCode=2"));
    }

    [Fact]
    public async Task Restore_inert_and_installation_changes_refuse_old_authority()
    {
        await using Fixture f = await Fixture.CreateAsync();

        GuildCallerContext caller = await f.OperatorAsync();

        Result<GuildQuestStored> root = await f.IssueAsync(caller);

        Assert.True(root.IsSuccess, root.Error.Message);

        await f.ExecuteAsync("UPDATE guild_signets SET RestoredInert=1,Revision=Revision+1");

        Assert.True((await f.Policy.CreateRecoveryAsync(caller, root.Value.Quest.Id, root.Value.Quest.OperationId, root.Value.Quest.SignetRevision, default)).IsFailure);

        await f.ExecuteAsync($"UPDATE covenant_authority_state SET InstallationIdentity='{Guid.NewGuid():D}'");

        Assert.True((await f.IssueAsync(caller)).IsFailure);

        Assert.True((await f.Policy.CreateNativeOperatorAsync(Guid.NewGuid(), default)).IsFailure);
    }

    [Fact]
    public async Task Unlabelled_roots_do_not_depend_on_optional_covenant_health()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync("UPDATE grimoire_feature_schemas SET HealthCode=1 WHERE FamilyCode=1");

        GuildCallerContext caller = await f.OperatorAsync();

        Result<GuildQuestStored> root = await f.IssueAsync(caller);

        Assert.True(root.IsSuccess, root.Error.Message);
    }

    [Fact]
    public async Task Failed_native_identity_commit_does_not_publish_a_rolled_back_principal()
    {
        await using Fixture f = await Fixture.CreateAsync();

        await f.ExecuteAsync("""
            CREATE TABLE fixture_identity_parent(Id INTEGER PRIMARY KEY);
            CREATE TABLE fixture_identity_child(Id INTEGER REFERENCES fixture_identity_parent(Id) DEFERRABLE INITIALLY DEFERRED);
            CREATE TRIGGER fixture_identity_commit_fault AFTER INSERT ON guild_workspace_authority_state
            BEGIN INSERT INTO fixture_identity_child VALUES(1); END;
            """);

        Result<GuildCallerContext> failed = await f.Policy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

        Assert.Equal(ErrorCodes.Guild.RevisionConflict, failed.Error.Code);

        Assert.Equal(Guid.Empty, f.Policy.OperatorPrincipalId);

        Assert.Equal(0L, await f.ScalarAsync("SELECT count(*) FROM guild_workspace_authority_state"));

        await f.ExecuteAsync("DROP TRIGGER fixture_identity_commit_fault");

        Result<GuildCallerContext> retry = await f.Policy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

        Assert.True(retry.IsSuccess, retry.Error.Message);

        Assert.True(f.Policy.Authenticates(retry.Value));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly EvolutionScratchDatabase _file;

        internal string WorkspaceRoot { get; } = Directory.CreateTempSubdirectory("guild-local-host-").FullName;

        internal Guid Generation { get; } = Guid.NewGuid();

        internal readonly GrimoireConnectionAdmissionGate Gate;

        internal readonly InboundOptionsMonitor Settings = new(new() { Features = new() { Guild = true } });

        internal readonly PhysicalCampaignRootOpener Opener = new(new KeySource());

        internal readonly FixtureOrdinaryConnectionFactory Connections;

        internal readonly GuildInboundPeerRegistry? InboundPeers;

        internal readonly GuildInboundPeerDenialJournal? DenialJournal;

        internal string DenialPath => Path.Combine(WorkspaceRoot, "peer-denials");

        internal const string PeerCredential = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        internal string PeerCredentialValue { get; set; } = PeerCredential;

        internal readonly GuildLocalHostPolicy Policy;

        internal readonly SignetAuthority Authority;

        internal readonly GuildQuestStore Store;

        internal readonly TestGuildLocalHostReadiness Readiness = new();

        internal readonly GuildIssueRootRequest Request = new(Guid.NewGuid(), null, "Local", "Compute a result", new([], [], [], new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [GuildDelegationKind.Local], null));

        private Fixture(EvolutionScratchDatabase file, bool localExecution, bool productionOptions, bool inboundPeers, bool denialJournal, bool unsafeJournal, TimeProvider? gateTimeProvider)
        {
            _file = file;

            Gate = new(gateTimeProvider ?? TimeProvider.System);

            Connections = new(file.ConnectionString);

            if (denialJournal)
            {
                if (unsafeJournal) File.WriteAllText(DenialPath, "operator bytes");

                DenialJournal = new(DenialPath, Gate);
            }

            if (inboundPeers)
            {
                Settings.CurrentValue.Integrations.A2A.InboundPeers = [new()
                {
                    PeerId = "alpha", Enabled = true, CredentialEnvironmentVariable = "INBOUND_ALPHA", CampaignId = null,
                    Grant = new() { Workspaces = [], Tools = [], Memory = [], Budget = new()
                        { Usd = new() { Mode = GuildBudgetMode.Unlimited }, Tokens = new() { Mode = GuildBudgetMode.Unlimited } },
                        Delegation = [GuildDelegationKind.Local], ExpiresAtUtc = null }
                }];

                var cache = new RetroDownfall.Arcanum.Infrastructure.Security.ApiKeyDigestCache(TimeProvider.System);

                cache.StoreDigest(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("master-test-key")), 300);

                InboundPeers = new(Settings, cache, _ => PeerCredentialValue, DenialJournal);
            }

            Policy = NewPolicy();

            Authority = new(new(Connections), Gate, Policy, TimeProvider.System);

            Store = new(new(Connections, Gate, TimeProvider.System), Authority, Policy, new GuildBudgetLedger(), TimeProvider.System,
                productionOptions ? new GuildLocalExecutionAdmission(Settings) : localExecution ? new ToolLocalAdmission(this) : null, Readiness);
        }

        internal GuildLocalExecutionMode ToolChildMode { get; set; } = GuildLocalExecutionMode.Apprentice;

        internal string[]? ToolDelegationChain { get; set; }

        private sealed class ToolLocalAdmission(Fixture fixture) : IGuildLocalExecutionAdmission
        {
            public Task<Result<GuildLocalExecutionInput>> ResolveWithinAsync(GuildCallerContext caller, GuildQuestStored quest,
                SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct) =>
                Task.FromResult(Result<GuildLocalExecutionInput>.Success(new(1, fixture.ToolChildMode, quest.Quest.GoalRef,
                    "accepted-provider", "accepted-model", [], null, fixture.ToolDelegationChain is { } chain ? [.. chain] : null)));
        }

        internal GuildLocalHostPolicy NewPolicy() => new(Connections, Gate, Settings, Opener, TimeProvider.System, InboundPeers);

        internal static async Task<Fixture> CreateAsync(bool localExecution = false, bool productionOptions = false, bool inboundPeers = false, bool denialJournal = false, bool unsafeJournal = false, TimeProvider? gateTimeProvider = null)
        {
            EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

            await using SqliteConnection connection = await file.OpenAsync(default);

            var installed = await GrimoireSchemaTestInstaller.InstallAsync(connection, 768, default);

            Assert.True(installed.GuildCanonical.IsHealthy, installed.GuildCanonical.ToString());

            Fixture f = new(file, localExecution, productionOptions, inboundPeers, denialJournal, unsafeJournal, gateTimeProvider);

            await f.ExecuteAsync($"UPDATE covenant_authority_state SET InstallationIdentity='{f.Generation:D}' WHERE StateKey=1");

            return f;
        }

        internal async Task<GuildCallerContext> OperatorAsync()
        {
            Result<GuildCallerContext> caller = await Policy.CreateNativeOperatorAsync(Guid.NewGuid(), default);

            Assert.True(caller.IsSuccess, caller.Error.Message);

            return caller.Value;
        }

        internal Task<Result<GuildQuestStored>> IssueAsync(GuildCallerContext caller, GuildIssueRootRequest? request = null)
        {
            request ??= Request;

            return Store.IssueAsync(caller, new(Generation, null, caller.InvocationId, request.IdempotencyKey, request.IdempotencyKey), request, default);
        }

        internal Task AddCampaignAsync(Guid id) => ExecuteAsync($"INSERT INTO Campaigns(Id,Name,NameLower,Path,Type,Settings,CreatedAt,UpdatedAt) VALUES('{id:D}','{id:N}','{id:N}','/unused/{id:N}',0,'{{}}','2026-11-08T00:00:00.0000000Z','2026-11-08T00:00:00.0000000Z')".ToUpperInvariant());

        internal async Task<Guid> AddWorkspaceAsync()
        {
            Guid workspace = Guid.NewGuid();

            await AddCampaignAsync(workspace);

            string workspaceId = workspace.ToString("D").ToUpperInvariant();

            await ExecuteAsync($"UPDATE Campaigns SET Path='{WorkspaceRoot.Replace("'", "''")}' WHERE Id='{workspaceId}'");

            string digest = Opener.IdentifyExact(WorkspaceRoot)!.Value.ToString();

            await ExecuteAsync($"INSERT INTO campaign_path_identities(CampaignId,PolicyVersion,Revision,DisplayPath,Depth,PhysicalIdentityDigest,UpdatedAtUtc) VALUES('{workspace.ToString("D").ToUpperInvariant()}',1,1,'{WorkspaceRoot.Replace("'", "''")}',1,X'{digest}','2026-11-08T00:00:00.0000000Z')");

            return workspace;
        }

        internal async Task AddSessionAsync(Guid id, Guid? campaign, bool binding)
        {
            await ExecuteAsync($"INSERT INTO Sessions(Id,CampaignId,CreatedAt,UpdatedAt) VALUES('{id:D}',{(campaign is null ? "NULL" : $"'{campaign:D}'")},'2026-11-08T00:00:00.0000000Z','2026-11-08T00:00:00.0000000Z')".ToUpperInvariant());

            if (binding) await BindSessionAsync(id, campaign);
        }

        internal Task BindSessionAsync(Guid id, Guid? campaign) => ExecuteAsync($"INSERT INTO session_campaign_bindings VALUES('{id:D}',{(campaign is null ? 1 : 2)},{(campaign is null ? "NULL" : $"'{campaign:D}'")},'2026-11-08T00:00:00.0000000Z')".ToUpperInvariant());

        internal async Task ExecuteAsync(string sql)
        {
            await using SqliteConnection connection = await _file.OpenAsync(default);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            using CovenantSqliteAuthorizationScope authorization = CovenantSqliteConnectionInitializer.Instance
                .Authorize(connection, CovenantSqliteAuthorizationKind.SessionBindingWrite);

            _ = await command.ExecuteNonQueryAsync();
        }

        internal Task<SqliteConnection> OpenAsync() => _file.OpenAsync(default);

        internal async Task<long> ScalarAsync(string sql)
        {
            await using SqliteConnection connection = await _file.OpenAsync(default);

            await using SqliteCommand command = connection.CreateCommand();

            command.CommandText = sql;

            return (long)(await command.ExecuteScalarAsync())!;
        }

        public ValueTask DisposeAsync()
        {
            Authority.Dispose();

            InboundPeers?.Dispose();

            DenialJournal?.Dispose();

            _file.Dispose();

            Directory.Delete(WorkspaceRoot, recursive: true);

            if (Directory.Exists(WorkspaceRoot + "-moved")) Directory.Delete(WorkspaceRoot + "-moved", recursive: true);

            return ValueTask.CompletedTask;
        }
    }

    private sealed class KeySource : ICampaignRootIdentityKeyProvider
    {
        public bool TryCopyRootIdentityKey(Span<byte> destination)
        {
            destination[..32].Fill(0x42);

            return true;
        }
    }
}
