using System.Globalization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Memory;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Renders an erase's server-measured plan before the question, and its result after.
/// </summary>
/// <remarks>
/// Every number is the host's. A client that recomputed a count would be asking the operator to
/// confirm a second opinion about an effect only the host can measure, and the effect digest the
/// token carries binds the host's measurement, not this process's.
///
/// <para>Every enum is rendered through a closed map rather than its name, so a reader sees what a
/// note or a retained copy means rather than an identifier. A value a map does not know fails the
/// command instead of printing a guess. Nothing here prints content: the wire carries none, and the
/// show response the target came from is never written.</para>
///
/// <para>The plan follows the mode — the payload stream in human mode, the diagnostic stream under
/// <c>--json</c>, where stdout belongs to the one result document. The Covenant availability costs do
/// not: draining in-flight turns and reclaiming the key are warnings an operator weighs before
/// answering, so they go to the diagnostic stream in every mode, beside the question.</para>
/// </remarks>
internal static class MemoryErasureRenderer
{

    /// <summary>The scrub verb a pending log checkpoint is finished by.</summary>
    private const string ScrubCommand = "arcanum memory erasure scrub";

    /// <summary>What a Covenant erase costs every Covenant turn and installation-wide lease while it runs.</summary>
    private const string DrainSentence =
        "Erasing drains in-flight Covenant turns first: it waits up to 30 seconds for them to finish, "
            + "turns that start meanwhile run without Covenant content, and backups, prepares and inventory "
            + "fail fast until it completes.";

    /// <summary>Why a reclaiming erase stales every outstanding Covenant preflight, on every key.</summary>
    private const string ReclaimSentence =
        "This erase reclaims the key, so every outstanding Covenant preflight on this installation goes stale "
            + "and must be prepared again.";

    /// <summary>What reclaiming does to the key's curation beyond the erased scope.</summary>
    private const string ReclaimScopeSentence =
        "Reclaiming removes the key's pins and masks in every scope, not only this one; the key can be set "
            + "again later and starts with none.";

    public static void WritePreflight(
        IConsoleDispatcher dispatcher,
        MemoryErasurePreflightDto preflight,
        bool json)
    {

        ArgumentNullException.ThrowIfNull(dispatcher);

        ArgumentNullException.ThrowIfNull(preflight);

        Action<string> write = json ? dispatcher.WriteDiagnostic : dispatcher.WritePayload;

        MemoryErasurePlanDto plan = preflight.Plan;

        write($"{preflight.Store} erase plan, mutation {preflight.MutationId:D}:");

        write($"  Items erased: {Count(plan.ErasedItemCount)}");

        write($"  Rows removed: {Count(plan.RowsToRemove)}");

        write($"  Sensitivity labels removed: {Count(plan.LabelsToRemove)}");

        write($"  Retirement suppressions removed: {Count(plan.RetirementSuppressionsToRemove)}");

        write(plan.Pinned ? "  Pinned: yes. A pin does not stop an erase." : "  Pinned: no");

        if (plan.Lexicon is { GlobalEntryResurfaces: true })
        {

            write("  A Global entry of the same name exists; Campaign turns will see it once this entry is gone.");

        }

        if (plan.Covenant is { } covenant)
        {

            WriteCovenantFacts(write, covenant);

        }

        foreach (MemoryErasureNote note in preflight.Notes)
        {

            if (note is not MemoryErasureNote.CovenantDrainsInFlightTurns)
            {

                write($"  {NoteText(note)}");

            }

        }

        write("  Local copies this erase does not reach, reported and never purged:");

        foreach (MemoryRetainedLocalCopy copy in preflight.RetainedLocalCopies)
        {

            write($"    - {RetainedCopyText(copy)}");

        }

        write($"  This plan expires {preflight.ExpiresAtUtc.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)}.");

        if (preflight.Notes.Contains(MemoryErasureNote.CovenantDrainsInFlightTurns))
        {

            dispatcher.WriteDiagnostic(DrainSentence);

        }

        if (plan.Covenant is { ReclaimsKey: true })
        {

            dispatcher.WriteDiagnostic(ReclaimSentence);

            dispatcher.WriteDiagnostic(ReclaimScopeSentence);

        }

    }

