using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Infrastructure.Resilience;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saga_successful_turn_commits_only_exact_injected_content_once(bool streaming)
    {
        SagaUseCoordinate exact = new("memory-one", "claim-one", "version-one", 1, new CovenantDigest(Enumerable.Repeat((byte)0xaa, 32).ToArray()));

        FakeSagaMemoryStore memories = new();

        memories.Memories[exact.MemoryId] = new SagaMemoryDto(exact.MemoryId, "Prefer SQLite for local persistence.", DateTimeOffset.UtcNow, null, null, "extraction", UseCoordinate: exact);

        memories.Memories["duplicate"] = new SagaMemoryDto("duplicate", "Prefer SQLite for local persistence.", DateTimeOffset.UtcNow, null, null, "extraction",
            UseCoordinate: exact with { MemoryId = "duplicate", ClaimId = "duplicate-claim", VersionId = "duplicate-version" });

        memories.Memories["legacy"] = new SagaMemoryDto("legacy", "Legacy memory remains useful context.", DateTimeOffset.UtcNow, null, null, "extraction");

        ScriptingChatClient chat = new();

        chat.EnqueueText("Use SQLite.");

        chat.EnqueueStreamTokens("Use SQLite.");

        var (wizard, commits, request) = CreateSagaUseWizard(chat, memories, SagaUseSettings());

        ArcanumInvocationContext invocation = LongRestTask7Invocation(campaign: false);

        if (streaming)
        {
            List<IntelligenceEvent> events = [];

            await foreach (IntelligenceEvent frame in wizard.StreamPromptAsync(request, invocation, CancellationToken.None))
            {
                events.Add(frame);
            }

            Assert.Contains(events, frame => frame.Type is IntelligenceEventType.Result);

            Assert.DoesNotContain(events, frame => frame.Type is IntelligenceEventType.Error);
        }
        else
        {
            Result<PromptTurnResult> answer = await wizard.ExecutePromptAsync(request, invocation, CancellationToken.None);

            Assert.True(answer.IsSuccess, answer.IsFailure ? answer.Error.Message : null);
        }

        TurnCommitRequest committed = Assert.Single(commits.Commits);

        Assert.Equal(exact, Assert.Single(committed.SagaUses));

        IReadOnlyList<ChatMessage> dispatched = Assert.Single(streaming ? chat.AllStreamingCalls : chat.AllBufferedCalls);

        Assert.Contains("Legacy memory remains useful context.", dispatched.Single(message => message.Role == ChatRole.System).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saga_failed_turn_cannot_publish_successful_use_even_after_partial_output(bool streaming)
    {
        FakeSagaMemoryStore memories = SagaUseStore("Remember this exact version.");

        ScriptingChatClient chat = new();

        chat.EnqueueException(new InvalidOperationException("provider failed"));

        chat.EnqueueStreamFailure(new InvalidOperationException("provider failed after partial output"));

        var (wizard, claims, request) = CreateSagaUseWizard(chat, memories, SagaUseSettings());

        if (streaming)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (IntelligenceEvent frame in wizard.StreamPromptAsync(request, LongRestTask7Invocation(false), CancellationToken.None))
            {
                frames.Add(frame);
            }

            Assert.Contains(frames, frame => frame.Type is IntelligenceEventType.Error);

            Assert.Equal("partial", Assert.Single(claims.Commits).FinalText);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, LongRestTask7Invocation(false), CancellationToken.None);

            Assert.True(result.IsFailure);
        }

        Assert.Empty(claims.Commits.SelectMany(commit => commit.SagaUses));

        IReadOnlyList<ChatMessage> dispatched = Assert.Single(streaming ? chat.AllStreamingCalls : chat.AllBufferedCalls);

        Assert.Contains("Remember this exact version.", dispatched.Single(message => message.Role == ChatRole.System).Text);
    }

    [Fact]
    public async Task Saga_context_pressure_removes_use_for_memory_dropped_before_dispatch()
    {
        FakeSagaMemoryStore memories = SagaUseStore("pressure-marker " + string.Join(' ', Enumerable.Repeat("storage", 3000)));

        ArcanumSettings settings = SagaUseSettings() with { Providers = [DefaultProvider() with { ContextWindowLimit = 3000 }] };

        ScriptingChatClient chat = new();

        chat.EnqueueText("Memory did not fit.");

        var (wizard, claims, request) = CreateSagaUseWizard(chat, memories, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request with { MaxOutputTokens = 256 }, LongRestTask7Invocation(false), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Empty(Assert.Single(claims.Commits).SagaUses);

        string system = Assert.Single(chat.AllBufferedCalls).Single(message => message.Role == ChatRole.System).Text!;

        Assert.DoesNotContain("pressure-marker", system);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saga_fallback_publishes_only_final_provider_branch_exact_version(bool streaming)
    {
        FakeSagaMemoryStore memories = SagaUseStore("Version one was retrieved for the abandoned branch.");

        SagaUseCoordinate final = memories.Memories["memory-one"].UseCoordinate! with { VersionId = "version-two" };

        ScriptingChatClient chat = new();

        chat.EnqueueException(new HttpRequestException("connection dropped before answer"));

        chat.EnqueueText("Final provider succeeded.");

        chat.EnqueueImmediateStreamFailure(new HttpRequestException("connection dropped before answer"));

        chat.EnqueueStreamTokens("Final provider succeeded.");

        bool replaced = false;

        chat.BeforeCall = () =>
        {
            if (!replaced)
            {
                memories.Memories["memory-one"] = memories.Memories["memory-one"] with
                {
                    Content = "Version two was retrieved for the successful branch.", UseCoordinate = final,
                };

                replaced = true;
            }
        };

        ArcanumSettings settings = SagaUseSettings() with
        {
            Providers = [DefaultProvider() with { Name = "first" }, DefaultProvider() with { Name = "second" }],
        };

        var (wizard, claims, request) = CreateSagaUseWizard(chat, memories, settings,
            new SagaUseChatClientFactory(chat, settings.Providers[0]), new ProviderHealthTracker(NullLogger<ProviderHealthTracker>.Instance));

        if (streaming)
        {
            List<IntelligenceEvent> frames = [];

            await foreach (IntelligenceEvent frame in wizard.StreamPromptAsync(request, LongRestTask7Invocation(false), CancellationToken.None))
            {
                frames.Add(frame);
            }

            Assert.Contains(frames, frame => frame.Type is IntelligenceEventType.Result);
        }
        else
        {
            Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, LongRestTask7Invocation(false), CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        }

        Assert.Equal(final, Assert.Single(Assert.Single(claims.Commits).SagaUses));

        IReadOnlyList<IReadOnlyList<ChatMessage>> calls = streaming ? chat.AllStreamingCalls : chat.AllBufferedCalls;

        Assert.Equal(2, calls.Count);

        Assert.Contains("Version one was retrieved", calls[0].Single(message => message.Role == ChatRole.System).Text);

        Assert.Contains("Version two was retrieved", calls[1].Single(message => message.Role == ChatRole.System).Text);
    }

    [Fact]
    public async Task Saga_disabled_LongRest_keeps_ordinary_turn_without_receipt_or_claim()
    {
        FakeSagaMemoryStore memories = SagaUseStore("Gate-off memory still reaches the provider.");

        ArcanumSettings settings = SagaUseSettings();

        settings.Features.LongRest = false;

        ScriptingChatClient chat = new();

        chat.EnqueueText("ordinary answer");

        var (wizard, claims, request) = CreateSagaUseWizard(chat, memories, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, LongRestTask7Invocation(false), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Empty(claims.Commits);

        Assert.Empty(claims.Requests);

        Assert.Contains("Gate-off memory still reaches the provider.", Assert.Single(chat.AllBufferedCalls).Single(message => message.Role == ChatRole.System).Text);
    }

    [Fact]
    public async Task Saga_disabled_LongRest_keeps_legacy_ledger_identity_even_when_store_supplies_exact_coordinate()
    {
        FakeSagaMemoryStore memories = SagaUseStore("Gate-off memory still reaches the provider.");

        ArcanumSettings settings = SagaUseSettings();

        settings.Features.LongRest = false;

        ContextMaterializationEntry? admitted = null;

        ScriptingChatClient chat = new()
        {
            BeforeCall = () => admitted = Assert.Single(
                Assert.IsType<ContextMaterializationLedger>(ContextMaterializationLedgerAmbient.Ledger).Entries,
                entry => entry.SourceKind is ContextMaterializationSourceKind.SagaMemory),
        };

        chat.EnqueueText("ordinary answer");

        var (wizard, claims, request) = CreateSagaUseWizard(chat, memories, settings);

        Result<PromptTurnResult> result = await wizard.ExecutePromptAsync(request, LongRestTask7Invocation(false), CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.NotNull(admitted);

        Assert.True(admitted.Injected);

        Assert.Equal("71de05c4cbbc16ecaf187e8301c63c4d3a15bb99308588642554efb0a557d68b", admitted.Identity.SourceId);

        Assert.Equal("71de05c4cbbc16ecaf187e8301c63c4d3a15bb99308588642554efb0a557d68b", admitted.Identity.VersionOrContentHash);

        Assert.Empty(claims.Commits);
    }

    private static FakeSagaMemoryStore SagaUseStore(string content)
    {
        SagaUseCoordinate coordinate = new("memory-one", "claim-one", "version-one", 1, new CovenantDigest(Enumerable.Repeat((byte)0xaa, 32).ToArray()));

        FakeSagaMemoryStore memories = new();

        memories.Memories[coordinate.MemoryId] = new SagaMemoryDto(coordinate.MemoryId, content, DateTimeOffset.UtcNow, null, null, "extraction", UseCoordinate: coordinate);

        return memories;
    }

    private sealed class SagaUseChatClientFactory(ScriptingChatClient chat, ProviderSettings first) : IChatClientFactory
    {
        public Task<ChatClientLease> ResolveClientAsync(string? model, CancellationToken cancellationToken) =>
            ResolveClientAsync(first, model ?? ModelName, cancellationToken);

        public Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string model, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatClientLease(chat, provider, model, ownedHttpClient: null));
    }

    private (WizardIntelligenceProvider Wizard, CampaignWizardClaims Claims, PingRequest Request) CreateSagaUseWizard(
        ScriptingChatClient chat, FakeSagaMemoryStore memories, ArcanumSettings settings,
        IChatClientFactory? factory = null, IProviderHealthTracker? healthTracker = null)
    {
        Session session = new() { Id = Guid.NewGuid() };

        FakeGrimoireRepository grimoire = new() { Session = session };

        CampaignWizardClaims claims = new();

        CampaignRollupTurnPreparer preparer = new(new CampaignRollupTurnSnapshotTests.ProbeStore(),
            new TestOptionsMonitor<ArcanumSettings>(settings), new LongRestTask7ScopeResolver(CanonicalCampaignContext.GlobalOnly),
            null!, new LongRestTask7QueuedCoordinator(claims), UnreachedLongRestPorts.Maintenance, UnreachedLongRestPorts.Discovery,
            new LongRestTask7Configuration(settings), new PassThroughRootEnricher(), new CampaignWizardMaintenance(),
            claims, grimoire, claims, new CampaignRollupTurnSnapshotTests.Authority(), new CampaignWizardLookup(), replayStore: claims);

        WizardIntelligenceProvider wizard = CreateWizard(chat, settings, grimoire,
            weaveService: new FakeRagWeaveService { Available = true },
            divinationService: new FakeRagDivinationService
            {
                Results = memories.Memories.Keys.Select(id => new DivinationResult(id, 0.9f, EmptyDivinationMetadata)).ToArray(),
            },
            sagaMemoryStore: memories, factory: factory, healthTracker: healthTracker,
            turnCommitter: claims, claimedBeginStore: claims, claims: claims, campaignRollups: preparer);

        return (wizard, claims, BaseRequest() with
        {
            Prompt = "Choose local persistence", SessionId = session.Id, SkipSpellRouting = true,
            DisableAllTools = true, WorkingDirectory = _workspace.Root,
        });
    }

    private static ArcanumSettings SagaUseSettings() => DefaultSettings() with
    {
        Features = DefaultSettings().Features with { Embeddings = true, Saga = true, LongRest = true },
    };
}
