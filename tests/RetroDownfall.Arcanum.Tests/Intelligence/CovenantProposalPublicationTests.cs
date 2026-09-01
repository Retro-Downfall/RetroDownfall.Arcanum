using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// What happens to a staged proposal when the turn carrying it does not finish (§10.13).
/// </summary>
/// <remarks>
/// A proposal and the answer it accompanied share a fate: they publish in one transaction or neither
/// publishes. The two endings asserted here are the ones where "neither" is the whole guarantee — a
/// turn the provider abandoned, and a turn whose host composed no batch-aware committer to publish it
/// with. Both would otherwise leave an operator an answer whose proposal the tool had already
/// reported as recorded, which is the one outcome worse than losing the reply.
///
/// <para>Every turn here enters through <see cref="WizardIntelligenceProvider.ExecutePromptAsync"/>.
/// That is not a preference: the only production caller of the dispatch gate's admission, planning
/// and sensitivity members is a private wrapper on the provider, and a suite that reaches those
/// members directly runs the gate's half of the turn and none of the wrapper's — which is exactly how
/// a green test came to stand over a feature that could not bootstrap. Nothing below hand-builds a
/// plan, an admission, a capability, a batch, or a commit binding; the scripted client stands in for
/// the model's tool round and reaches the real in-process MCP server over the real transport, so the
/// capability the proposal runs under is minted by the real binder and taken by the real server.</para>
///
/// <para>The cross-turn rendering case this file used to open with now lives in
/// <c>CovenantBootstrapProposalTests</c>, which proves the same guarantee — a proposal one turn
/// staged is rendered to a later, independent turn — from a genuinely empty Covenant and through the
/// same provider entry point. Keeping a second copy here would have added a duplicate rather than
/// coverage.</para>
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class CovenantProposalPublicationTests : IAsyncLifetime
{

    private const string ModelName = "covenant-publication-test-model";

    private const string ProposedKey = "tests.reply.style";

    private const string ProposedContent = "answer with the failing assertion first";

    private const string OperatorPrompt = "always lead with the failing assertion";

    private const string AssistantAnswer = "Noted — I will lead with the failing assertion.";

    private static readonly Guid CampaignId = Guid.Parse("6C1F2A94-7B3D-4E58-9A02-D45E7F1C8B36");

    private readonly GrimoireFixture _fixture;

    private readonly FakeCovenantAvailability _availability = new();

    private readonly FakeCovenantAuthorityProvider _authority = new();

    private readonly FakeCovenantCampaignScopeProbe _campaigns = new();

    private readonly AcceptingJournal _journal = new();

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private SqliteConnection? _connection;

    private CovenantOperationGate? _operationGate;

    public CovenantProposalPublicationTests(GrimoireFixture fixture) =>
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
    public async Task An_interrupted_turn_publishes_neither_its_partial_answer_nor_its_staged_proposal()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync();

        Guid sessionId = await SeedUntaintedSessionAsync();

        CovenantToolCapabilityRegistry registry = new();

        await using CovenantToolCall toolCall = await CovenantToolCall.CreateAsync(registry, _availability);

        // The proposal is staged and the provider then fails, which is the ordering that matters: a
        // turn that failed before staging has nothing to lose and would satisfy every assertion below
        // without proving any of them.
        StagingChatClient chat = new(toolCall, ProposedKey, ProposedContent, AssistantAnswer, failAfterStaging: true);

        Result<PromptTurnResult> turn = await Wizard(chat, Gate(), registry, withCommitter: true)
            .ExecutePromptAsync(Ping(sessionId), Invocation(), CancellationToken.None);

        Assert.True(turn.IsFailure);

        // The tool told the model the proposal was recorded, so a batch existed for this turn to lose.
        Assert.Null(chat.ToolFailure);

        Assert.NotNull(chat.Staged);

        // Nothing the turn produced survives it. A reply persisted here would be an answer the
        // operator can read whose accompanying proposal was silently dropped.
        Assert.Null(await ReadLastAssistantContentAsync(sessionId));

        Assert.False(await ReadTaintAsync(sessionId));

        // The rendered bytes a later, independent turn is handed. A published head that rendered into
        // nothing would satisfy a head-count assertion and still be a leak.
        Assert.DoesNotContain(ProposedContent, await LaterProposedSectionAsync(), StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task A_turn_whose_batch_cannot_publish_atomically_refuses_rather_than_saving_the_answer_alone()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync();

        Guid sessionId = await SeedUntaintedSessionAsync();

        CovenantToolCapabilityRegistry registry = new();

        await using CovenantToolCall toolCall = await CovenantToolCall.CreateAsync(registry, _availability);

        StagingChatClient chat = new(toolCall, ProposedKey, ProposedContent, AssistantAnswer, failAfterStaging: false);

        // A host with no batch-aware committer composed. The ordinary reply would fall through to the
        // plain finalize path; a turn holding a proposal must not, because that path writes the answer
        // and drops the batch without telling anyone.
        Result<PromptTurnResult> turn = await Wizard(chat, Gate(), registry, withCommitter: false)
            .ExecutePromptAsync(Ping(sessionId), Invocation(), CancellationToken.None);

        Assert.True(turn.IsFailure);

        Assert.Null(chat.ToolFailure);

        Assert.NotNull(chat.Staged);

        Assert.Null(await ReadLastAssistantContentAsync(sessionId));

        Assert.DoesNotContain(ProposedContent, await LaterProposedSectionAsync(), StringComparison.Ordinal);

    }

    /// <summary>
    /// The Proposed section a second, independent turn would be shown.
    /// </summary>
    /// <remarks>
    /// A new logical turn, a new Session identity, a new plan and a new lease, so the read shares
    /// nothing with the turn that staged but the installation itself.
    /// </remarks>
    private async Task<string> LaterProposedSectionAsync()
    {

        await using CovenantTurnScope later = await Gate().BeginTurnAsync(
            Invocation(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(later.HasPlan);

        return later.PlanContent.CampaignProposed;

    }

    private static PingRequest Ping(Guid sessionId) =>
        new(
            Prompt: OperatorPrompt,
            Model: ModelName,
            WorkingDirectory: string.Empty,
            SessionId: sessionId,
            DisableMcpTools: true,
            SkipSpellRouting: true);

    private WizardIntelligenceProvider Wizard(
        StagingChatClient chat,
        CovenantDispatchGate gate,
        CovenantToolCapabilityRegistry registry,
        bool withCommitter)
    {

        ProviderSettings provider = new()
        {
            Name = "provider-covenant-publication",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1",
            Models = [ModelName],
            ContextWindowLimit = 32_768,
        };

        GrimoireRepository repository = Repository();

        return WizardIntelligenceProviderFallbackTests.CreateCovenantStagingWizard(
            new SingleLeaseChatClientFactory(chat, provider, ModelName),
            gate,
            registry,
            repository,
            withCommitter ? repository : null,
            provider);

    }

    /// <summary>
    /// The composed dispatch gate over the real store, provider, linker, and operation gate.
    /// </summary>
    /// <remarks>
    /// One operation gate for the whole test, because a second would mint its own authority and every
    /// turn built against the first would be refused as stale — the same shape as a real failure, and
    /// it would hide one.
    /// </remarks>
    private CovenantDispatchGate Gate() =>
        new(
            new CovenantContextProvider(
                _availability,
                OperationGate(),
                new CovenantStore(new FixedCovenantConnectionSource(Connection())),
                new CovenantLinker()),
            _journal,
            new ArtifactSensitivityLedger(new FixedCovenantConnectionSource(Connection())),
            _authority,
            TimeProvider.System,
            NullLogger<CovenantDispatchGate>.Instance);

    private CovenantOperationGate OperationGate()
    {

        if (_operationGate is not null)
        {

            return _operationGate;

        }

        _campaigns.Set(CampaignId, CovenantCampaignScopeState.Live);

        _operationGate = CovenantOperationGateFixture.CreateGate(_availability, _authority, _campaigns);

        return _operationGate;

    }

    /// <summary>
    /// An attended Campaign-bound turn whose read epoch matches the live authority.
    /// </summary>
    /// <remarks>
    /// Built from the authority the gate actually publishes rather than from a constant. The provider
    /// refuses a turn whose epoch does not match the lease it just took, and a hard-coded epoch would
    /// degrade every turn here to <see cref="CovenantTurnAbsence.CapabilityUnavailable"/> — a silent
    /// absence under which "no proposal was published" would read as a passing assertion.
    /// </remarks>
    private ArcanumInvocationContext Invocation()
    {

        _ = OperationGate();

        CovenantAuthoritySnapshot authority = _authority.Current!;

        return ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CovenantOperationGateFixture.CampaignContext(CampaignId),
            InvocationAttendance.Attended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            CovenantReadAuthorityEpoch.CreateForTests(
                Guid.Parse(authority.InstallationIdentity),
                authority.RuntimeAuthorityGeneration,
                authority.AuthorityEpoch)).Value;

    }

    private GrimoireRepository Repository() =>
        new(
            _db!,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            attachmentIndex: null,
            new CovenantMutationKernel(),
            FixtureOrdinaryConnectionFactory.For(_db!));

    private SqliteConnection Connection()
    {

        if (_connection is not null)
        {

            return _connection;

        }

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {

            connection.Open();

        }

        _connection = connection;

        return connection;

    }

    /// <summary>
    /// Seeds the Campaign the turn binds to, through EF rather than raw SQL.
    /// </summary>
    /// <remarks>
    /// EF's SQLite mapping stores a <see cref="Guid"/> key as uppercase <c>D</c>-format text, and the
    /// canonical store's Campaign predicates compare against that column. A hand-written lowercase
    /// row would seed a Campaign no canonical query could ever match, which is how a scan once
    /// shipped never matching a head in a real host.
    /// </remarks>
    private async Task SeedCampaignAsync()
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = _db!.Campaigns.Add(new Campaign
        {
            Id = CampaignId,
            Name = "covenant-publication",
            NameLower = "covenant-publication",
            Path = Path.Combine(Path.GetTempPath(), "covenant-publication"),
            Type = WorkspaceType.Campaign,
            CreatedAt = now,
            UpdatedAt = now,
        });

        _ = await _db.SaveChangesAsync(CancellationToken.None);

    }

    /// <summary>
    /// An empty Session with the immutable Campaign binding a live turn refuses to proceed without.
    /// </summary>
    /// <remarks>
    /// No entries and no sensitivity label. The turn writes its own user entry and assistant
    /// placeholder, which is what makes "no assistant content survived" a fact about the turn rather
    /// than about what the fixture declined to seed. The binding row's foreign key is stood down for
    /// that one statement because EF stores the Session key as uppercase text and the binding writer
    /// does not, so the reference can never hold; that mismatch is a defect of its own and not one
    /// this file can assert around.
    /// </remarks>
    private async Task<Guid> SeedUntaintedSessionAsync()
    {

        Guid sessionId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = _db!.Sessions.Add(new Session
        {
            Id = sessionId,
            CampaignId = CampaignId,
            CreatedAt = now,
            UpdatedAt = now,
            Status = "active",
            Title = "covenant proposal",
            UnsummarizedEntryCount = 0,
        });

        _ = await _db.SaveChangesAsync(CancellationToken.None);

        await SetForeignKeyEnforcementAsync(enabled: false);

        try
        {

            using CovenantSqliteAuthorizationScope authorized = CovenantSqliteConnectionInitializer.Instance.Authorize(
                Connection(),
                CovenantSqliteAuthorizationKind.SessionBindingWrite);

            await using SqliteCommand command = Connection().CreateCommand();

            command.CommandText = """
                INSERT INTO session_campaign_bindings (SessionId, BindingKindCode, CampaignId, BoundAtUtc)
                VALUES ($sessionId, $kindCode, $campaignId, $boundAtUtc);
                """;

            // Canonical, because the foreign key to "Sessions"("Id") leaves this column no spelling of
            // its own and the parent is written by the object-relational writer. CampaignId below is now
            // canonical for a different reason: that column still carries no foreign key, but its two
            // production writers no longer disagree - the core data initializer always canonicalized and
            // GrimoireRepository.InsertBindingAsync now does too - and version 5 guards it.
            _ = command.Parameters.AddWithValue("$sessionId", sessionId.ToString("D").ToUpperInvariant());

            _ = command.Parameters.AddWithValue(
                "$kindCode",
                (long)SessionCampaignBinding.ForCampaign(CampaignId).Kind);

            _ = command.Parameters.AddWithValue(
                "$campaignId",
                CampaignId.ToString("D").ToUpperInvariant());

            _ = command.Parameters.AddWithValue(
                "$boundAtUtc",
                now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);

        }
        finally
        {

            await SetForeignKeyEnforcementAsync(enabled: true);

        }

        return sessionId;

    }

    private async Task SetForeignKeyEnforcementAsync(bool enabled)
    {

        await using SqliteCommand pragma = Connection().CreateCommand();

        pragma.CommandText = enabled ? "PRAGMA foreign_keys = ON;" : "PRAGMA foreign_keys = OFF;";

        _ = await pragma.ExecuteNonQueryAsync(CancellationToken.None);

    }

    private async Task<bool> ReadTaintAsync(Guid sessionId)
    {

        Result<SessionSensitivityProjection> projection = await new ArtifactSensitivityLedger(
            new FixedCovenantConnectionSource(Connection()))
            .ReadSessionProjectionAsync(sessionId, CancellationToken.None);

        Assert.True(projection.IsSuccess, projection.IsFailure ? projection.Error.Message : null);

        return projection.Value.IsTainted;

    }

    private async Task<string?> ReadLastAssistantContentAsync(Guid sessionId) =>
        await _db!.Entries
            .AsNoTracking()
            .Where(entry => entry.SessionId == sessionId && entry.Role == MessageRole.Assistant)
            .OrderByDescending(entry => entry.Sequence)
            .Select(entry => entry.Content)
            .FirstOrDefaultAsync(CancellationToken.None);

    /// <summary>
    /// A scripted provider that spends the turn's staging capability and then either answers or dies.
    /// </summary>
    /// <remarks>
    /// It runs inside the turn's async flow, which is the whole point: the staging ambient is an
    /// <c>AsyncLocal</c> the turn loop pushes around the provider call, so a client that cannot see it
    /// here is a turn in which no tool call could have seen it either. The failure is thrown after the
    /// tool has already reported the proposal staged, because a provider that failed first would leave
    /// the collector empty and prove nothing about what happens to a batch.
    /// </remarks>
    private sealed class StagingChatClient(
        CovenantToolCall toolCall,
        string key,
        string content,
        string answer,
        bool failAfterStaging) : IChatClient
    {

        public bool SawStagingCapability { get; private set; }

        public CovenantMutationFailureResultWire? ToolFailure { get; private set; }

        public CovenantMutationStagedResultWire? Staged { get; private set; }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {

            SawStagingCapability = CovenantToolStagingAmbient.Current is not null;

            McpToolsCallResultWire result = await toolCall.ProposeAsync(key, content).ConfigureAwait(false);

            if (result.IsError)
            {

                ToolFailure = JsonSerializer.Deserialize(
                    result.StructuredContent!.Value,
                    McpJsonSerializerContext.Default.CovenantMutationFailureResultWire);

            }
            else
            {

                Staged = JsonSerializer.Deserialize(
                    result.StructuredContent!.Value,
                    McpJsonSerializerContext.Default.CovenantMutationStagedResultWire);

            }

            return failAfterStaging
                ? throw new InvalidOperationException("The provider connection dropped mid-turn.")
                : new ChatResponse(new MeAiChatMessage(ChatRole.Assistant, answer));

        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    /// <summary>
    /// One live in-process MCP server, driven over its real transport.
    /// </summary>
    /// <remarks>
    /// The binder and the server are both production. <c>ApplyAmbientBinding</c> is what mints the
    /// capability from the staging ambient and registers it under the connection and request identity;
    /// the server is what takes it back out and publishes it to the handler. Registering a capability
    /// by hand would skip precisely the handover the whole capability model exists to enforce.
    /// </remarks>
    private sealed class CovenantToolCall : IAsyncDisposable
    {

        private readonly InProcessMcpTransport _transport;

        private readonly Task _serverTask;

        private readonly CancellationTokenSource _lifetime;

        private readonly string _connectionKey;

        private int _nextId;

        private CovenantToolCall(
            InProcessMcpTransport transport,
            Task serverTask,
            CancellationTokenSource lifetime,
            string connectionKey)
        {

            _transport = transport;

            _serverTask = serverTask;

            _lifetime = lifetime;

            _connectionKey = connectionKey;

        }

        public static async Task<CovenantToolCall> CreateAsync(
            CovenantToolCapabilityRegistry registry,
            ICovenantAvailability availability)
        {

            ServiceCollection services = [];

            services.AddSingleton<ICovenantCompiler, CovenantCompiler>();

            services.AddSingleton(registry);

            services.AddSingleton(availability);

            services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(
                new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

            ServiceProvider provider = services.BuildServiceProvider();

            IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            (InProcessMcpTransport transport, ArcanumInternalToolServer server) = InProcessMcpTransport.CreatePair(
                new HumanPromptRegistry(),
                scopeFactory,
                new UnseenServantPacer(
                    new SilentEventBus(),
                    new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
                    scopeFactory,
                    NullLogger<UnseenServantPacer>.Instance),
                workspaceRootNormalizedOrNull: null,
                listDirectoryMaxPaths: 64,
                intelligenceSettings: ArcanumRuntimeDefaults.Intelligence with
                {
                    EnableLexiconSystem = false,
                    EnableArchiveSearch = false,
                },
                maxFileReadSizeBytes: 1024 * 1024,
                conclaveEnabled: false,
                sagaEnabled: false,
                a2aClientEnabled: false,
                attachmentsToolEnabled: false,
                maxJsonRpcLineBytes: 2_097_152,
                logger: NullLogger<ArcanumInternalToolServer>.Instance);

            CancellationTokenSource lifetime = new();

            Task serverTask = server.RunAsync(lifetime.Token);

            await transport.StartAsync();

            return new CovenantToolCall(transport, serverTask, lifetime, server.AmbientConnectionKey);

        }

        public async Task<McpToolsCallResultWire> ProposeAsync(string key, string content)
        {

            int id = Interlocked.Increment(ref _nextId);

            JsonElement arguments = JsonSerializer.SerializeToElement(
                new ProposeCovenantParams(key, content),
                McpJsonSerializerContext.Default.ProposeCovenantParams);

            JsonRpcRequest request = new()
            {
                Method = "tools/call",
                Params = JsonSerializer.SerializeToElement(
                    new McpToolsCallParams
                    {
                        Name = CovenantToolNames.ProposeCovenant,
                        Arguments = arguments,
                    },
                    McpJsonSerializerContext.Default.McpToolsCallParams),
                Id = JsonSerializer.SerializeToElement(id, McpJsonSerializerContext.Default.Int32),
            };

            // The production binding site. It reads the staging ambient this turn published and mints
            // the single-use capability from it, or mints nothing at all.
            JsonRpcRequest bound = SessionAttachmentAmbientSend.ApplyAmbientBinding(_connectionKey, request);

            await _transport.WriteRequestAsync(bound).ConfigureAwait(false);

            McpInboundEnvelope envelope = await _transport.InboundReader.ReadAsync().ConfigureAwait(false);

            Assert.Equal(McpInboundKind.Response, envelope.Kind);

            return JsonSerializer.Deserialize(
                envelope.Response!.Result!.Value,
                McpJsonSerializerContext.Default.McpToolsCallResultWire)!;

        }

        public async ValueTask DisposeAsync()
        {

            await _lifetime.CancelAsync();

            try
            {

                await _serverTask.ConfigureAwait(false);

            }
            catch (OperationCanceledException)
            {
            }

            await _transport.DisposeAsync().ConfigureAwait(false);

            _lifetime.Dispose();

        }

    }

    private sealed class SingleLeaseChatClientFactory(
        IChatClient client,
        ProviderSettings provider,
        string model) : IChatClientFactory
    {

        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatClientLease(client, provider, model, ownedHttpClient: null));

        public Task<ChatClientLease> ResolveClientAsync(
            ProviderSettings candidate,
            string resolvedModel,
            CancellationToken cancellationToken) =>
            ResolveClientAsync(resolvedModel, cancellationToken);

    }

    private sealed class AcceptingJournal : ICovenantDisclosureJournal
    {

        private ulong _sequence;

        public ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
            CovenantDisclosureDraft draft,
            CovenantDisclosureEffectCategory category,
            ProviderCallSensitivity sensitivity,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CovenantDisclosureReceipt>.Success(
                new CovenantDisclosureReceipt(draft, ++_sequence)));

    }

    private sealed class SilentEventBus : IEventBus
    {

        public void Publish<T>(T @event) where T : notnull
        {
        }

        public async IAsyncEnumerable<T> Subscribe<T>(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
            where T : notnull
        {

            await Task.CompletedTask;

            yield break;

        }

    }

}