    public static void WriteResult(
        IConsoleDispatcher dispatcher,
        MemoryErasureResultDto result)
    {

        ArgumentNullException.ThrowIfNull(dispatcher);

        ArgumentNullException.ThrowIfNull(result);

        MemoryErasureLocalResultDto local = result.Local;

        dispatcher.WritePayload(result.Replayed
            ? $"Already erased: {result.Store} mutation {result.MutationId:D} was applied earlier, and this is its recorded result."
            : $"Erased: {result.Store} mutation {result.MutationId:D}.");

        dispatcher.WritePayload(
            $"  Items erased: {Count(local.ErasedItemCount)}; rows removed: {Count(local.RemovedRowCount)}; "
            + $"sensitivity labels removed: {Count(local.RemovedLabelCount)}; "
            + $"retirement suppressions removed: {Count(local.RemovedRetirementSuppressionCount)}.");

        dispatcher.WritePayload($"  Local erasure: {LocalOutcome(local)}");

        if (local.Outcome is MemoryLocalErasureOutcome.RowsRemovedScrubPending
            && CheckpointAttemptText(local.WalCheckpointAttempt) is { } attempt)
        {

            dispatcher.WritePayload($"  {attempt}");

        }

        dispatcher.WritePayload(local.SuppressionFingerprintRecorded
            ? "  Erasure fingerprint recorded: extraction and agents cannot write this again in this exact scope."
            : "  No erasure fingerprint was recorded.");

        dispatcher.WritePayload($"  {RevocationText(result.External.Revocation)}");

        dispatcher.WritePayload("  The local copies the plan listed were reported and not purged.");

        foreach (MemoryErasureNote note in result.Notes)
        {

            if (note is not MemoryErasureNote.CovenantDrainsInFlightTurns)
            {

                dispatcher.WritePayload($"  {NoteText(note)}");

            }

        }

        dispatcher.WritePayload("No other memory store was touched.");

    }

    /// <summary>
    /// Names the mutation of an apply whose outcome is unknown, because it may have committed.
    /// </summary>
    /// <remarks>
    /// Callers write this whenever the apply was sent and nothing proves it rolled back: the host said
    /// the commit's outcome could not be read back (<c>Covenant.ManualRecoveryRequired</c>), it failed
    /// with an exception it never classified (<c>Hub.Unhandled</c>), its answer could not be read at all, or the operator cancelled after the request went out — a Covenant
    /// erase completes its disposition whatever the caller does, so cancelling does not stop it. A
    /// typed refusal the host did send proves a rollback, and there the note would be false. The
    /// identity is a random, content-free GUID the operator can match against the host's own log and
    /// receipts.
    /// </remarks>
    public static void WriteUnconfirmedApply(
        IConsoleDispatcher dispatcher,
        Guid mutationId)
    {

        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.WriteDiagnostic(
            $"The host did not confirm erase mutation {mutationId:D}, so it may have been applied. "
            + "Show the item again before erasing it again.");

    }

    private static void WriteCovenantFacts(Action<string> write, CovenantErasurePlanFacts covenant)
    {

        write(
            $"  Versions: {Count(covenant.ConfirmedVersions)} Confirmed, {Count(covenant.ProposedVersions)} Proposed; "
            + $"provenance leaves: {Count(covenant.ProvenanceLeaves)}; mutation receipts: {Count(covenant.MutationReceipts)}; "
            + $"curation rows: {Count(covenant.CurationRows)}; search outbox rows: {Count(covenant.OutboxRows)}; "
            + $"search documents: {Count(covenant.SearchDocuments)}.");

        write($"  Campaigns affected: {Count(covenant.AffectedCampaigns)}");

        if (covenant.RetainsCampaignMask)
        {

            write("  This Campaign's mask of the Global key is kept: it is policy about the Global entry, not content of this one.");

        }

        if (covenant.GlobalConfirmedResurfaces)
        {

            write("  The Global entry for this key starts applying in this Campaign again.");

        }

    }

