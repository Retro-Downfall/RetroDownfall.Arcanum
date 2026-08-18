using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ContextMaterializationLedgerTests
{

    [Fact]

    public void AttachmentProvenance_IsPublishedOnlyAfterSuccessfulMaterialization()
    {

        Guid sessionId = Guid.NewGuid();

        Guid attachmentId = Guid.NewGuid();

        AttachmentMemoryProvenance provenance = new(
            sessionId,
            attachmentId,
            "design-notes",
            3,
            "sha256-value",
            DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
            "SessionAttachmentRag",
            AttachmentSourceAvailability.Available);

        ContextMaterializationLedger ledger = CreateLedger(sessionId);

        using IDisposable scope = AttachmentMemoryGateAmbient.BeginTurn();

        ContextMaterializationEntry rejected = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.AttachmentRag,
                attachmentId.ToString("N"),
                versionOrdinal: 3,
                contentHash: "sha256-value",
                range: new ContextMaterializationRange(0, 12)) with
            {
                AttachmentProvenance = provenance,
            },
            materialized: false);

        Assert.False(rejected.Accepted);

        Assert.False(AttachmentMemoryGateAmbient.TryResolve(attachmentId, out _));

        ContextMaterializationEntry accepted = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.AttachmentRag,
                attachmentId.ToString("N"),
                versionOrdinal: 3,
                contentHash: "sha256-value",
                range: new ContextMaterializationRange(0, 12)) with
            {
                AttachmentProvenance = provenance,
            },
            materialized: true);

        Assert.True(accepted.Accepted);

        Assert.True(AttachmentMemoryGateAmbient.TryResolve(attachmentId, out AttachmentMemoryProvenance resolved));

        Assert.Equal(provenance, resolved);

    }

    [Fact]

    public void ExplicitWholeAttachment_SuppressesSemanticChunksFromSameVersion()
    {

        Guid sessionId = Guid.NewGuid();

        ContextMaterializationLedger ledger = CreateLedger(sessionId);

        ContextMaterializationEntry explicitEntry = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.ExplicitAttachmentReference,
                "attachment-1",
                versionOrdinal: 2,
                contentHash: "whole-hash",
                range: ContextMaterializationRange.Whole),
            materialized: true);

        ContextMaterializationEntry semanticEntry = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.AttachmentRag,
                "attachment-1",
                versionOrdinal: 2,
                contentHash: "chunk-hash",
                range: new ContextMaterializationRange(0, 120)),
            materialized: true);

        Assert.True(explicitEntry.Accepted);

        Assert.False(semanticEntry.Accepted);

        Assert.Equal(ContextMaterializationRejection.ExplicitSourceAlreadyMaterialized, semanticEntry.Rejection);

    }

    [Fact]

    public void IdenticalBytesAndRange_AreInjectedOnlyOnceAcrossSourcePaths()
    {

        Guid sessionId = Guid.NewGuid();

        ContextMaterializationLedger ledger = CreateLedger(sessionId);

        ContextMaterializationEntry attached = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.CurrentTurnAttachment,
                "current-file",
                contentHash: "same-content",
                range: ContextMaterializationRange.Whole),
            materialized: true);

        ContextMaterializationEntry pinned = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.ExplicitContextPin,
                "pin-1",
                contentHash: "same-content",
                range: ContextMaterializationRange.Whole),
            materialized: true);

        Assert.True(attached.Accepted);

        Assert.False(pinned.Accepted);

        Assert.Equal(ContextMaterializationRejection.DuplicateContentRange, pinned.Rejection);

        Assert.True(ledger.TryMarkInjected(attached.Identity, providerRound: 0));

        Assert.False(ledger.TryMarkInjected(attached.Identity, providerRound: 1));

        ContextMaterializationEntry recorded = Assert.Single(ledger.Entries);

        Assert.True(recorded.Injected);

        Assert.Equal(0, recorded.ProviderRound);

    }

    [Fact]

    public void IdenticalCurrentTurnParts_AreAllAcceptedAsExplicitOperatorInput()
    {

        Guid sessionId = Guid.NewGuid();

        ContextMaterializationLedger ledger = CreateLedger(sessionId);

        ContextMaterializationEntry first = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.CurrentTurnAttachment,
                "current-part-1",
                contentHash: "same-content",
                range: ContextMaterializationRange.Whole),
            materialized: true);

        ContextMaterializationEntry second = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.CurrentTurnAttachment,
                "current-part-2",
                contentHash: "same-content",
                range: ContextMaterializationRange.Whole),
            materialized: true);

        Assert.True(first.Accepted);

        Assert.True(second.Accepted);

        Assert.Equal(2, ledger.Entries.Count);

    }

    [Fact]

    public void RefreshedCurrentVersion_RemovesStaleSemanticVersion()
    {

        Guid sessionId = Guid.NewGuid();

        ContextMaterializationLedger ledger = CreateLedger(sessionId);

        ContextMaterializationEntry stale = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.AttachmentRag,
                "attachment-1",
                versionOrdinal: 1,
                contentHash: "old-chunk",
                range: new ContextMaterializationRange(0, 80)),
            materialized: true);

        ContextMaterializationEntry refreshed = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.RefreshSessionFile,
                "attachment-1",
                versionOrdinal: 2,
                contentHash: "new-whole",
                range: ContextMaterializationRange.Whole),
            materialized: true);

        Assert.True(stale.Accepted);

        Assert.True(refreshed.Accepted);

        ContextMaterializationEntry remaining = Assert.Single(ledger.Entries);

        Assert.Equal(ContextMaterializationSourceKind.RefreshSessionFile, remaining.SourceKind);

        ContextMaterializationEntry staleAfterRefresh = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.AttachmentRag,
                "attachment-1",
                versionOrdinal: 1,
                contentHash: "other-old-chunk",
                range: new ContextMaterializationRange(81, 160)),
            materialized: true);

        Assert.False(staleAfterRefresh.Accepted);

        Assert.Equal(ContextMaterializationRejection.StaleVersion, staleAfterRefresh.Rejection);

    }

    [Fact]

    public void ModelRequestedWholeAttachment_RemovesSameVersionSemanticChunks()
    {

        Guid sessionId = Guid.NewGuid();

        ContextMaterializationLedger ledger = CreateLedger(sessionId);

        _ = ledger.Accept(
            SemanticCandidate(sessionId, "attachment-1", "whole-hash", 20, 5, 0),
            materialized: true);

        ContextMaterializationEntry attached = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.AttachSessionFile,
                "attachment-1",
                versionOrdinal: 1,
                contentHash: "whole-hash",
                range: ContextMaterializationRange.Whole),
            materialized: true);

        Assert.True(attached.Accepted);

        Assert.Equal(ContextMaterializationSourceKind.AttachSessionFile, Assert.Single(ledger.Entries).SourceKind);

    }

    [Fact]

    public void ExplicitLiveWorkspaceSource_SuppressesEquivalentWorkspaceRagChunks()
    {

        Guid sessionId = Guid.NewGuid();

        ContextMaterializationLedger ledger = CreateLedger(sessionId);

        ledger.RegisterExplicitWorkspaceSource("docs/notes.md");

        ContextMaterializationEntry semantic = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.WorkspaceRag,
                "docs/notes.md\u001f2",
                contentHash: "workspace-chunk",
                range: new ContextMaterializationRange(2, 2)),
            materialized: true);

        Assert.False(semantic.Accepted);

        Assert.Equal(
            ContextMaterializationRejection.ExplicitSourceAlreadyMaterialized,
            semantic.Rejection);

    }

    [Fact]

    public void SemanticBounds_LimitChunksAttachmentsBytesAndTokens()
    {

        Guid sessionId = Guid.NewGuid();

        ContextMaterializationLedger ledger = new(
            sessionId,
            new ContextMaterializationLimits(
                MaxRetrievedChunks: 3,
                MaxRetrievedAttachments: 2,
                MaxRetrievedBytes: 25,
                MaxRetrievedTokens: 12));

        ContextMaterializationEntry first = ledger.Accept(
            SemanticCandidate(sessionId, "attachment-1", "hash-1", 10, 4, 0),
            materialized: true);

        ContextMaterializationEntry second = ledger.Accept(
            SemanticCandidate(sessionId, "attachment-2", "hash-2", 10, 4, 10),
            materialized: true);

        ContextMaterializationEntry tooManyAttachments = ledger.Accept(
            SemanticCandidate(sessionId, "attachment-3", "hash-3", 1, 1, 20),
            materialized: true);

        ContextMaterializationEntry tooManyBytes = ledger.Accept(
            SemanticCandidate(sessionId, "attachment-1", "hash-4", 6, 1, 30),
            materialized: true);

        ContextMaterializationEntry tooManyTokens = ledger.Accept(
            SemanticCandidate(sessionId, "attachment-1", "hash-5", 5, 5, 40),
            materialized: true);

        Assert.True(first.Accepted);

        Assert.True(second.Accepted);

        Assert.Equal(ContextMaterializationRejection.RetrievedAttachmentLimit, tooManyAttachments.Rejection);

        Assert.Equal(ContextMaterializationRejection.RetrievedByteLimit, tooManyBytes.Rejection);

        Assert.Equal(ContextMaterializationRejection.RetrievedTokenLimit, tooManyTokens.Rejection);

    }

    [Fact]

    public void ContextPressure_DropsLowestPrioritySemanticSourcesBeforeExplicitContent()
    {

        Guid sessionId = Guid.NewGuid();

        ContextMaterializationLedger ledger = CreateLedger(sessionId);

        _ = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.CurrentTurnAttachment,
                "explicit",
                contentHash: "explicit-hash",
                range: ContextMaterializationRange.Whole),
            materialized: true);

        _ = ledger.Accept(SemanticCandidate(sessionId, "attachment", "attachment-hash", 10, 3, 0), materialized: true);

        _ = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.WorkspaceRag,
                "workspace",
                contentHash: "workspace-hash",
                range: new ContextMaterializationRange(0, 10)),
            materialized: true);

        _ = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.SagaMemory,
                "saga",
                contentHash: "saga-hash",
                range: ContextMaterializationRange.Whole),
            materialized: true);

        Assert.Equal(ContextMaterializationSourceKind.SagaMemory, ledger.DropLowestPrioritySemantic()!.SourceKind);

        Assert.Equal(ContextMaterializationSourceKind.WorkspaceRag, ledger.DropLowestPrioritySemantic()!.SourceKind);

        Assert.Equal(ContextMaterializationSourceKind.AttachmentRag, ledger.DropLowestPrioritySemantic()!.SourceKind);

        Assert.Null(ledger.DropLowestPrioritySemantic());

        Assert.Equal(1, ledger.DroppedAttachmentRagChunks);

        Assert.Equal(3, ledger.DroppedAttachmentRagTokens);

        Assert.Equal(1, ledger.DroppedWorkspaceRagChunks);

        Assert.Equal(3, ledger.DroppedWorkspaceRagTokens);

        Assert.Equal(ContextMaterializationSourceKind.CurrentTurnAttachment, Assert.Single(ledger.Entries).SourceKind);

    }

    [Fact]

    public void SessionIsolationAndFailedMaterialization_DoNotPolluteLedger()
    {

        Guid sessionId = Guid.NewGuid();

        ContextMaterializationLedger ledger = CreateLedger(sessionId);

        ContextMaterializationEntry otherSession = ledger.Accept(
            Candidate(
                Guid.NewGuid(),
                ContextMaterializationSourceKind.AttachmentRag,
                "other",
                contentHash: "other-hash",
                range: new ContextMaterializationRange(0, 10)),
            materialized: true);

        ContextMaterializationEntry failed = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.ExplicitAttachmentReference,
                "failed",
                contentHash: "failed-hash",
                range: ContextMaterializationRange.Whole),
            materialized: false);

        Assert.Equal(ContextMaterializationRejection.SessionMismatch, otherSession.Rejection);

        Assert.Equal(ContextMaterializationRejection.MaterializationFailed, failed.Rejection);

        Assert.Empty(ledger.Entries);

    }

    [Fact]

    public void IdenticalSequences_ProduceBufferedStreamingParity()
    {

        Guid sessionId = Guid.NewGuid();

        ContextMaterializationLedger buffered = CreateLedger(sessionId);

        ContextMaterializationLedger streaming = CreateLedger(sessionId);

        ContextMaterializationCandidate[] sequence =
        [
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.CurrentTurnAttachment,
                "explicit",
                contentHash: "explicit-hash",
                range: ContextMaterializationRange.Whole),
            SemanticCandidate(sessionId, "attachment", "semantic-hash", 12, 3, 0),
            SemanticCandidate(sessionId, "attachment", "semantic-hash", 12, 3, 0),
        ];

        foreach (ContextMaterializationCandidate candidate in sequence)
        {

            _ = buffered.Accept(candidate, materialized: true);

            _ = streaming.Accept(candidate, materialized: true);

        }

        Assert.Equal(buffered.Entries, streaming.Entries);

    }

    /// <summary>
    /// A ledger entry whose counterpart cannot be found in the rendered prompt frees nothing when it is
    /// dropped, so it must not be reported as pressure relief — the turn's <c>DroppedAttachmentRag*</c>
    /// counters are what the operator and telemetry read to explain a shrunken context.
    /// </summary>
    [Fact]

    public void RescindContextPressureDrop_UncountsADropThatFreedNothing()
    {

        Guid sessionId = Guid.NewGuid();

        ContextMaterializationLedger ledger = CreateLedger(sessionId);

        _ = ledger.Accept(SemanticCandidate(sessionId, "attachment", "attachment-hash", 10, 3, 0), materialized: true);

        ContextMaterializationEntry dropped = ledger.DropLowestPrioritySemantic()!;

        Assert.Equal(1, ledger.DroppedAttachmentRagChunks);

        Assert.Equal(3, ledger.DroppedAttachmentRagTokens);

        ledger.RescindContextPressureDrop(dropped);

        Assert.Equal(0, ledger.DroppedAttachmentRagChunks);

        Assert.Equal(0, ledger.DroppedAttachmentRagTokens);

        // The entry stays surrendered — it is not in the rendered prompt, so it is not context either,
        // and the cascade has to be able to move past it.
        Assert.Empty(ledger.Entries);

    }

    /// <summary>
    /// The eviction cascade must not abandon admission on the first ledger entry with no counterpart in
    /// the rendered prompt: the payload is still over budget, and the next-lowest semantic entry may
    /// well be the one holding it there.
    /// </summary>
    [Fact]

    public void CascadeSemanticDrops_SkipsPastAnEntryTheRenderedPromptDoesNotHold()
    {

        Guid sessionId = Guid.NewGuid();

        ContextMaterializationLedger ledger = CreateLedger(sessionId);

        _ = ledger.Accept(SemanticCandidate(sessionId, "attachment", "attachment-hash", 10, 3, 0), materialized: true);

        _ = ledger.Accept(
            Candidate(
                sessionId,
                ContextMaterializationSourceKind.WorkspaceRag,
                "workspace",
                contentHash: "workspace-hash",
                range: new ContextMaterializationRange(0, 10)),
            materialized: true);

        List<ContextMaterializationSourceKind> offered = [];

        // The workspace chunk is the lower priority and is surrendered first, but nothing in the
        // rendered prompt matches it; only the attachment chunk actually leaves the payload.
        bool OnDropped(ContextMaterializationEntry removed)
        {

            offered.Add(removed.SourceKind);

            return removed.SourceKind == ContextMaterializationSourceKind.AttachmentRag;

        }

        int budgetChecks = 0;

        bool IsOverBudget()
        {

            budgetChecks++;

            return !offered.Contains(ContextMaterializationSourceKind.AttachmentRag);

        }

        WizardIntelligenceProvider.CascadeSemanticDrops(ledger, OnDropped, IsOverBudget);

        Assert.Equal(
            [ContextMaterializationSourceKind.WorkspaceRag, ContextMaterializationSourceKind.AttachmentRag],
            offered);

        // Only the eviction that actually shrank the payload is reported.
        Assert.Equal(0, ledger.DroppedWorkspaceRagChunks);

        Assert.Equal(0, ledger.DroppedWorkspaceRagTokens);

        Assert.Equal(1, ledger.DroppedAttachmentRagChunks);

        Assert.Equal(3, ledger.DroppedAttachmentRagTokens);

        // Re-measuring an unchanged payload is a whole tokenizer pass plus a payload-wide hash for no
        // new information, so an ineffective drop must not trigger one: the entry check, then one
        // re-check after the drop that actually landed.
        Assert.Equal(2, budgetChecks);

    }

    private static ContextMaterializationLedger CreateLedger(Guid sessionId) =>
        new(
            sessionId,
            new ContextMaterializationLimits(
                MaxRetrievedChunks: 20,
                MaxRetrievedAttachments: 8,
                MaxRetrievedBytes: 1024,
                MaxRetrievedTokens: 256));

    private static ContextMaterializationCandidate SemanticCandidate(
        Guid sessionId,
        string sourceId,
        string contentHash,
        int bytes,
        int tokens,
        int start) =>
        Candidate(
            sessionId,
            ContextMaterializationSourceKind.AttachmentRag,
            sourceId,
            versionOrdinal: 1,
            contentHash: contentHash,
            range: new ContextMaterializationRange(start, start + bytes),
            bytes: bytes,
            tokens: tokens);

    private static ContextMaterializationCandidate Candidate(
        Guid sessionId,
        ContextMaterializationSourceKind sourceKind,
        string sourceId,
        int? versionOrdinal = null,
        string contentHash = "content-hash",
        ContextMaterializationRange? range = null,
        int bytes = 10,
        int tokens = 3) =>
        new(
            SessionId: sessionId,
            SourceKind: sourceKind,
            SourceId: sourceId,
            VersionOrContentHash: versionOrdinal?.ToString() ?? contentHash,
            VersionOrdinal: versionOrdinal,
            Range: range ?? ContextMaterializationRange.Whole,
            Origin: sourceKind is ContextMaterializationSourceKind.AttachmentRag
                or ContextMaterializationSourceKind.WorkspaceRag
                or ContextMaterializationSourceKind.SagaMemory
                    ? ContextMaterializationOrigin.Semantic
                    : ContextMaterializationOrigin.Explicit,
            SourceLabel: sourceId,
            ContentHash: contentHash,
            EstimatedTokens: tokens,
            MaterializedBytes: bytes,
            Trust: ContextMaterializationTrust.UntrustedData);

}
