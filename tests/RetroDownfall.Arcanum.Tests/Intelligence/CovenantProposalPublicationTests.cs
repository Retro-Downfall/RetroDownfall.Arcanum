using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
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
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// One agent proposal, from the tool that staged it to the later turn that reads it back (§10.13).
/// </summary>
/// <remarks>
/// Every component here is the production one: the real canonical store over a real installed
/// schema, the real context provider, linker, and operation gate, the real dispatch gate that mints
/// the admission, the real capability the MCP binding constructs, the real agent mutation factory
/// and collector, the real <see cref="GrimoireTurnWriter"/> entry point the inference loop calls,
/// and the real mutation kernel underneath the turn committer.
///
/// <para>Nothing here hand-builds a batch, a binding, or a <c>TurnCommitRequest</c>. That is the
/// whole point of the file: every previous attempt at this path proved an arm no production caller
/// reached, and a test that assembles the sealed batch itself proves exactly that again. The
/// assertion is on the rendered Proposed section a second, independent turn is handed — the bytes a
/// model would actually be shown — rather than on any intermediate object.</para>
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class CovenantProposalPublicationTests : IAsyncLifetime
{

    private const string ProposedKey = "tests.reply.style";

    private const string ProposedContent = "answer with the failing assertion first";

    private static readonly Guid CampaignId = Guid.Parse("6C1F2A94-7B3D-4E58-9A02-D45E7F1C8B36");

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private SqliteConnection? _connection;

    private readonly FakeCovenantAvailability _availability = new();

    private readonly FakeCovenantAuthorityProvider _authority = new();

    private readonly FakeCovenantCampaignScopeProbe _campaigns = new();

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
    public async Task A_proposal_staged_by_one_turn_is_rendered_to_a_later_turn_that_never_saw_it()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync();

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        CovenantDispatchGate gate = Gate();

        Guid stagingTurnId = Guid.NewGuid();

        // Turn one: adopt the Covenant, admit a dispatch, stage a proposal through the same
        // capability the in-process MCP binding mints, then finalize.
        ProviderCallSensitivity sensitivity;

        CovenantTurnCommitBinding? commit;

        await using (CovenantTurnScope staging = await gate.BeginTurnAsync(
            Invocation(),
            stagingTurnId,
            sessionId,
            CancellationToken.None))
        {

            Assert.True(staging.HasPlan);

            Assert.NotNull(staging.Collector);

            CovenantDispatchAdmission admitted = await AdmitAsync(gate, staging);

            await StageProposalAsync(staging, admitted, stagingTurnId);

            Assert.Equal(1, staging.Collector!.StagedCount);

            sensitivity = admitted.Sensitivity;

            commit = staging.StagedCommit();

            Assert.NotNull(commit);

            bool finalized = await Writer().TryFinalizeBufferedAssistantEntryAsync(
                Handle(sessionId, assistantEntryId),
                "Noted — I will lead with the failing assertion.",
                "test-model",
                CancellationToken.None,
                sensitivity,
                commit);

            Assert.True(finalized);

        }

        Assert.Equal(
            "Noted — I will lead with the failing assertion.",
            await ReadAssistantContentAsync(assistantEntryId));

        // Turn two shares nothing with turn one but the installation: a new logical turn, a new
        // plan, a new lease, and a store read that never saw the collector.
        await using CovenantTurnScope later = await gate.BeginTurnAsync(
            Invocation(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(later.HasPlan);

        string rendered = later.PlanContent.CampaignProposed;

        // The rendered bytes, not the head row. A head that existed but rendered into nothing would
        // satisfy every intermediate assertion and still show the model nothing.
        Assert.Contains(ProposedKey, rendered, StringComparison.Ordinal);

        Assert.Contains(ProposedContent, rendered, StringComparison.Ordinal);

        // The lane matters as much as the content. Proposed content that arrived in a Confirmed
        // section would be an agent writing the operator's own instructions.
        Assert.DoesNotContain(ProposedContent, later.PlanContent.CampaignConfirmed, StringComparison.Ordinal);

        Assert.DoesNotContain(ProposedContent, later.PlanContent.GlobalConfirmed, StringComparison.Ordinal);

        Assert.Equal(CovenantLane.Proposed, Assert.Single(later.Plan!.CampaignProposedSection.Candidates).Candidate.Lane);

    }

    [SkippableFact]
    public async Task An_interrupted_turn_publishes_neither_its_partial_answer_nor_its_staged_proposal()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync();

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        CovenantDispatchGate gate = Gate();

        Guid stagingTurnId = Guid.NewGuid();

        await using (CovenantTurnScope staging = await gate.BeginTurnAsync(
            Invocation(),
            stagingTurnId,
            sessionId,
            CancellationToken.None))
        {

            CovenantDispatchAdmission admitted = await AdmitAsync(gate, staging);

            await StageProposalAsync(staging, admitted, stagingTurnId);

            // The stream-exit arm, reached both by a genuine interrupt and by the cleanup that runs
            // after the atomic finalize refused. Committing the partial reply here would leave the
            // operator an answer whose proposal the tool had already reported as accepted.
            bool resolved = await Writer().ResolveInterruptedAndMarkFinalizedAsync(
                Handle(sessionId, assistantEntryId),
                "partial streamed text",
                CancellationToken.None,
                admitted.Sensitivity,
                staging.StagedCommit());

            Assert.True(resolved);

        }

        Assert.Null(await ReadAssistantContentAsync(assistantEntryId));

        await using CovenantTurnScope later = await gate.BeginTurnAsync(
            Invocation(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.DoesNotContain(ProposedContent, later.PlanContent.CampaignProposed, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task A_turn_whose_batch_cannot_publish_atomically_refuses_rather_than_saving_the_answer_alone()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await SeedCampaignAsync();

        (Guid sessionId, Guid assistantEntryId) = await SeedTurnAsync();

        CovenantDispatchGate gate = Gate();

        Guid stagingTurnId = Guid.NewGuid();

        await using CovenantTurnScope staging = await gate.BeginTurnAsync(
            Invocation(),
            stagingTurnId,
            sessionId,
            CancellationToken.None);

        CovenantDispatchAdmission admitted = await AdmitAsync(gate, staging);

        await StageProposalAsync(staging, admitted, stagingTurnId);

        // A host with no batch-aware committer composed. The ordinary reply would fall through to
        // the plain finalize path; a turn holding a proposal must not, because that path writes the
        // answer and drops the batch without telling anyone.
        GrimoireTurnWriter uncomposed = new(
            Repository(withKernel: false),
            Repository(withKernel: false),
            new SessionEventHub(NullLogger<SessionEventHub>.Instance),
            NullLogger<GrimoireTurnWriter>.Instance);

        bool finalized = await uncomposed.TryFinalizeBufferedAssistantEntryAsync(
            Handle(sessionId, assistantEntryId),
            "an answer that must not outlive its batch",
            "test-model",
            CancellationToken.None,
            admitted.Sensitivity,
            staging.StagedCommit());

        Assert.False(finalized);

        Assert.Equal(string.Empty, await ReadAssistantContentAsync(assistantEntryId));

    }

    private async Task<CovenantDispatchAdmission> AdmitAsync(CovenantDispatchGate gate, CovenantTurnScope scope)
    {

        CovenantDispatchPlan plan = CovenantDispatchGate.PlanDispatch(
            scope,
            100_000,
            static content => (ulong)(content.GlobalConfirmed.Length
                + content.CampaignConfirmed.Length
                + content.CampaignProposed.Length),
            static fragment => (ulong)fragment.Length);

        // Resolved by the gate, never asserted into it. A first proposal is authored on a turn that
        // showed the provider no Covenant bytes at all, so the honest label is None — and the receipt
        // below has to be minted anyway, because the staging capability is minted from it. Asserting
        // the label here is what stops this file from quietly reverting to the tainted arm, which is
        // the arm it used to manufacture for itself.
        ProviderCallSensitivity sensitivity = CovenantDispatchGate.ResolveSensitivity(scope, plan);

        Assert.Equal(ContentSensitivity.None, sensitivity.Level);

        Result<CovenantDispatchAdmission> admitted = await gate.AcknowledgeDispatchAsync(
            scope,
            plan,
            ProviderCall(sensitivity),
            CancellationToken.None);

        Assert.True(admitted.IsSuccess, admitted.IsFailure ? admitted.Error.Message : null);

        Assert.NotNull(admitted.Value.Receipt);

        return admitted.Value;

    }

    /// <summary>
    /// Stages one proposal the way a live <c>propose_covenant</c> call does.
    /// </summary>
    /// <remarks>
    /// The capability is minted and registered exactly as <c>BindCovenantStaging</c> mints it — same
    /// collector, same Campaign, same producing admission, same call materialization, same probe, no
    /// retirement preflight and no Ward receipt — and taken back out of the real registry the way the
    /// handler takes it. Constructing one directly and using it would skip the register-and-take
    /// handover that is the only thing standing between a turn's collector and an arbitrary caller.
    /// </remarks>
    private static async Task StageProposalAsync(
        CovenantTurnScope scope,
        CovenantDispatchAdmission admitted,
        Guid turnId)
    {

        CovenantToolCapabilityRegistry registry = new();

        CovenantToolCapabilityNonce nonce = CovenantToolCapabilityNonce.Create();

        string requestId = $"call-{turnId:N}";

        await using CovenantToolInvocationContext registered = new(
            scope.Collector!,
            CovenantOperationGateFixture.CampaignContext(CampaignId),
            admitted.Receipt!,
            admitted.Receipt!.Materialization,
            scope.HeadProbe!,
            nonce,
            CovenantToolNames.ProposeCovenant,
            requestId,
            retirementPreflight: null,
            wardReceipt: null,
            CancellationToken.None);

        Assert.True(registry.TryRegister("connection-1", requestId, registered, nonce));

        Result<CovenantToolCapabilityGrant> grant = registry.TryTake("connection-1", requestId);

        Assert.True(grant.IsSuccess, grant.IsFailure ? grant.Error.Message : null);

        CovenantToolInvocationContext capability = grant.Value.Capability;

        Result<IDisposable> lease = capability.TryAcquireUse(grant.Value.Nonce);

        Assert.True(lease.IsSuccess, lease.IsFailure ? lease.Error.Message : null);

        using (lease.Value)
        {

            CovenantCompiledContent compiled = new CovenantCompiler().Compile(ProposedKey, ProposedContent);

            Result<CovenantLaneHeadProbe> probe = await capability.ProbeLaneHeadAsync(
                grant.Value.Nonce,
                CovenantLane.Proposed,
                compiled.NormalizedKey,
                CancellationToken.None);

            Assert.True(probe.IsSuccess, probe.IsFailure ? probe.Error.Message : null);

            Result<CovenantMutationIntent> intent = CovenantAgentMutationFactory.Propose(
                capability,
                compiled,
                probe.Value.Presence is CovenantLaneHeadPresence.Present ? probe.Value.LaneRevision : 0,
                probe.Value.KeyEpoch,
                CovenantTask6Fixture.D(71));

            Assert.True(intent.IsSuccess, intent.IsFailure ? intent.Error.Message : null);

            Assert.True(capability.RecheckBeforeIrreversibleEffect(grant.Value.Nonce).IsSuccess);

            Result<ICovenantMutationCollector> collector = capability.ResolveCollector(grant.Value.Nonce);

            Assert.True(collector.IsSuccess, collector.IsFailure ? collector.Error.Message : null);

            Result<CovenantStagedMutationReceipt> staged = collector.Value.Stage(
                intent.Value,
                capability.ProducingAdmission,
                CovenantTask6Fixture.D(71));

            Assert.True(staged.IsSuccess, staged.IsFailure ? staged.Error.Message : null);

        }

    }

    /// <summary>
    /// The composed dispatch gate over the real store, provider, linker, and operation gate.
    /// </summary>
    /// <remarks>
    /// One operation gate for the whole test, because a second would mint its own authority and every
    /// turn built against the first would be refused as stale — which is the same shape as a real
    /// failure and would hide one.
    /// </remarks>
    private CovenantDispatchGate Gate() =>
        new(
            new CovenantContextProvider(
                _availability,
                OperationGate(),
                new CovenantStore(new FixedCovenantConnectionSource(Connection())),
                new CovenantLinker()),
            new AcceptingJournal(),
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
    /// make every turn here degrade to <see cref="CovenantTurnAbsence.CapabilityUnavailable"/> — a
    /// silent absence that still lets most assertions in this file read as a passing composition.
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

    private GrimoireTurnWriter Writer()
    {

        GrimoireRepository repository = Repository(withKernel: true);

        return new GrimoireTurnWriter(
            repository,
            repository,
            new SessionEventHub(NullLogger<SessionEventHub>.Instance),
            NullLogger<GrimoireTurnWriter>.Instance,
            repository);

    }

    private GrimoireRepository Repository(bool withKernel) =>
        new(
            _db!,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot(new ArcanumSettings()),
            attachmentIndex: null,
            withKernel ? new CovenantMutationKernel() : null);

    private static GrimoireTurnWriter.TurnHandle Handle(Guid sessionId, Guid assistantEntryId) =>
        new()
        {
            SessionId = sessionId,
            AssistantEntryId = assistantEntryId,
        };

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
    /// The Session and the assistant placeholder a turn finalizes into, and nothing more.
    /// </summary>
    /// <remarks>
    /// No sensitivity label. This file used to write one itself and then assert the taint it had just
    /// written, which meant every assertion below it ran on the tainted arm — an arm no installation
    /// reaches until some turn has already published a proposal. A first proposal is authored on a
    /// clean Session against an empty Covenant, so that is the state seeded here, and the label the
    /// gate resolves for it is checked rather than supplied.
    /// </remarks>
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
            Title = "covenant proposal",
            UnsummarizedEntryCount = 2,
        });

        _ = _db.Entries.Add(new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Role = MessageRole.User,
            Content = "always lead with the failing assertion",
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

    private async Task<string?> ReadAssistantContentAsync(Guid assistantEntryId) =>
        await _db!.Entries
            .AsNoTracking()
            .Where(entry => entry.Id == assistantEntryId)
            .Select(entry => entry.Content)
            .FirstOrDefaultAsync(CancellationToken.None);

    private static ProviderCallEnvelope ProviderCall(ProviderCallSensitivity sensitivity) =>
        new(
            "provider.test",
            "model.test",
            CovenantProviderDispatchMode.Buffered,
            "o200k_base",
            128_000,
            0,
            sensitivity,
            FrozenProviderOptions.Create(new ProviderOptionsDigestInput(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                ProviderToolChoice.Auto,
                null,
                CovenantTriStateBoolean.Absent,
                ProviderResponseFormat.Text,
                null,
                null,
                null,
                CovenantTriStateBoolean.Absent,
                null,
                null,
                null,
                CovenantReasoningWireDialect.Standard,
                default)),
            [],
            [],
            new ProviderCallMaterializationSnapshot(false, []),
            [],
            [],
            null);

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

    private sealed class TestOptionsSnapshot(ArcanumSettings value) : IOptionsSnapshot<ArcanumSettings>
    {

        public ArcanumSettings Value => value;

        public ArcanumSettings Get(string? name) => value;

    }

}