    private static string LocalOutcome(MemoryErasureLocalResultDto local)
    {

        switch (local.Outcome)
        {

            case MemoryLocalErasureOutcome.Verified:

                return "verified.";

            case MemoryLocalErasureOutcome.RowsRemovedScrubPending:

                string reasons = string.Join(", ", local.PendingReasons.Select(PendingReasonText));

                MemoryErasureScrubPendingReason[] permanent =
                    [.. local.PendingReasons.Where(static reason => reason is not MemoryErasureScrubPendingReason.WalCheckpointPending)];

                bool checkpoint = local.PendingReasons.Contains(MemoryErasureScrubPendingReason.WalCheckpointPending);

                // A scrub clears only the log checkpoint. Sending an operator to it for a reason it can
                // never clear would promise a verification that cannot happen.
                string remedy = (checkpoint, permanent.Length) switch
                {
                    (true, 0) => $" Run '{ScrubCommand}' to finish.",
                    (true, _) => $" Run '{ScrubCommand}' to clear the WAL checkpoint; no scrub can clear "
                        + $"{string.Join(", ", permanent.Select(PendingReasonText))}.",
                    (false, _) => " No scrub can clear this.",
                };

                return $"rows removed; scrub pending ({reasons}).{remedy}";

            default:

                throw new ArgumentOutOfRangeException(nameof(local), local.Outcome, "No text exists for this local erasure outcome.");

        }

    }

    private static string PendingReasonText(MemoryErasureScrubPendingReason reason) =>
        reason switch
        {
            MemoryErasureScrubPendingReason.WalCheckpointPending => "WAL checkpoint pending",
            MemoryErasureScrubPendingReason.FullTextSecureDeleteUnverified => "full-text secure delete unverified",
            MemoryErasureScrubPendingReason.VectorIndexScrubUnverified => "vector index scrub unverified",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "No text exists for this scrub-pending reason."),
        };

    private static string? CheckpointAttemptText(MemoryErasureWalCheckpointAttempt attempt) =>
        attempt switch
        {
            MemoryErasureWalCheckpointAttempt.Truncated or MemoryErasureWalCheckpointAttempt.NotAttempted => null,
            MemoryErasureWalCheckpointAttempt.Busy => "The WAL checkpoint was busy; a reader still held the log.",
            MemoryErasureWalCheckpointAttempt.Unavailable => "The WAL checkpoint could not open its connection.",
            _ => throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "No text exists for this checkpoint attempt."),
        };

    private static string RevocationText(MemoryExternalRevocation revocation) =>
        revocation switch
        {
            MemoryExternalRevocation.NotPerformed => "Nothing outside this machine was revoked.",
            _ => throw new ArgumentOutOfRangeException(nameof(revocation), revocation, "No text exists for this revocation value."),
        };

    private static string NoteText(MemoryErasureNote note) =>
        note switch
        {
            MemoryErasureNote.GlobalKeyStillProposableInCampaigns =>
                "This erases the Global entry only: agents may still propose the key inside a Campaign.",
            MemoryErasureNote.UnresolvedScopeStopsMatchingOnResolution =>
                "This memory's Campaign is unresolved, so its erasure fingerprint stops matching once that Session's Campaign is resolved.",
            MemoryErasureNote.OtherScopesUnaffected =>
                "Other scopes are unaffected: the same content, name or key in another scope is a different item and is kept.",
            MemoryErasureNote.CovenantDrainsInFlightTurns => DrainSentence,
            _ => throw new ArgumentOutOfRangeException(nameof(note), note, "No text exists for this erasure note."),
        };

    private static string RetainedCopyText(MemoryRetainedLocalCopy copy) =>
        copy switch
        {
            MemoryRetainedLocalCopy.SessionTranscripts => "Session transcripts, which may quote it",
            MemoryRetainedLocalCopy.SearchAndSummaryDerivatives => "search indexes, embeddings, summaries and Tapestry nodes derived from Sessions",
            MemoryRetainedLocalCopy.Attachments => "the attachment sources it may have been derived from",
            MemoryRetainedLocalCopy.ResponseCaches => "cached inference responses",
            MemoryRetainedLocalCopy.ApplicationLogs => "application logs",
            MemoryRetainedLocalCopy.AuditLog => "the inference audit log",
            MemoryRetainedLocalCopy.BackupArchives => "backup archives already written",
            MemoryRetainedLocalCopy.OtherLocalState => "other local state: Apprentice and batch checkpoints, batch files, and workspace files an agent wrote",
            _ => throw new ArgumentOutOfRangeException(nameof(copy), copy, "No text exists for this retained local copy."),
        };

    private static string Count(long value) => value.ToString(CultureInfo.InvariantCulture);

}
