using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// R-008: a delegated child turn runs inside its parent's tool call, where the parent's turn ambients
/// are re-established, and must leave the parent's accounting and attachment state as it found them.
/// </summary>
/// <remarks>
/// <c>SubagentRunnerTests</c> and <c>ContextMaterializationLedgerTests</c> pin the two halves of this in
/// isolation, against a fake child and a hand-driven ledger. This drives a real child Wizard turn,
/// through the real turn coordinator, from inside a real parent Wizard turn's tool call, which is the
/// composition the two hazards come from: the child adopting the parent's accounting (and settling the
/// parent's run and reservation when it finishes), and the child's ledger begin tearing down the
/// parent's per-Session attachment state.
/// </remarks>
public sealed partial class WizardIntelligenceProviderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToolCall_DelegatingToARealChildTurn_LeavesTheParentsAccountingAndAttachmentStateIntact(bool streaming)
    {
        const string toolName = "delegate_to_child_turn";

        Guid sessionId = Guid.NewGuid();

        Guid attachmentId = Guid.NewGuid();

        byte[] payload = System.Text.Encoding.UTF8.GetBytes("explicitly attached notes");

        SessionAttachmentRecord record = new(
            attachmentId,
            sessionId,
            EntryId: null,
            PendingTurnId: null,
            SessionAttachmentState.Bound,
            "notes",
            "notes.txt",
            Version: 1,
            RelativePath: $"noop/{attachmentId:N}",
            ContentSha256: attachmentId.ToString("N"),
            MimeType: "text/plain",
            ByteLength: payload.Length,
            SessionAttachmentKind.Text,
            DateTimeOffset.UtcNow);

        NoOpSessionAttachmentStore store = new(
            records: new Dictionary<Guid, SessionAttachmentRecord> { [attachmentId] = record },
            openRead: (_, _) => Task.FromResult<Stream>(new MemoryStream(payload, writable: false)));

        ArcanumSettings defaults = DefaultSettings();

        ArcanumSettings settings = defaults with
        {
            Cost = defaults.Cost with
            {
                Pricing = new PricingSettings
                {
                    DefaultPricing = new ModelPricingEntry
                    {
                        InputPer1M = 1m,
                        OutputPer1M = 1m,
                    },
                },
            },
        };

        ScriptingChatClient childChat = new();

        childChat.EnqueueText("child summary");

        RecordingTurnRunWriter childWriter = new();

        RecordingBudgetReservationService childReservations = new();

        WizardIntelligenceProvider childWizard = CreateWizard(
            childChat,
            settings,
            turnRunWriter: childWriter,
            budgetReservationService: childReservations);

        bool childSawParentAccounting = true;

        SubagentRunner runner = new(
            new Lazy<ITurnExecutionFacade>(() => new ChildWizardTurnFacade(
                childWizard,
                () => childSawParentAccounting = TurnAccountingAmbient.Current is not null)),
            new AcquiringOperationCoordinator(),
            TimeProvider.System,
            NullLogger<SubagentRunner>.Instance);

        List<DelegatedChildTurnObservation> observed = [];

        FakeMcpConnectionManager mcp = new();

        mcp.Tools.Add(
            AIFunctionFactory.Create(
                async () =>
                {
                    TurnAccountingHandle? parentAccounting = TurnAccountingAmbient.Current;

                    SubagentRunResult child = await runner
                        .RunAsync(
                            new SubagentRunRequest("summarize the notes", ModelName, [], MaxTokens: 1_000, MaxCostUsd: null),
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    observed.Add(new DelegatedChildTurnObservation(
                        parentAccounting,
                        TurnAccountingAmbient.Current,
                        child,
                        AttachmentMemoryGateAmbient.HasSessionStateForTests(sessionId),
                        AttachmentMemoryGateAmbient.TryResolve(attachmentId, out _)));

                    return child.Summary;
                },
                toolName,
                "runs one delegated child turn"));

        ScriptingChatClient parentChat = new();

        if (streaming)
        {
            parentChat.EnqueueStreamToolCall(toolName, "call-1");

            parentChat.EnqueueStreamTokens("done");
        }
        else
        {
            parentChat.EnqueueToolCall(toolName, "call-1");

            parentChat.EnqueueText("done");
        }

        RecordingTurnRunWriter parentWriter = new();

        RecordingBudgetReservationService parentReservations = new();

        WizardIntelligenceProvider parentWizard = CreateWizard(
            parentChat,
            settings,
            grimoire: new FakeGrimoireRepository
            {
                Session = new Session { Id = sessionId, Entries = [] },
            },
            mcp: mcp,
            turnRunWriter: parentWriter,
            budgetReservationService: parentReservations,
            sessionAttachmentStore: store);

        PingRequest request = BaseRequest() with
        {
            Prompt = "delegate the summary",
            SessionId = sessionId,
            AttachmentReferences = [attachmentId],
            SkipSpellRouting = true,
        };

        ArcanumInvocationContext invocation = InvocationContexts.AttendedSession();

        if (streaming)
        {
            List<IntelligenceEvent> events = [];

            await foreach (IntelligenceEvent evt in parentWizard.StreamPromptAsync(request, invocation, CancellationToken.None))
            {
                events.Add(evt);
            }

            Assert.Contains(events, static e => e.Type == IntelligenceEventType.Result);
        }
        else
        {
            Result<PromptTurnResult> result = await parentWizard.ExecutePromptAsync(
                request,
                invocation,
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.IsFailure ? $"{result.Error.Code}: {result.Error.Message}" : null);
        }

        DelegatedChildTurnObservation seen = Assert.Single(observed);

        Assert.True(seen.Child.Success, seen.Child.FailureCode);

        Assert.Equal("child summary", seen.Child.Summary);

        // The child began its own run and reservation rather than adopting the parent's.
        Assert.False(childSawParentAccounting);

        Assert.NotNull(childReservations.LastRequest);

        Assert.Equal(InferenceRunStatus.Completed, Assert.Single(childWriter.CompletedRuns).Status);

        Assert.Equal(1, childReservations.ReconcileCount);

        // A child that had adopted the parent's handle would have settled the parent's run when it
        // finished, and a settled run reads as no ambient accounting at all.
        Assert.NotNull(seen.ParentAccountingBefore);

        Assert.Same(seen.ParentAccountingBefore, seen.ParentAccountingAfter);

        // The child's ledger begin must not have disposed the parent's attachment state: its
        // per-Session entry is how the in-process server's tools find it.
        Assert.True(seen.ParentSessionStateRegistered);

        Assert.True(seen.ParentAttachmentResolvable);

        // The parent's run is settled once, by the parent, after its own reply.
        Assert.Equal(InferenceRunStatus.Completed, Assert.Single(parentWriter.CompletedRuns).Status);

        Assert.Equal(1, parentReservations.ReconcileCount);

        Assert.False(AttachmentMemoryGateAmbient.HasSessionStateForTests(sessionId));
    }

    /// <summary>What the parent's tool call could see of its own turn once the child turn returned.</summary>
    private sealed record DelegatedChildTurnObservation(
        TurnAccountingHandle? ParentAccountingBefore,
        TurnAccountingHandle? ParentAccountingAfter,
        SubagentRunResult Child,
        bool ParentSessionStateRegistered,
        bool ParentAttachmentResolvable);

    /// <summary>
    /// Runs the child request as a real buffered Wizard turn, which goes through the real turn
    /// coordinator and engine exactly as the production facade does.
    /// </summary>
    private sealed class ChildWizardTurnFacade(
        WizardIntelligenceProvider childWizard,
        Action onExecute) : ITurnExecutionFacade
    {
        public Task<Result<PromptTurnResult>> ExecuteBufferedAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken executionToken,
            InferenceAuditContext? auditContext = null)
        {
            onExecute();

            return childWizard.ExecutePromptAsync(request, invocationContext, executionToken, auditContext);
        }

        public IAsyncEnumerable<IntelligenceEvent> ExecuteIntelligenceStreamAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken executionToken,
            InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException("A delegated child turn is buffered.");
    }

    /// <summary>A durable operation store that grants every lease and accepts every transition.</summary>
    private sealed class AcquiringOperationCoordinator : ILongRunningOperationCoordinator
    {
        public Task<LongRunningOperationLeaseResult> StartAsync(
            LongRunningOperationCreateRequest request,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LongRunningOperationLeaseResult(
                true,
                new LongRunningOperation(
                    Guid.NewGuid(),
                    request.Kind,
                    LongRunningOperationState.Running,
                    request.RecoveryPolicy,
                    request.RootOperationId,
                    request.ParentOperationId,
                    request.SessionId,
                    request.RunId,
                    request.InferenceRunId,
                    request.BudgetReservationId,
                    request.IdempotencyClaimId,
                    request.CreatedAt,
                    request.CreatedAt,
                    request.CreatedAt,
                    CompletedAt: null,
                    LeaseOwner: ownerId,
                    LeaseExpiresAt: request.CreatedAt.Add(leaseDuration),
                    AttemptCount: 1,
                    CheckpointVersion: 0,
                    CheckpointPayload: null,
                    CheckpointReference: null,
                    request.PublicSummary,
                    TerminalErrorCode: null,
                    Revision: 1)));

        public Task<Result<LongRunningOperationRequestIdentityResult>> StartWithRequestIdentityAsync(
            LongRunningOperationCreateRequest request,
            LongRunningOperationRequestIdentity identity,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> HeartbeatAsync(
            Guid operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> CheckpointAsync(
            Guid operationId,
            string ownerId,
            int expectedCheckpointVersion,
            int checkpointVersion,
            byte[]? checkpointPayload,
            string? checkpointReference,
            string publicSummary,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> CompleteAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> FailAsync(
            Guid operationId,
            string ownerId,
            long expectedRevision,
            string errorCode,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
