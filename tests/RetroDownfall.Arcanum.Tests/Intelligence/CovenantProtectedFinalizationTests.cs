using System.Collections.Immutable;

using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Storage.Entities;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Telemetry;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Tests.Covenant;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// A protected reply carries its label out of the turn, on every path that persists one.
/// </summary>
/// <remarks>
/// The completed path is the obvious one. The interrupted path is the one that matters: the reply is
/// partial, the turn is unwinding, and persisting what arrived without its label would launder the
/// taint that the next turn reads to decide whether it owes a disclosure.
/// </remarks>
public sealed class CovenantProtectedFinalizationTests
{

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consumed_root_refuses_atomic_staged_finalization_before_the_mutation_writer(bool consumed)
    {
        await using var h = await MemoryDelegationLifecycleTests.Harness.CreateAsync();
        await using var root = await h.PrepareAsync();
        if (consumed) Assert.True((await h.Coordinator.ConsumeAsync(root, h.RootId, Guid.NewGuid(), h.Digest)).IsSuccess);
        var binding = new RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation.MemoryDelegationRootBinding(
            new RetroDownfall.Arcanum.Core.Intelligence.PingRequest("parent"), h.Source.Invocation, h.RootId, root, h.Coordinator);
        RecordingCommitter committer = new();
        RecordingGrimoire grimoire = new();
        using var writer = CreateWriter(grimoire, committer);
        var handle = Handle();
        handle.OutputProtection = binding.CaptureOutputProtection;
        var collector = new PendingCollector();
        var staged = new CovenantTurnCommitBinding(collector, collector.CurrentBranchId, 1, Guid.NewGuid(), 1);
        var finalized = await writer.TryFinalizeBufferedAssistantEntryAsync(handle, "answer", "model", default, Derived(), staged)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(!consumed, finalized);
        Assert.Equal(consumed ? 0 : 1, committer.Requests.Count);
        Assert.Empty(grimoire.Finalized);
    }

    private sealed class PendingCollector : ICovenantMutationCollector
    {
        public Guid LogicalTurnId { get; } = Guid.NewGuid();
        public CovenantDigest BasePlanDigest { get; } = new(new byte[32]);
        public CovenantCollectorState State => CovenantCollectorState.Open;
        public Guid CurrentBranchId { get; } = Guid.NewGuid();
        public int StagedCount => 1;
        public ImmutableArray<CovenantMutationIntent> PendingIntents() => Seal(CurrentBranchId, 1).Value;
        public Result<IDisposable> TryAcquireUse() => throw new NotSupportedException();
        public Result OpenBranch(Guid branchId, ulong sharedPrefixOrdinal) => throw new NotSupportedException();
        public Result AbandonBranch(Guid branchId) => throw new NotSupportedException();
        public Result<CovenantStagedMutationReceipt> Stage(CovenantMutationIntent intent, CovenantAdmissionReceipt producingAdmission, CovenantDigest toolInputDigest) => throw new NotSupportedException();
        public void Discard() { }
        public Result<ImmutableArray<CovenantMutationIntent>> Seal(Guid committedBranchId, ulong finalBranchOrdinal)
        {
            var digest = BasePlanDigest;
            var intent = new CovenantMutationIntent(Guid.NewGuid(), CovenantMutationKind.AgentPropose, CovenantOperation.Set,
                CovenantOrigin.AgentProposed, new(CovenantOperationScope.ForCampaign(Guid.NewGuid()), new CovenantKey("test.key"), "test.key", CovenantLane.Proposed, digest),
                0, false, 0, new("value", "value", digest, digest, 5, 1, CovenantCompiler.CompilerPolicyVersion, CovenantCompiler.RendererPolicyVersion), [],
                new(digest, digest, digest, digest, CovenantAuthorizationMode.None, null, null), LogicalTurnId, "call", digest, digest);
            return ImmutableArray.Create(intent);
        }
    }

