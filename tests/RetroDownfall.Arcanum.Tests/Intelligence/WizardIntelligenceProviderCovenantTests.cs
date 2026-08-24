using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The composed Covenant arm: a real gate over a real linked plan, driving a real provider turn.
/// </summary>
/// <remarks>
/// Every other direct-construction test builds this provider with no gate, so the dispatch block is
/// unreachable under test and deleting it leaves the suite green. Composition is what is being tested
/// here. The gate is registered unconditionally in production, and until a turn runs with one
/// attached, "the operator's agreement reaches the model" is a claim supported by reading the code.
///
/// <para>The plan comes from <see cref="CovenantLinker"/> over synthetic candidates rather than from
/// SQL. That is the same object a storage-backed turn hands the gate, and it keeps this file about
/// the provider composition instead of about the canonical store, which its own tests already cover.</para>
/// </remarks>
public sealed class WizardIntelligenceProviderCovenantTests : IAsyncLifetime
{

    private const string ModelName = "wizard-covenant-test-model";

    private readonly TempWorkspace _workspace = new();

    public Task InitializeAsync() => _workspace.InitializeAsync();

    public Task DisposeAsync() => _workspace.DisposeAsync();

    [Fact]
    public async Task A_turn_with_no_gate_composed_sends_the_pre_covenant_prompt_unchanged()
    {

        CapturingChatClient chat = Scripted();

        WizardIntelligenceProvider wizard = Wizard(chat, covenantDispatch: null);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            Request(),
            InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId),
            CancellationToken.None,
            new InferenceAuditContext { RequestType = "test" });

        Assert.True(result.IsSuccess, result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : null);

        // Composing the runtime must not move a byte for a turn that admits nothing, or every
        // provider prefix cache invalidates the moment the arm is registered.
        Assert.DoesNotContain("The Covenant", Assert.Single(chat.SystemPrompts), StringComparison.Ordinal);

    }

    [Fact]
    public async Task A_covenant_that_cannot_fit_at_all_is_reported_as_withheld_rather_than_trimmed()
    {

        CapturingChatClient chat = Scripted();

        FakeInferenceAuditLogger audit = new();

        // A window with no room for the Confirmed section. Confirmed is admitted all-or-fail, so this
        // is a refusal rather than a trim -- and the planner reports a refusal as every Proposed
        // candidate pressured out, which reads as "honored, minus a few suggestions" if believed.
        WizardIntelligenceProvider wizard = Wizard(
            chat,
            Gate(confirmed: 16, proposed: 8),
            audit,
            contextWindowLimit: 2_300);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(
            Request(),
            InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId),
            CancellationToken.None,
            new InferenceAuditContext { RequestType = "test" });

        Assert.True(result.IsSuccess, result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : null);

        Assert.DoesNotContain(
            "### The Covenant, Global Confirmed",
            Assert.Single(chat.SystemPrompts),
            StringComparison.Ordinal);

        ContextTokenBreakdown breakdown = LastBreakdown(audit);

        Assert.True(breakdown.CovenantConfirmedNoFit);

        Assert.Equal(0, breakdown.DroppedCovenantProposed);

        Assert.Equal(0, breakdown.DroppedCovenantProposedTokens);

    }

    private static ContextTokenBreakdown LastBreakdown(FakeInferenceAuditLogger audit) =>
        audit.Records
            .SelectMany(static record => record.ContextBreakdowns ?? [])
            .Last();

    private static PingRequest Request() =>
        new(
            Prompt: "hello",
            Model: ModelName,
            WorkingDirectory: string.Empty,
            SkipSpellRouting: true,
            DisableMcpTools: true);

    private static CapturingChatClient Scripted()
    {

        CapturingChatClient chat = new();

        chat.EnqueueText("acknowledged");

        return chat;

    }

    private static WizardIntelligenceProvider Wizard(
        CapturingChatClient chat,
        CovenantDispatchGate? covenantDispatch,
        FakeInferenceAuditLogger? audit = null,
        int contextWindowLimit = 32_768)
    {

        ProviderSettings provider = new()
        {
            Name = "provider-covenant",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1",
            Models = [ModelName],
            ContextWindowLimit = contextWindowLimit,
        };

        return WizardIntelligenceProviderFallbackTests.CreateCovenantWizard(
            new SingleLeaseChatClientFactory(chat, provider, ModelName),
            covenantDispatch,
            audit,
            provider);

    }

    private static CovenantDispatchGate Gate(int confirmed, int proposed) =>
        new(
            new PlannedContextProvider(CovenantCompositionFixture.Plan(confirmed, proposed)),
            new AcceptingJournal(),
            new CleanSensitivityLedger(),
            new EstablishedAuthority(),
            TimeProvider.System,
            NullLogger<CovenantDispatchGate>.Instance);

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

    private sealed class PlannedContextProvider(CovenantTurnPlan plan) : ICovenantContextProvider
    {

        public ValueTask<Result<CovenantTurnContext>> BeginTurnAsync(
            ArcanumInvocationContext invocation,
            Guid logicalTurnId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<CovenantTurnContext>.Success(
                CovenantTurnContext.ForPlan(
                    plan,
                    new CovenantTurnLease(new InertLeaseRegistration()),
                    null,
                    logicalTurnId)));

    }

    private sealed class InertLeaseRegistration : ICovenantLeaseRegistration
    {

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            RegistrationId: Guid.Parse("11111111-2222-4333-8444-555555555555"),
            RuntimeAuthorityGeneration: 1,
            CovenantLeaseKind.Turn,
            CovenantLeaseCoverage.Scoped,
            CovenantOperationScope.Global,
            CovenantCompositionFixture.DatasetGeneration,
            CapabilityGeneration: 1,
            AuthorityEpoch: 11,
            CanonicalSequence: 0,
            CampaignAvailabilityGeneration: 1,
            CampaignPathRevision: null,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            RecoveryOwner: null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;

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

    private sealed class CleanSensitivityLedger : IArtifactSensitivityLedger
    {

        public Task<Result<LabeledArtifactWriteReceipt>> LabelAsync(
            DerivedArtifactWrite write,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<ArtifactSensitivityLabel?>> TryReadLabelAsync(
            SensitiveArtifactKind artifactKind,
            Guid artifactId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<SessionSensitivityProjection>> ReadSessionProjectionAsync(
            Guid sessionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<SessionSensitivityProjection>.Success(new SessionSensitivityProjection(
                sessionId,
                0,
                ContentSensitivity.None,
                CovenantTask6Fixture.D(7),
                1)));

    }

    private sealed class EstablishedAuthority : ICovenantAuthoritySnapshotProvider
    {

        public CovenantAuthoritySnapshot? Current { get; } = new(
            1,
            "11111111-2222-3333-4444-555555555555",
            1,
            1,
            1,
            CovenantHostToolsState.Clean,
            null);

    }

    /// <summary>
    /// Records the exact system message each provider attempt was handed.
    /// </summary>
    /// <remarks>
    /// Per attempt rather than per turn. A restart is precisely the case where the second call's
    /// prompt may differ from the first's, and one captured value would hide whichever was wrong.
    /// </remarks>
    private sealed class CapturingChatClient : IChatClient
    {

        private readonly Queue<Func<ChatResponse>> _responses = new();

        public List<string> SystemPrompts { get; } = [];

        public void EnqueueText(string text) =>
            _responses.Enqueue(() => new ChatResponse(new MeAiChatMessage(ChatRole.Assistant, text)));

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {

            foreach (MeAiChatMessage message in messages)
            {

                if (message.Role == ChatRole.System)
                {

                    SystemPrompts.Add(message.Text);

                    break;

                }

            }

            return _responses.Count == 0
                ? throw new InvalidOperationException("No scripted response remaining.")
                : Task.FromResult(_responses.Dequeue()());

        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MeAiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

}

/// <summary>
/// A real linked plan over synthetic candidates, so composition tests read the same object a
/// storage-backed turn hands the gate.
/// </summary>
internal static class CovenantCompositionFixture
{

    internal static readonly Guid DatasetGeneration = Guid.Parse("99999999-9999-9999-9999-999999999999");

    internal static CovenantTurnPlan Plan(int confirmed, int proposed)
    {

        List<CovenantSnapshotCandidate> candidates = [];

        for (int index = 0; index < confirmed; index++)
        {

            candidates.Add(CovenantTask6Fixture.GlobalConfirmed(
                $"confirmed.{index}",
                CovenantTask6Fixture.GuidFor(100 + index),
                CovenantTask6Fixture.GuidFor(200 + index),
                (ulong)(index + 1),
                (byte)(index + 1)));

        }

        for (int index = 0; index < proposed; index++)
        {

            candidates.Add(CovenantTask6Fixture.CampaignProposed(
                $"proposed.{index}",
                CovenantTask6Fixture.GuidFor(300 + index),
                CovenantTask6Fixture.GuidFor(400 + index),
                (ulong)(confirmed + index + 1),
                (byte)(50 + index),
                CovenantTask6Fixture.CampaignId));

        }

        return new CovenantLinker()
            .Link(CovenantTask6Fixture.Snapshot(CovenantTask6Fixture.CampaignId, [.. candidates]))
            .Value;

    }

}
