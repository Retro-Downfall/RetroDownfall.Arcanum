using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderCovenantTests
{
    [Theory]
    [InlineData("buffered")]
    [InlineData("streaming")]
    [InlineData("preview")]
    public async Task Invalid_confirmed_sampling_artifact_fails_before_provider_dispatch(string surface)
    {

        CapturingChatClient chat = Scripted();

        CovenantDispatchGate gate = new(
            new InvalidArtifactContextProvider(InvalidSamplingSnapshot()),
            new AcceptingJournal(),
            new CleanSensitivityLedger(),
            new EstablishedAuthority(),
            TimeProvider.System,
            NullLogger<CovenantDispatchGate>.Instance);

        WizardIntelligenceProvider wizard = Wizard(chat, gate);

        ArcanumInvocationContext invocation = InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId);

        if (surface == "buffered")
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(Request(), invocation, CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);
        }
        else if (surface == "streaming")
        {
            List<IntelligenceEvent> frames = [];

            await foreach (IntelligenceEvent frame in wizard.StreamPromptAsync(Request(), invocation, CancellationToken.None))
            {
                frames.Add(frame);
            }

            IntelligenceEvent error = Assert.Single(frames, static frame => frame.Type == IntelligenceEventType.Error);

            Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, error.Data);
        }
        else
        {
            Result<ContextPreviewResult> result = await wizard.PreviewContextAsync(
                new ContextPreviewRequest(Prompt: "inspect this turn", Model: ModelName, CampaignId: CovenantTask6Fixture.CampaignId, NoRetrieval: true),
                invocation,
                CancellationToken.None);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);
        }

        Assert.Empty(chat.Options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_context_and_background_invocations_do_not_acquire_typed_defaults(bool noContext)
    {
        CapturingChatClient chat = Scripted();

        // The real provider must decide eligibility before touching any optional capability.
        CovenantContextProvider contextProvider = new(null!, null!, null!, new CovenantLinker());

        CovenantDispatchGate gate = new(
            contextProvider,
            new AcceptingJournal(),
            new CleanSensitivityLedger(),
            new EstablishedAuthority(),
            TimeProvider.System,
            NullLogger<CovenantDispatchGate>.Instance);

        Result<PromptTurnResult> result = await Wizard(chat, gate).ExecutePromptAsync(
            Request(),
            noContext ? InvocationContexts.NoContextSession() : ArcanumInvocationContext.None,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.DoesNotContain("The Covenant", Assert.Single(chat.SystemPrompts), StringComparison.Ordinal);

        Assert.Null(Assert.Single(chat.Options).Temperature);

        Assert.Null(Assert.Single(chat.Options).TopP);
    }

    [Fact]
    public async Task Preview_rejects_corrupt_defaults_before_resolving_any_provider()
    {
        CapturingChatClient chat = Scripted();

        ProviderSettings provider = PreviewProvider();

        RecordingPreviewFactory factory = new(new SingleLeaseChatClientFactory(chat, provider, ModelName));

        CovenantDispatchGate gate = AcquisitionGate(new InvalidArtifactContextProvider(InvalidSamplingSnapshot()));

        WizardIntelligenceProvider wizard = WizardIntelligenceProviderFallbackTests.CreateCovenantWizard(factory, gate, null, provider);

        // Retrieval stays enabled: routing, embeddings and entity extraction must all sit behind
        // acquisition, including the provider resolution required before any of those paths.
        Result<ContextPreviewResult> result = await wizard.PreviewContextAsync(
            new ContextPreviewRequest(Prompt: "inspect this turn", Model: ModelName, CampaignId: CovenantTask6Fixture.CampaignId),
            InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId),
            CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

        Assert.Equal(0, factory.Resolutions);

        Assert.Empty(chat.Options);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_acquires_once_and_releases_its_scope_even_when_provider_resolution_fails(bool failResolution)
    {
        CapturingChatClient chat = Scripted();

        ProviderSettings provider = PreviewProvider();

        RecordingPreviewFactory factory = new(new SingleLeaseChatClientFactory(chat, provider, ModelName), failResolution);

        RecordingPreviewContextProvider contextProvider = new();

        WizardIntelligenceProvider wizard = WizardIntelligenceProviderFallbackTests.CreateCovenantWizard(factory, AcquisitionGate(contextProvider), null, provider);

        Result<ContextPreviewResult> result = await wizard.PreviewContextAsync(
            new ContextPreviewRequest(Prompt: "inspect this turn", Model: ModelName, CampaignId: CovenantTask6Fixture.CampaignId, NoRetrieval: true),
            InvocationContexts.AttendedSession(CovenantTask6Fixture.CampaignId),
            CancellationToken.None);

        Assert.Equal(!failResolution, result.IsSuccess);

        Assert.Equal(1, contextProvider.Acquisitions);

        Assert.Equal(1, contextProvider.Registration.Releases);
    }

    private static ProviderSettings PreviewProvider() => new()
    {
        Name = "provider-covenant",
        Type = AiProviderKind.OpenAICompatible,
        Endpoint = "https://example.test/v1",
        Models = [ModelName],
        ContextWindowLimit = 32_768,
    };

    private static CovenantDispatchGate AcquisitionGate(ICovenantContextProvider provider) => new(
        provider,
        new AcceptingJournal(),
        new CleanSensitivityLedger(),
        new EstablishedAuthority(),
        TimeProvider.System,
        NullLogger<CovenantDispatchGate>.Instance);

    private sealed class RecordingPreviewFactory(IChatClientFactory inner, bool failResolution = false) : IChatClientFactory
    {
        public int Resolutions { get; private set; }

        public Task<ChatClientLease> ResolveClientAsync(string? targetModel, CancellationToken cancellationToken)
        {
            Resolutions++;

            return failResolution
                ? throw new InvalidOperationException("Provider unavailable.")
                : inner.ResolveClientAsync(targetModel, cancellationToken);
        }

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings candidate, string resolvedModel, CancellationToken cancellationToken) =>
            ResolveClientAsync(resolvedModel, cancellationToken);
    }

    private sealed class RecordingPreviewContextProvider : ICovenantContextProvider
    {
        public int Acquisitions { get; private set; }

        public RecordingPreviewRegistration Registration { get; } = new();

        public ValueTask<Result<CovenantTurnContext>> BeginTurnAsync(ArcanumInvocationContext invocation, Guid logicalTurnId, CancellationToken cancellationToken)
        {
            Acquisitions++;

            return ValueTask.FromResult(Result<CovenantTurnContext>.Success(CovenantTurnContext.ForPlan(
                CovenantCompositionFixture.Plan(1, 0),
                new CovenantTurnLease(Registration),
                null,
                logicalTurnId)));
        }
    }

    private sealed class RecordingPreviewRegistration : ICovenantLeaseRegistration
    {
        public int Releases { get; private set; }

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new InertLeaseRegistration().Snapshot;

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask ReleaseAsync()
        {
            Releases++;

            return ValueTask.CompletedTask;
        }
    }

    private static CovenantTurnSnapshot InvalidSamplingSnapshot()
    {
        CovenantSnapshotCandidate candidate = CovenantTask6Fixture.CreateCandidate(
            "defaults.inference.temperature",
            CovenantTask6Fixture.G1,
            CovenantTask6Fixture.G2,
            1,
            CovenantScope.Global,
            null,
            CovenantLane.Confirmed,
            CovenantOperation.Set,
            CovenantOrigin.Operator,
            CovenantCompiler.CompilerPolicyVersion,
            0,
            CovenantSnapshotCandidateIntegrity.Verified,
            compiledFragment: [.. Encoding.UTF8.GetBytes("- defaults.inference.temperature: \"garbage\"\n")]);


        return CovenantTask6Fixture.Snapshot(CovenantTask6Fixture.CampaignId, candidate);
    }

    private sealed class InvalidArtifactContextProvider(CovenantTurnSnapshot snapshot) : ICovenantContextProvider
    {
        public ValueTask<Result<CovenantTurnContext>> BeginTurnAsync(
            ArcanumInvocationContext invocation,
            Guid logicalTurnId,
            CancellationToken cancellationToken)
        {
            Result<CovenantTurnPlan> linked = new CovenantLinker().Link(snapshot);

            Assert.True(linked.IsFailure);

            return ValueTask.FromResult(Result<CovenantTurnContext>.Failure(linked.Error));
        }
    }
}