    [Fact]
    public async Task Missing_protected_committer_refuses_instead_of_persisting_unlabelled_reply()
    {
        RecordingGrimoire grimoire = new();
        using var writer = new GrimoireTurnWriter(grimoire, new FakeSessionTurnBeginStore(),
            new SessionEventHub(NullLogger<SessionEventHub>.Instance), NullLogger<GrimoireTurnWriter>.Instance);
        Assert.False(await writer.TryFinalizeBufferedAssistantEntryAsync(Handle(), "protected", "model", default, Derived()));
        Assert.Empty(grimoire.Finalized);
    }

    [Fact]
    public async Task A_completed_protected_reply_is_committed_with_its_label()
    {

        RecordingCommitter committer = new();

        RecordingGrimoire grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire, committer);

        GrimoireTurnWriter.TurnHandle handle = Handle();

        bool finalized = await writer.TryFinalizeBufferedAssistantEntryAsync(
            handle,
            "an answer",
            "model",
            CancellationToken.None,
            Derived());

        Assert.True(finalized);

        TurnCommitRequest request = Assert.Single(committer.Requests);

        Assert.Equal(ContentSensitivity.CovenantDerived, request.ContentSensitivity);

        Assert.Equal("an answer", request.FinalText);

        Assert.Equal(AssistantFinalizationOutcome.Committed, request.Outcome);

    }

    [Fact]
    public async Task An_interrupted_protected_stream_persists_its_partial_content_labelled()
    {

        RecordingCommitter committer = new();

        RecordingGrimoire grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire, committer);

        GrimoireTurnWriter.TurnHandle handle = Handle();

        bool resolved = await writer.ResolveInterruptedAsync(
            handle,
            "half an ans",
            CancellationToken.None,
            Derived());

        Assert.True(resolved);

        TurnCommitRequest request = Assert.Single(committer.Requests);

        Assert.Equal(ContentSensitivity.CovenantDerived, request.ContentSensitivity);

        Assert.Equal("half an ans", request.FinalText);

    }

    [Fact]
    public async Task An_ordinary_reply_never_reaches_the_committer()
    {

        RecordingCommitter committer = new();

        RecordingGrimoire grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire, committer);

        _ = await writer.TryFinalizeBufferedAssistantEntryAsync(
            Handle(),
            "an answer",
            "model",
            CancellationToken.None);

        _ = await writer.ResolveInterruptedAsync(Handle(), "partial", CancellationToken.None);

        Assert.Empty(committer.Requests);

    }

    [Fact]
    public async Task A_protected_reply_that_cannot_be_labelled_is_not_persisted_at_all()
    {

        RecordingCommitter committer = new()
        {
            Failure = new Error(ErrorCodes.Grimoire.WriteFailed, "no"),
        };

        RecordingGrimoire grimoire = new();

        GrimoireTurnWriter writer = CreateWriter(grimoire, committer);

        GrimoireTurnWriter.TurnHandle handle = Handle();

        bool finalized = await writer.TryFinalizeBufferedAssistantEntryAsync(
            handle,
            "an answer",
            "model",
            CancellationToken.None,
            Derived());

        Assert.False(finalized);

        Assert.False(handle.IsFinalized);

    }

    private static GrimoireTurnWriter CreateWriter(
        RecordingGrimoire grimoire,
        IGrimoireTurnCommitter committer) =>
        new(
            grimoire,
            new FakeSessionTurnBeginStore(),
            new SessionEventHub(NullLogger<SessionEventHub>.Instance),
            NullLogger<GrimoireTurnWriter>.Instance,
            committer);

    private static GrimoireTurnWriter.TurnHandle Handle()
    {

        GrimoireTurnWriter.TurnHandle handle = new();

        typeof(GrimoireTurnWriter.TurnHandle)
            .GetProperty(nameof(GrimoireTurnWriter.TurnHandle.AssistantEntryId))!
            .SetValue(handle, Guid.NewGuid());

        typeof(GrimoireTurnWriter.TurnHandle)
            .GetProperty(nameof(GrimoireTurnWriter.TurnHandle.SessionId))!
            .SetValue(handle, Guid.NewGuid());

        return handle;

    }

    private static ProviderCallSensitivity Derived()
    {

        GenerationProvenance provenance =
            GenerationProvenance.CreateExact([Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]);

        return new ProviderCallSensitivity(
            ContentSensitivity.CovenantDerived,
            provenance,
            CovenantDigests.Sensitivity(new SensitivityDigestInput(
                ContentSensitivity.CovenantDerived,
                provenance.Mode,
                provenance.ExactGenerationIds,
                provenance.BloomBits)));

    }

    /// <summary>
    /// The unlabelled write path, so a test can prove which arm a reply actually took.
    /// </summary>
    /// <remarks>
    /// Everything this suite does not exercise throws rather than returning a default: a fake that
    /// quietly answers a call the production code was not supposed to make is a fake that hides the
    /// bug the test exists to find.
    /// </remarks>
    private sealed class RecordingGrimoire : IGrimoireRepository
    {

        public List<string> Finalized { get; } = [];

        public int Discards { get; private set; }

        public Task<(Guid SessionId, Guid AssistantEntryId)> BeginAssistantReplyAsync( Guid? sessionId, string prompt, string model, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task FinalizeAssistantEntryAsync(
            Guid assistantEntryId,
            string fullContent,
            CancellationToken cancellationToken = default)
        {

            Finalized.Add(fullContent);

            return Task.CompletedTask;

        }

        public Task DiscardAssistantEntryAsync(
            Guid assistantEntryId,
            CancellationToken cancellationToken = default)
        {

            Discards++;

            return Task.CompletedTask;

        }

        public Task AppendToolInteractionAsync( Guid sessionId, string toolName, string arguments, string result, string modelUsed, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveCompletedExchangeAsync( string userPrompt, string assistantText, string modelUsed, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Session?> GetSessionAsync( Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Session?> GetSessionHeaderAsync( Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<GrimoireEntryDto>?> GetSessionEntriesAsync( Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<GrimoireEntryDto>?> GetRecentSessionEntriesAsync( Guid sessionId, int takeLast, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GrimoireEntryDto?> GetEntryByIdAsync( Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteEntryAsync( Guid sessionId, Guid entryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SetEntryPinnedAsync( Guid sessionId, Guid entryId, bool pinned, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> GetPinnedEntryCountAsync( Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<Guid>> GetSessionsNeedingSummarizationAsync( int threshold, DateTime idleCutoff, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<Entry>> GetUnsummarizedEntriesAsync( Guid sessionId, DateTime watermark, int batchSize, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task IncrementSessionTokensAsync(Guid sessionId, long totalTokens, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task IncrementSessionTokensAndCostAsync( Guid sessionId, long totalTokens, decimal costUsd, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<decimal> GetTodaySpendAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AdvanceCampaignLogWatermarkAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateSessionCampaignRollupAsync( Guid sessionId, string summary, DateTime lastSummarizedMessageAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> ReadLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LoreDto> ScribeLoreAsync(string key, string value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ListPageResult<LoreDto>> ListLoreAsync( int? limit = null, int offset = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LoreDto?> GetLoreAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> SearchArchivesAsync(string query, int maxResults, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RecordWorkspaceContextAsync(WorkspaceContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkspaceContext?> GetLatestWorkspaceContextAsync(string workspacePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

    }

    private sealed class RecordingCommitter : IGrimoireTurnCommitter
    {

        public List<TurnCommitRequest> Requests { get; } = [];

        public Error? Failure { get; init; }

        public Task<Result<TurnCommitReceipt>> CommitTurnAsync(
            TurnCommitRequest request,
            CancellationToken cancellationToken)
        {

            Requests.Add(request);

            return Task.FromResult(Failure is { } error
                ? Result<TurnCommitReceipt>.Failure(error)
                : Result<TurnCommitReceipt>.Success(new TurnCommitReceipt(
                    request.AssistantEntryId,
                    request.Outcome,
                    Replayed: false,
                    ImmutableArray<CovenantMutationReceipt>.Empty)));

        }

    }

}
