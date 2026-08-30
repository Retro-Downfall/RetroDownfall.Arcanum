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
/// The first proposal an agent ever authors, on an installation whose Covenant is still empty.
/// </summary>
/// <remarks>
/// This is the case the feature exists to serve and the one no fixture had ever built. Every other
/// test of the staging path starts from a Covenant that already holds an entry or a Session already
/// labelled Covenant-derived, and reaches the dispatch gate directly. Production does neither: it
/// enters through <c>ExecutePromptAsync</c>, and the wrapper between that entry point and the gate
/// carried an early return that withheld the admission receipt for exactly this turn — leaving
/// <c>propose_covenant</c> advertised on every healthy installation and refused on every call,
/// permanently, because the only thing that could have lifted the refusal was a proposal.
///
/// <para>So the turn here is driven the whole way. The store is empty, the Session carries no label,
/// and nothing about the precondition is seeded: the plan, the collector, the head probe, the
/// admission receipt, and the staging ambient are all minted by the production turn loop or not at
/// all. The scripted client stands in for the model's tool round and reaches the real in-process MCP
/// server over the real transport, so the capability is minted by the real binder from the real
/// ambient and taken back out by the real server. What the client does not reproduce is the
/// tool-choice round trip that decides to call the tool, which is not what is under test.</para>
///
/// <para>The negative case is here for the same reason the positive one is: relaxing the wrapper
/// could have handed every turn a staging capability, and a test that only proves the door opens
/// cannot tell that outcome from the intended one.</para>
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class CovenantBootstrapProposalTests : IAsyncLifetime
{

    private const string ModelName = "covenant-bootstrap-test-model";

    private const string ProposedKey = "tests.reply.style";

    private const string ProposedContent = "answer with the failing assertion first";

    private static readonly Guid CampaignId = Guid.Parse("1D8A5C07-3E64-4B29-8F51-A0C7E6B24D93");

    private readonly GrimoireFixture _fixture;

    private readonly FakeCovenantAvailability _availability = new();

    private readonly FakeCovenantAuthorityProvider _authority = new();

    private readonly FakeCovenantCampaignScopeProbe _campaigns = new();

    private readonly RecordingDisclosureJournal _journal = new();

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private SqliteConnection? _connection;

    private CovenantOperationGate? _operationGate;

    public CovenantBootstrapProposalTests(GrimoireFixture fixture) =>
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
    public async Task An_agent_authors_the_first_proposal_on_an_empty_covenant_and_a_later_turn_reads_it()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync();

        Guid sessionId = await SeedUntaintedSessionAsync();

        // Nothing in this Session has ever been labelled, so the gate reads the taint production
        // reads and gets the answer every fresh installation gets. Asserting it here is what makes
        // the rest of the test a bootstrap rather than a second proposal wearing one's clothes.
        Assert.False(await ReadTaintAsync(sessionId));

        CovenantDispatchGate gate = Gate();

        CovenantToolCapabilityRegistry registry = new();

        await using CovenantToolCall toolCall = await CovenantToolCall.CreateAsync(registry, _availability);

        StagingChatClient chat = new(toolCall, ProposedKey, ProposedContent);

        Result<PromptTurnResult> turn = await Wizard(chat, gate, registry).ExecutePromptAsync(
            new PingRequest(
                Prompt: "always lead with the failing assertion",
                Model: ModelName,
                WorkingDirectory: string.Empty,
                SessionId: sessionId,
                DisableMcpTools: true,
                SkipSpellRouting: true),
            Invocation(),
            CancellationToken.None);

        Assert.True(turn.IsSuccess, turn.IsFailure ? $"{turn.Error.Code}: {turn.Error.Message}" : null);

        // The capability existed while the provider call was in flight. Without it the tool below
        // would have refused with Covenant.IneligibleTurn, which is what every live call did.
        Assert.True(chat.SawStagingMaterial);

        Assert.True(chat.SawRegisteredCapability);

        Assert.Null(chat.ToolFailure);

        Assert.NotNull(chat.Staged);

        Assert.Equal("Noted — I will lead with the failing assertion.", await ReadLastAssistantContentAsync(sessionId));

        // The turn carried no Covenant bytes to the provider, so it owes no disclosure and the reply
        // it produced is not protected. A fix that reached the committer by borrowing the
        // Covenant-derived label would taint this Session and every turn after it.
        Assert.Equal(0, _journal.Count);

        Assert.False(await ReadTaintAsync(sessionId));

        // The rendered bytes a second, independent turn is handed. A published head that rendered
        // into nothing would satisfy every intermediate assertion and still show the model nothing.
        await using CovenantTurnScope later = await gate.BeginTurnAsync(
            Invocation(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(later.HasPlan);

        Assert.Contains(ProposedKey, later.PlanContent.CampaignProposed, StringComparison.Ordinal);

        Assert.Contains(ProposedContent, later.PlanContent.CampaignProposed, StringComparison.Ordinal);

        // The lane matters as much as the content. Proposed content arriving in a Confirmed section
        // would be an agent writing the operator's own instructions.
        Assert.DoesNotContain(ProposedContent, later.PlanContent.CampaignConfirmed, StringComparison.Ordinal);

        Assert.DoesNotContain(ProposedContent, later.PlanContent.GlobalConfirmed, StringComparison.Ordinal);

        Assert.Equal(
            CovenantLane.Proposed,
            Assert.Single(later.Plan!.CampaignProposedSection.Candidates).Candidate.Lane);

    }

    [SkippableFact]
    public async Task A_turn_that_may_not_stage_still_receives_no_capability_on_an_empty_covenant()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync();

        Guid sessionId = await SeedUntaintedSessionAsync();

        CovenantToolCapabilityRegistry registry = new();

        await using CovenantToolCall toolCall = await CovenantToolCall.CreateAsync(registry, _availability);

        StagingChatClient chat = new(toolCall, ProposedKey, ProposedContent);

        // Unattended is the one clause of the staging predicate this turn fails; the operator
        // surface, the Campaign binding, the default context policy and the read authority epoch all
        // still hold, so it remains eligible to read the Covenant. Relaxing the wrapper's early
        // return could have handed a staging capability to every turn that reaches it, and a test
        // that only proves the door opens cannot tell that outcome from the intended one.
        Result<PromptTurnResult> turn = await Wizard(chat, Gate(), registry).ExecutePromptAsync(
            new PingRequest(
                Prompt: "always lead with the failing assertion",
                Model: ModelName,
                WorkingDirectory: string.Empty,
                SessionId: sessionId,
                DisableMcpTools: true,
                SkipSpellRouting: true),
            Invocation(InvocationAttendance.Unattended),
            CancellationToken.None);

        Assert.True(turn.IsSuccess, turn.IsFailure ? $"{turn.Error.Code}: {turn.Error.Message}" : null);

        Assert.True(chat.SawStagingMaterial);

        Assert.False(chat.SawRegisteredCapability);

        Assert.Equal(ErrorCodes.Covenant.IneligibleTurn, chat.ToolFailure?.Code);

        Assert.Null(chat.Staged);

        Assert.Equal(0, _journal.Count);

        await using CovenantTurnScope later = await Gate().BeginTurnAsync(
            Invocation(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.DoesNotContain(ProposedContent, later.PlanContent.CampaignProposed, StringComparison.Ordinal);

    }

    private WizardIntelligenceProvider Wizard(
        StagingChatClient chat,
        CovenantDispatchGate gate,
        CovenantToolCapabilityRegistry registry)
    {

        ProviderSettings provider = new()
        {
            Name = "provider-covenant-bootstrap",
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
            repository,
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
    /// absence under which "no staging capability was minted" would read as a passing negative.
    /// </remarks>
    private ArcanumInvocationContext Invocation(
        InvocationAttendance attendance = InvocationAttendance.Attended)
    {

        _ = OperationGate();

        CovenantAuthoritySnapshot authority = _authority.Current!;

        return ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CovenantOperationGateFixture.CampaignContext(CampaignId),
            attendance,
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
            new CovenantMutationKernel());

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
    /// row would seed a Campaign no canonical query could ever match.
    /// </remarks>
    private async Task SeedCampaignAsync()
    {

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = _db!.Campaigns.Add(new Campaign
        {
            Id = CampaignId,
            Name = "covenant-bootstrap",
            NameLower = "covenant-bootstrap",
            Path = Path.Combine(Path.GetTempPath(), "covenant-bootstrap"),
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
    /// The binding row is written the way the turn path writes and reads one — the same table, the
    /// same authorization scope its guard trigger demands, and the same lowercase identity text
    /// <c>GrimoireRepository</c> binds and queries with. Its foreign key is stood down for that one
    /// statement because EF stores the Session key as uppercase text and the binding writer does not,
    /// so the reference can never hold; that mismatch is a defect of its own and not one this file
    /// can assert around, and reproducing the row the turn actually looks for is what keeps the rest
    /// of the test about the Covenant.
    ///
    /// <para>Nothing else is seeded. The turn writes its own user entry and assistant placeholder,
    /// and there are no Covenant rows and no sensitivity labels — which is the whole point: seeding a
    /// labelled entry is what made every earlier fixture prove the tainted arm instead of this
    /// one.</para>
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
            Title = "covenant bootstrap",
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
    /// A scripted provider that spends the turn's staging capability the way a tool round does.
    /// </summary>
    /// <remarks>
    /// It runs inside the turn's async flow, which is the whole point: the staging ambient is an
    /// <c>AsyncLocal</c> the turn loop pushes around the provider call, so a client that cannot see
    /// it here is a turn in which no tool call could have seen it either. Nothing is constructed by
    /// hand — the ambient is read, handed to the production binder, and the tool is then called over
    /// the wire, so the capability this proposal runs under is minted and taken by production code.
    /// </remarks>
    private sealed class StagingChatClient(
        CovenantToolCall toolCall,
        string key,
        string content) : IChatClient
    {

        public bool SawStagingMaterial { get; private set; }

        public bool SawRegisteredCapability => toolCall.SawRegisteredCapability;

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

            SawStagingMaterial = CovenantToolStagingAmbient.Current is not null;

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

            return new ChatResponse(new MeAiChatMessage(
                ChatRole.Assistant,
                "Noted — I will lead with the failing assertion."));

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
    /// capability from the staging ambient and registers it under the connection and request
    /// identity; the server is what takes it back out and publishes it to the handler. Registering a
    /// capability by hand would skip precisely the handover the whole capability model exists to
    /// enforce.
    /// </remarks>
    private sealed class CovenantToolCall : IAsyncDisposable
    {

        private readonly InProcessMcpTransport _transport;

        private readonly Task _serverTask;

        private readonly CancellationTokenSource _lifetime;

        private readonly string _connectionKey;

        private readonly CovenantToolCapabilityRegistry _registry;

        private int _nextId;

        private CovenantToolCall(
            InProcessMcpTransport transport,
            Task serverTask,
            CancellationTokenSource lifetime,
            string connectionKey,
            CovenantToolCapabilityRegistry registry)
        {

            _transport = transport;

            _serverTask = serverTask;

            _lifetime = lifetime;

            _connectionKey = connectionKey;

            _registry = registry;

        }

        public bool SawRegisteredCapability { get; private set; }

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

            return new CovenantToolCall(
                transport,
                serverTask,
                lifetime,
                server.AmbientConnectionKey,
                registry);

        }

        public async Task<McpToolsCallResultWire> ProposeAsync(string key, string content)
        {

            int id = Interlocked.Increment(ref _nextId);

            string requestId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

            SawRegisteredCapability = _registry.CountForTests == 1;

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

    /// <summary>A journal that accepts everything and counts what it was asked to record.</summary>
    /// <remarks>
    /// Counting is the assertion. A bootstrap turn shows the provider no Covenant bytes, so it owes
    /// no disclosure, and a journal that merely accepted would let a spurious one pass unnoticed.
    /// </remarks>
    private sealed class RecordingDisclosureJournal : ICovenantDisclosureJournal
    {

        private ulong _sequence;

        public int Count => (int)Interlocked.Read(ref _acknowledged);

        private long _acknowledged;

        public ValueTask<Result<CovenantDisclosureReceipt>> AcknowledgeAsync(
            CovenantDisclosureDraft draft,
            CovenantDisclosureEffectCategory category,
            ProviderCallSensitivity sensitivity,
            CancellationToken cancellationToken)
        {

            _ = Interlocked.Increment(ref _acknowledged);

            return ValueTask.FromResult(Result<CovenantDisclosureReceipt>.Success(
                new CovenantDisclosureReceipt(draft, ++_sequence)));

        }

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
