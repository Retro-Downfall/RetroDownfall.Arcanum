using System.Globalization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Memory;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Renders an erase's server-measured plan before the question, and its result after; and the same
/// for a release, the erasure status, a scrub, and a key reset.
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

    /// <summary>The verb that discards evidence the current key cannot verify.</summary>
    private const string ResetKeyCommand = "arcanum memory erasure reset-key";

    /// <summary>What lifting a fingerprint gives back to the writers it was refusing.</summary>
    internal const string RelearnAfterRelease = "Agents and extraction may write this again once it is released.";

    /// <summary>What discarding unverifiable evidence gives back to the writers it was refusing.</summary>
    private const string RelearnAfterReset =
        "Discarding them means the erasures they recorded may be learned again by extraction or agent writes.";

    /// <summary>
    /// What an operator is told when the host holds erasure fingerprints it could not check, so whether
    /// a write released one is unknown: the command that can say is named rather than guessed at.
    /// </summary>
    internal const string UncheckedFingerprints =
        "Erasure fingerprints could not be checked; run 'arcanum memory erasure status'.";

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

    /// <summary>
    /// Says, before the question, what a release lifts and that lifting it lets the writers back in.
    /// </summary>
    /// <remarks>
    /// <paramref name="identity"/> is the caller's description of the identity: what the operator typed
    /// for a name or key, and only the kind of identity for Saga content, which is never printed. The
    /// warning goes to the diagnostic stream in every mode, as the erase warnings do.
    /// </remarks>
    public static void WriteReleasePlan(
        IConsoleDispatcher dispatcher,
        MemoryReviewStore store,
        string scope,
        string identity,
        bool json)
    {

        ArgumentNullException.ThrowIfNull(dispatcher);

        Action<string> write = json ? dispatcher.WriteDiagnostic : dispatcher.WritePayload;

        write($"{store} erasure release, {scope} scope:");

        write($"  Identity: {identity}");

        write("  Only this identity's fingerprint in this exact scope is deleted; its erasure receipts are kept.");

        dispatcher.WriteDiagnostic(RelearnAfterRelease);

    }

    public static void WriteReleaseResult(
        IConsoleDispatcher dispatcher,
        MemoryErasureReleaseResultDto result)
    {

        ArgumentNullException.ThrowIfNull(dispatcher);

        ArgumentNullException.ThrowIfNull(result);

        dispatcher.WritePayload(result.Outcome switch
        {
            MemoryErasureReleaseOutcome.Released =>
                $"Released {Plural(result.ReleasedCount, "erasure fingerprint", "erasure fingerprints")}.",
            MemoryErasureReleaseOutcome.NotFingerprinted =>
                "No erasure fingerprint matched, so nothing was released.",
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "No text exists for this release outcome."),
        });

    }

    /// <summary>
    /// Renders the key state and each store's counts, then what the operator can do about them.
    /// </summary>
    /// <remarks>
    /// Counts and states only: the status carries no content, no key material and no key identity.
    /// </remarks>
    public static void WriteStatus(
        IConsoleDispatcher dispatcher,
        MemoryErasureStatusDto status)
    {

        ArgumentNullException.ThrowIfNull(dispatcher);

        ArgumentNullException.ThrowIfNull(status);

        dispatcher.WritePayload($"Erasure key: {status.KeyStatus}. {KeyStatusText(status.KeyStatus)}");

        WriteStoreCounts(dispatcher.WritePayload, status.Stores);

        dispatcher.WritePayload(status.PendingScrubReceipts == 0
            ? "Receipts pending scrub: 0."
            : $"Receipts pending scrub: {Count(status.PendingScrubReceipts)}. '{ScrubCommand}' retries the log checkpoint; "
                + "a full-text or vector-index reason no scrub can clear stays pending.");

        if (status.KeyStatus is MemoryErasureKeyStatus.Unavailable)
        {

            dispatcher.WritePayload("Unverifiable counts are unknown while the erasure key cannot be read.");

        }

        if (status.KeyStatus is MemoryErasureKeyStatus.Lost
            || status.Stores.Any(static store => store.Unverifiable > 0))
        {

            dispatcher.WritePayload(
                $"Run '{ResetKeyCommand}' to review and discard the evidence the current key cannot verify; "
                + "the erasures it recorded may then be learned again.");

        }

    }

    public static void WriteScrubResult(
        IConsoleDispatcher dispatcher,
        MemoryErasureScrubResultDto result)
    {

        ArgumentNullException.ThrowIfNull(dispatcher);

        ArgumentNullException.ThrowIfNull(result);

        string attempt = result.WalCheckpointAttempt switch
        {
            MemoryErasureWalCheckpointAttempt.Truncated => "the log checkpoint truncated",
            MemoryErasureWalCheckpointAttempt.Busy => "the log checkpoint was busy because a reader still held the log, so run it again once readers finish",
            MemoryErasureWalCheckpointAttempt.Unavailable => "the log checkpoint could not open its connection",
            MemoryErasureWalCheckpointAttempt.NotAttempted => "nothing was pending on the log",
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.WalCheckpointAttempt, "No text exists for this checkpoint attempt."),
        };

        dispatcher.WritePayload(
            $"Scrub: {attempt}. Verified {Count(result.Verified)}, still pending {Count(result.StillPending)}.");

    }

    /// <summary>
    /// Renders what a key reset found and what applying it costs, before the question.
    /// </summary>
    /// <remarks>
    /// The measurement follows the mode, as an erase plan does; the cost — erasures that may be learned
    /// again — is a warning and goes to the diagnostic stream in every mode.
    /// </remarks>
    public static void WriteKeyResetPreflight(
        IConsoleDispatcher dispatcher,
        MemoryErasureKeyResetPreflightDto preflight,
        bool json)
    {

        ArgumentNullException.ThrowIfNull(dispatcher);

        ArgumentNullException.ThrowIfNull(preflight);

        Action<string> write = json ? dispatcher.WriteDiagnostic : dispatcher.WritePayload;

        write($"Erasure key: {preflight.KeyStatus}. {KeyResetKeyText(preflight.KeyStatus)}");

        WriteStoreCounts(write, preflight.Stores);

        write("Applying discards every fingerprint and receipt the current key did not record, and nothing it did.");

        write($"This preview expires {preflight.ExpiresAtUtc.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)}.");

        dispatcher.WriteDiagnostic(RelearnAfterReset);

    }

    public static void WriteKeyResetResult(
        IConsoleDispatcher dispatcher,
        MemoryErasureKeyResetResultDto result)
    {

        ArgumentNullException.ThrowIfNull(dispatcher);

        ArgumentNullException.ThrowIfNull(result);

        dispatcher.WritePayload(
            $"Discarded {Plural(result.FingerprintsDiscarded, "fingerprint", "fingerprints")} and "
            + $"{Plural(result.ReceiptsDiscarded, "receipt", "receipts")}"
            + (result.KeyCreated ? "; created a new erasure key." : "."));

    }

    /// <summary>
    /// Tells the operator a release, scrub or key reset may have been applied although the host never
    /// confirmed it, and what to run to find out.
    /// </summary>
    /// <remarks>
    /// The same rule as <see cref="WriteUnconfirmedApply"/>: written only when the call was sent and
    /// nothing proves it rolled back. All three are naturally idempotent, so the advice is to look, or to
    /// run the same command again, never to undo anything.
    /// </remarks>
    public static void WriteUnconfirmed(
        IConsoleDispatcher dispatcher,
        string operation,
        string next)
    {

        ArgumentNullException.ThrowIfNull(dispatcher);

        dispatcher.WriteDiagnostic($"The host did not confirm this {operation}, so it may have been applied. {next}");

    }

    private static void WriteStoreCounts(Action<string> write, IEnumerable<MemoryErasureStoreCountsDto> stores)
    {

        foreach (MemoryErasureStoreCountsDto store in stores)
        {

            write(
                $"  {store.Store}: {Plural(store.Fingerprints, "fingerprint", "fingerprints")}, "
                + $"{Count(store.Unverifiable)} unverifiable, {Plural(store.Receipts, "receipt", "receipts")}");

        }

    }

    private static string KeyStatusText(MemoryErasureKeyStatus status) =>
        status switch
        {
            MemoryErasureKeyStatus.Absent => "No key item exists and no fingerprint needs one; the first erase creates it.",
            MemoryErasureKeyStatus.Present => "The key is readable.",
            MemoryErasureKeyStatus.Lost =>
                "Fingerprints exist and the key item is gone, so nothing can verify them; extraction, the Lexicon "
                + "scribe and agent Covenant proposals stay refused until the evidence is reset.",
            MemoryErasureKeyStatus.Unavailable => "The credential store could not answer, or the stored item is not a valid key.",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No text exists for this key status."),
        };

    private static string KeyResetKeyText(MemoryErasureKeyStatus status) =>
        status switch
        {
            MemoryErasureKeyStatus.Present => "It is kept.",
            MemoryErasureKeyStatus.Absent or MemoryErasureKeyStatus.Lost => "Applying creates a new erasure key unless one exists by then.",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No text exists for this key status in a reset."),
        };

    private static string Plural(long value, string singular, string plural) =>
        $"{Count(value)} {(value == 1 ? singular : plural)}";

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
