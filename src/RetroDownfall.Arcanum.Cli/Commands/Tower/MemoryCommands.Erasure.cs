using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Lexicon;

using RetroDownfall.Arcanum.Core.Memory;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Cli.Commands.Tower;

/// <summary>
/// The operator's erase and release verbs over one Saga memory and one Lexicon entry, and the
/// <c>memory erasure</c> administration verbs: status, scrub and reset-key.
/// </summary>
/// <remarks>
/// Each erase runs the same five steps: show, prepare, render the host's plan and what local erasure
/// cannot revoke, confirm, apply. The target is read off <c>show</c> and forwarded unchanged, so the
/// operator never transcribes a hash or a version identity, and the show response itself is never
/// printed — the plan names counts and states, never the content being erased.
///
/// <para>The mutation identity is generated once, before prepare, and the apply carries exactly the
/// prepared fields plus the token. That identity is what lets the host answer a repeated apply from its
/// receipt rather than erasing twice.</para>
///
/// <para>A release names its identity directly — Saga content from a file or standard input, a name or
/// a key as typed — and confirms before it lifts anything, because lifting is the direction that lets
/// extraction and agents write the identity again. A key reset previews, confirms and applies the
/// preview's token; status and scrub ask nothing.</para>
/// </remarks>
public sealed partial class MemoryCommands
{

    /// <summary>
    /// Erases one Saga memory and every memory with byte-identical content in its scope.
    /// </summary>
    /// <remarks>
    /// <paramref name="expectedContentHash"/> is optional here, unlike on <c>correct</c>: when omitted,
    /// the hash <c>show</c> just reported is sent, which still binds the erase to the text the host
    /// holds. Passing it binds the erase to the text the operator read earlier instead.
    /// </remarks>
    public async Task<int> SagaErase(
        string id,
        string? expectedContentHash,
        CancellationToken cancellationToken)
    {

        Result<SagaMemoryDetail> shown = await apiClient
            .ShowSagaMemoryAsync(id, cancellationToken)
            .ConfigureAwait(false);

        if (shown.IsFailure)
        {

            return WriteErasureError(shown.Error);

        }

        Guid mutationId = Guid.CreateVersion7();

        SagaErasePrepareRequest request = new(
            shown.Value.Memory.Id,
            expectedContentHash ?? shown.Value.ContentHash,
            shown.Value.Claim?.CurrentVersionId,
            mutationId);

        Result<MemoryErasurePreflightDto> prepared = await apiClient
            .PrepareSagaErasureAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return await ConfirmAndEraseAsync(
                MemoryReviewStore.Saga,
                mutationId,
                prepared,
                static plan => plan.ErasedItemCount == 1
                    ? "Erase this Saga memory in this scope? This cannot be undone."
                    : $"Erase {plan.ErasedItemCount} Saga memories with identical content in this scope? This cannot be undone.",
                preflight => apiClient.EraseSagaMemoryAsync(
                    new SagaEraseRequest(
                        request.MemoryId,
                        request.ExpectedContentHash,
                        request.ExpectedClaimVersionId,
                        request.MutationId,
                        preflight.PreflightToken),
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Erases one Lexicon entry in exactly one scope.
    /// </summary>
    /// <remarks>
    /// The scope is the one named: an omitted <c>--campaign</c> is exact Global and never falls back
    /// from a Campaign, and saved or active context is never read. The name is positional, as it is on
    /// every Lexicon verb, so it lands in shell history; the Command Reference says so.
    /// </remarks>
    public async Task<int> LexiconErase(
        string name,
        Guid? campaignId,
        CancellationToken cancellationToken)
    {

        LexiconCurationScope scope = ExactLexiconScope(campaignId);

        if (scope.Validate() is { IsFailure: true } invalid)
        {

            return WriteErasureInputError(invalid.Error.Message);

        }

        Result<LexiconEntryDetail> shown = await apiClient
            .ShowLexiconAsync(new(name, scope), cancellationToken)
            .ConfigureAwait(false);

        if (shown.IsFailure)
        {

            return WriteErasureError(shown.Error);

        }

        Guid mutationId = Guid.CreateVersion7();

        // Forward the measured target unchanged — lifecycle, Annals head and label evidence included.
        LexiconErasePrepareRequest request = new(shown.Value.Target, mutationId);

        Result<MemoryErasurePreflightDto> prepared = await apiClient
            .PrepareLexiconErasureAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return await ConfirmAndEraseAsync(
                MemoryReviewStore.Lexicon,
                mutationId,
                prepared,
                _ => $"Erase the Lexicon entry '{name}' in the {LexiconScopeText(scope)} scope? This cannot be undone.",
                preflight => apiClient.EraseLexiconEntryAsync(
                    new LexiconEraseRequest(request.Target, request.MutationId, preflight.PreflightToken),
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Renders the host's plan and the external disclosure, asks, then applies — or records the decline.
    /// </summary>
    /// <remarks>
    /// Everything an operator decides on is written before the question, on the diagnostic stream
    /// under <c>--json</c> so stdout stays the one document: the result, or the cancellation payload.
    /// <c>--yes</c> answers the question; it does not skip the disclosure, so an unattended run records
    /// what it was told. A non-interactive session without <c>--yes</c> is refused by the prompt itself,
    /// which the command tree maps to exit 2 having applied nothing.
    /// </remarks>
    private async Task<int> ConfirmAndEraseAsync(
        MemoryReviewStore store,
        Guid mutationId,
        Result<MemoryErasurePreflightDto> prepared,
        Func<MemoryErasurePlanDto, string> question,
        Func<MemoryErasurePreflightDto, Task<Result<MemoryErasureResultDto>>> apply,
        CancellationToken cancellationToken)
    {

        if (prepared.IsFailure)
        {

            return WriteErasureError(prepared.Error);

        }

        bool json = CliInvocationContext.Current.Json;

        MemoryErasureRenderer.WritePreflight(dispatcher, prepared.Value, json);

        DisclosureWriter.WriteErasure(prepared.Value.External);

        if (!CliInvocationContext.Current.Yes
            && !await confirmationPrompt
                .PromptForConfirmationAsync(question(prepared.Value.Plan), cancellationToken)
                .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic($"{store} erasure cancelled.");

            if (json)
            {

                dispatcher.WriteJson(
                    new MemoryErasureCancellationPayload("erase", store, mutationId, Cancelled: true),
                    CliJsonContext.Default.MemoryErasureCancellationPayload);

            }

            return (int)CliExitCode.Success;

        }

        // A cancellation that lands before the apply is sent cancels an erase that never started, and
        // must not claim otherwise.
        cancellationToken.ThrowIfCancellationRequested();

        Result<MemoryErasureResultDto> result;

        try
        {

            result = await apply(prepared.Value).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            // Ctrl-C after the request went out: the exit stays the cancellation, and the operator is
            // told the erase may still have happened.
            MemoryErasureRenderer.WriteUnconfirmedApply(dispatcher, mutationId);

            throw;

        }

        if (result.IsFailure)
        {

            int exitCode = WriteErasureError(result.Error);

            if (ArcanumApiClient.ErasureOutcomeUnknown(result.Error))
            {

                MemoryErasureRenderer.WriteUnconfirmedApply(dispatcher, mutationId);

            }

            return exitCode;

        }

        if (json)
        {

            dispatcher.WriteJson(result.Value, ArcanumJsonContext.Default.MemoryErasureResultDto);

        }
        else
        {

            MemoryErasureRenderer.WriteResult(dispatcher, result.Value);

        }

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Releases the erasure fingerprint of one Saga content in one exact scope, so extraction may write
    /// that content there again.
    /// </summary>
    /// <remarks>
    /// The content arrives through <c>--file</c> or piped standard input, never as an argument, so the
    /// text an operator erased does not land in shell history on its way back. It is sent exactly as
    /// read: the host tries those bytes and their trimmed form, so a file's trailing newline still
    /// releases. With neither flag the scope is Global; <c>--campaign</c> alone is Campaign. Literal
    /// <c>--file -</c> needs <c>--yes</c> before standard input is read, because reading it to the end
    /// leaves no channel to ask on.
    /// </remarks>
    public async Task<int> SagaRelease(
        string file,
        Guid? campaignId,
        string? scope,
        CancellationToken cancellationToken)
    {

        Result<SagaMemoryScopeKind> scopeKind = SagaReleaseScope(scope, campaignId);

        if (scopeKind.IsFailure)
        {

            return WriteErasureInputError(scopeKind.Error.Message);

        }

        if (string.IsNullOrWhiteSpace(file))
        {

            return WriteErasureInputError("Saga release requires --file <path|->.");

        }

        if (file == "-" && !CliInvocationContext.Current.Yes)
        {

            return WriteErasureInputError("Saga release with --file - requires --yes before reading standard input.");

        }

        Result<string> content = await AuthoredContentReader
            .ReadAsync(file, "Saga", emptyContentRemedy: null, cancellationToken)
            .ConfigureAwait(false);

        if (content.IsFailure)
        {

            return WriteErasureInputError(content.Error.Message);

        }

        SagaErasureReleaseRequest request = new(scopeKind.Value, campaignId, content.Value);

        string scopeText = SagaScopeText(scopeKind.Value, campaignId);

        return await ConfirmAndReleaseAsync(
                MemoryReviewStore.Saga,
                scopeText,
                "the exact content read from the file, which is not shown; its trimmed form is tried too",
                $"Release the Saga erasure fingerprint for this content in the {scopeText} scope?",
                onResent => apiClient.ReleaseSagaErasureAsync(request, cancellationToken, onResent),
                cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Releases the erasure fingerprint of one Lexicon name in exactly one scope, so extraction and
    /// agents may write that name there again.
    /// </summary>
    /// <remarks>
    /// The scope is the one named, as on <c>erase</c>: an omitted <c>--campaign</c> is exact Global. The
    /// name is positional and sent as typed; the host trims and case-folds it the way the scribe does.
    /// </remarks>
    public async Task<int> LexiconRelease(
        string name,
        Guid? campaignId,
        CancellationToken cancellationToken)
    {

        LexiconCurationScope scope = ExactLexiconScope(campaignId);

        if (scope.Validate() is { IsFailure: true } invalid)
        {

            return WriteErasureInputError(invalid.Error.Message);

        }

        LexiconErasureReleaseRequest request = new(scope, name);

        string scopeText = LexiconScopeText(scope);

        return await ConfirmAndReleaseAsync(
                MemoryReviewStore.Lexicon,
                scopeText,
                $"the Lexicon name '{name}', trimmed and case-folded as the scribe reads it",
                $"Release the Lexicon erasure fingerprint for '{name}' in the {scopeText} scope?",
                onResent => apiClient.ReleaseLexiconErasureAsync(request, cancellationToken, onResent),
                cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Reports the erasure key's state and each store's fingerprint, unverifiable and receipt counts.
    /// </summary>
    /// <remarks>
    /// Read-only. The host's answer carries counts and a state, never content or key material, and under
    /// <c>--json</c> it is the one document on stdout.
    /// </remarks>
    public async Task<int> ErasureStatus(CancellationToken cancellationToken)
    {

        Result<MemoryErasureStatusDto> status = await apiClient
            .GetMemoryErasureStatusAsync(cancellationToken)
            .ConfigureAwait(false);

        if (status.IsFailure)
        {

            return WriteErasureError(status.Error);

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(status.Value, ArcanumJsonContext.Default.MemoryErasureStatusDto);

        }
        else
        {

            MemoryErasureRenderer.WriteStatus(dispatcher, status.Value);

        }

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Retries the write-ahead-log checkpoint erasures left pending, and reports what it verified.
    /// </summary>
    /// <remarks>
    /// Asks nothing first: a scrub removes no evidence and changes no content, it only finishes making
    /// already-deleted rows unrecoverable from the log.
    /// </remarks>
    public async Task<int> ErasureScrub(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        bool resent = false;

        Result<MemoryErasureScrubResultDto> scrubbed;

        try
        {

            scrubbed = await apiClient
                .ScrubMemoryErasuresAsync(cancellationToken, () => resent = true)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            WriteUnconfirmedScrub();

            throw;

        }

        if (scrubbed.IsFailure)
        {

            int exitCode = WriteErasureError(scrubbed.Error);

            if (ArcanumApiClient.ErasureOutcomeUnknown(scrubbed.Error))
            {

                WriteUnconfirmedScrub();

            }

            WriteResentNote(resent, "scrub");

            return exitCode;

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(scrubbed.Value, ArcanumJsonContext.Default.MemoryErasureScrubResultDto);

        }
        else
        {

            MemoryErasureRenderer.WriteScrubResult(dispatcher, scrubbed.Value);

        }

        WriteResentNote(resent, "scrub");

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Discards the erasure evidence the current key cannot verify, creating a key when none exists.
    /// </summary>
    /// <remarks>
    /// Irreversible, so it always previews first: the host measures the key state and each store's
    /// counts and issues a short-lived token binding them, the CLI shows that measurement and what
    /// discarding it costs, and only an approval sends the token back unchanged. A refused prepare stops
    /// before the question. Nothing here reads or writes a keychain item; the host does both.
    /// </remarks>
    public async Task<int> ErasureResetKey(CancellationToken cancellationToken)
    {

        Result<MemoryErasureKeyResetPreflightDto> prepared = await apiClient
            .PrepareMemoryErasureKeyResetAsync(cancellationToken)
            .ConfigureAwait(false);

        if (prepared.IsFailure)
        {

            return WriteKeyResetError(prepared.Error);

        }

        bool json = CliInvocationContext.Current.Json;

        MemoryErasureRenderer.WriteKeyResetPreflight(dispatcher, prepared.Value, json);

        long unverifiable = prepared.Value.Stores.Sum(static store => store.Unverifiable);

        if (!CliInvocationContext.Current.Yes
            && !await confirmationPrompt
                .PromptForConfirmationAsync(
                    $"Reset the erasure evidence, discarding {unverifiable} unverifiable fingerprint(s) and every receipt the current key did not record? This cannot be undone.",
                    cancellationToken)
                .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Erasure key reset cancelled; nothing was discarded.");

            if (json)
            {

                dispatcher.WriteJson(
                    new MemoryErasureCancellationPayload("reset-key", Store: null, MutationId: null, Cancelled: true),
                    CliJsonContext.Default.MemoryErasureCancellationPayload);

            }

            return (int)CliExitCode.Success;

        }

        cancellationToken.ThrowIfCancellationRequested();

        bool resent = false;

        Result<MemoryErasureKeyResetResultDto> reset;

        try
        {

            reset = await apiClient
                .ResetMemoryErasureKeyAsync(
                    new MemoryErasureKeyResetRequest(prepared.Value.PreflightToken),
                    cancellationToken,
                    () => resent = true)
                .ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            WriteUnconfirmedKeyReset();

            throw;

        }

        if (reset.IsFailure)
        {

            int exitCode = WriteKeyResetError(reset.Error);

            if (ArcanumApiClient.ErasureOutcomeUnknown(reset.Error))
            {

                WriteUnconfirmedKeyReset();

            }

            WriteResentNote(resent, "key reset");

            return exitCode;

        }

        if (json)
        {

            dispatcher.WriteJson(reset.Value, ArcanumJsonContext.Default.MemoryErasureKeyResetResultDto);

        }
        else
        {

            MemoryErasureRenderer.WriteKeyResetResult(dispatcher, reset.Value);

        }

        WriteResentNote(resent, "key reset");

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Shows what a release lifts and its cost, asks, then releases — or records the decline.
    /// </summary>
    /// <remarks>
    /// Release is the unsafe direction: it lets extraction and agents write the identity again. So the
    /// warning is written before the question in every mode, <c>--yes</c> answers the question without
    /// skipping it, and a non-interactive session without <c>--yes</c> is refused by the prompt itself.
    /// </remarks>
    private async Task<int> ConfirmAndReleaseAsync(
        MemoryReviewStore store,
        string scope,
        string identity,
        string question,
        Func<Action, Task<Result<MemoryErasureReleaseResultDto>>> release,
        CancellationToken cancellationToken)
    {

        bool json = CliInvocationContext.Current.Json;

        MemoryErasureRenderer.WriteReleasePlan(dispatcher, store, scope, identity, json);

        if (!CliInvocationContext.Current.Yes
            && !await confirmationPrompt
                .PromptForConfirmationAsync(question, cancellationToken)
                .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic($"{store} release cancelled; nothing was released.");

            if (json)
            {

                dispatcher.WriteJson(
                    new MemoryErasureCancellationPayload("release", store, MutationId: null, Cancelled: true),
                    CliJsonContext.Default.MemoryErasureCancellationPayload);

            }

            return (int)CliExitCode.Success;

        }

        cancellationToken.ThrowIfCancellationRequested();

        bool resent = false;

        Result<MemoryErasureReleaseResultDto> released;

        try
        {

            released = await release(() => resent = true).ConfigureAwait(false);

        }
        catch (OperationCanceledException)
        {

            WriteUnconfirmedRelease(dispatcher);

            throw;

        }

        if (released.IsFailure)
        {

            int exitCode = WriteErasureError(released.Error);

            WriteReleaseRefusalGuidance(dispatcher, released.Error);

            WriteResentNote(resent, "release");

            return exitCode;

        }

        if (json)
        {

            dispatcher.WriteJson(released.Value, ArcanumJsonContext.Default.MemoryErasureReleaseResultDto);

        }
        else
        {

            MemoryErasureRenderer.WriteReleaseResult(dispatcher, released.Value);

        }

        WriteResentNote(resent, "release");

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// What follows a refused release: the may-have-applied note when nothing proves the release rolled back.
    /// </summary>
    /// <remarks>
    /// A lost-key refusal needs nothing added here: every host <c>KeyLost</c> message already ends with the
    /// pointer to the status verb, so a line of the CLI's own would only print it twice.
    /// </remarks>
    internal static void WriteReleaseRefusalGuidance(IConsoleDispatcher dispatcher, Error error)
    {

        if (ArcanumApiClient.ErasureOutcomeUnknown(error))
        {

            WriteUnconfirmedRelease(dispatcher);

        }

    }

    /// <summary>Says a resend happened, so the outcome just reported describes only the resend.</summary>
    private void WriteResentNote(bool resent, string operation)
    {

        if (resent)
        {

            MemoryErasureRenderer.WriteResent(dispatcher, operation);

        }

    }

    internal static void WriteUnconfirmedRelease(IConsoleDispatcher dispatcher) =>
        MemoryErasureRenderer.WriteUnconfirmed(
            dispatcher,
            "release",
            "Releasing again is safe: it reports whether a fingerprint remains.");

    private void WriteUnconfirmedScrub() =>
        MemoryErasureRenderer.WriteUnconfirmed(
            dispatcher,
            "scrub",
            "Run 'arcanum memory erasure status' to see which receipts are still pending.");

    private void WriteUnconfirmedKeyReset() =>
        MemoryErasureRenderer.WriteUnconfirmed(
            dispatcher,
            "key reset",
            "Run 'arcanum memory erasure status' before resetting again.");

    /// <summary>
    /// Writes a refused key reset: for the three refusals an operator can act on, the CLI's own
    /// explanation in place of the host's message, and otherwise the host's message.
    /// </summary>
    /// <remarks>
    /// Each of the three proves nothing was discarded: the host refuses before its delete commits. A
    /// stale plan can still follow the key an apply created before it re-measured, so its text says so.
    /// The explanation replaces the host's message rather than following it, because the host's says the
    /// same thing less exactly, and the key remedy must be read with its caveats, once.
    /// </remarks>
    private int WriteKeyResetError(Error error)
    {

        string? explanation = error.Code switch
        {
            ErrorCodes.MemoryErasure.StalePlan =>
                "The erasure key's state or a store's counts changed after the preview, so nothing was discarded, "
                + "though a new erasure key may have been created. Run 'arcanum memory erasure reset-key' again to review them.",
            ErrorCodes.MemoryErasure.InvalidPreflight =>
                "The preview is no longer valid: it expired, or the host restarted after issuing it. Nothing was "
                + "discarded. Run 'arcanum memory erasure reset-key' again.",
            ErrorCodes.MemoryErasure.KeyUnavailable =>
                "The erasure key could not be read, or the stored item is not a valid key, so nothing was discarded "
                + $"and no key was written. {MemoryErasureRenderer.KeyUnavailableRemedy}",
            _ => null,
        };

        return WriteErasureError(explanation is null ? error : error with { Message = explanation });

    }

    /// <summary>
    /// The exact Saga scope a release names, from <c>--scope</c> and <c>--campaign</c>.
    /// </summary>
    /// <remarks>
    /// Neither flag is Global and <c>--campaign</c> alone is Campaign. <c>--scope campaign</c> needs a
    /// Campaign to name, and any other scope names none, so combining it with <c>--campaign</c> is refused
    /// rather than resolved one way or the other.
    /// </remarks>
    private static Result<SagaMemoryScopeKind> SagaReleaseScope(string? scope, Guid? campaignId)
    {

        if (campaignId == Guid.Empty)
        {

            return InvalidSagaReleaseScope("--campaign must be a nonempty Campaign GUID.");

        }

        if (scope is null)
        {

            return Result<SagaMemoryScopeKind>.Success(
                campaignId is null ? SagaMemoryScopeKind.Global : SagaMemoryScopeKind.Campaign);

        }

        SagaMemoryScopeKind? named = scope.Trim().ToLowerInvariant() switch
        {
            "global" => SagaMemoryScopeKind.Global,
            "campaign" => SagaMemoryScopeKind.Campaign,
            "unresolved" => SagaMemoryScopeKind.LegacyUnresolved,
            "unclassified" => SagaMemoryScopeKind.Unclassified,
            _ => null,
        };

        if (named is not { } kind)
        {

            return InvalidSagaReleaseScope("--scope must be global|campaign|unresolved|unclassified.");

        }

        if (kind is SagaMemoryScopeKind.Campaign)
        {

            return campaignId is null
                ? InvalidSagaReleaseScope("--scope campaign requires --campaign <guid>.")
                : Result<SagaMemoryScopeKind>.Success(kind);

        }

        return campaignId is null
            ? Result<SagaMemoryScopeKind>.Success(kind)
            : InvalidSagaReleaseScope($"--campaign names a Campaign scope, so it cannot be combined with --scope {scope}.");

    }

    private static Result<SagaMemoryScopeKind> InvalidSagaReleaseScope(string message) =>
        Result<SagaMemoryScopeKind>.Failure(new Error(ErrorCodes.Validation.InvalidBody, message));

    private static string SagaScopeText(SagaMemoryScopeKind scope, Guid? campaignId) =>
        scope switch
        {
            SagaMemoryScopeKind.Global => "Global",
            SagaMemoryScopeKind.Campaign => $"Campaign {campaignId:D}",
            SagaMemoryScopeKind.LegacyUnresolved => "legacy unresolved",
            SagaMemoryScopeKind.Unclassified => "unclassified",
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "No text exists for this Saga scope."),
        };

    /// <summary>
    /// Writes a refusal of the command line itself, before any request, with exit 2.
    /// </summary>
    private int WriteErasureInputError(string message) =>
        WriteErasureFailure(message, (int)CliExitCode.ConfigurationError);

    /// <summary>
    /// Writes a host refusal or a transport failure with its classified exit code.
    /// </summary>
    private int WriteErasureError(Error error) =>
        WriteErasureFailure(error.Message, CliFailureExit.ExitCode(error));

    /// <summary>
    /// The one failure shape every erase, release and erasure-administration verb shares: the message on
    /// the diagnostic stream, and under <c>--json</c> the CLI error envelope as the one stdout document,
    /// as every other direct verb's refusal writes it.
    /// </summary>
    private int WriteErasureFailure(string message, int exitCode)
    {

        dispatcher.WriteDiagnostic(message);

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(
                new CliErrorPayload(message, exitCode),
                CliJsonContext.Default.CliErrorPayload);

        }

        return exitCode;

    }

    /// <summary>
    /// The shared disclosure writer, built from this command's own dispatcher and settings.
    /// </summary>
    /// <remarks>
    /// Built here rather than injected: the writer is internal and this class is public, so a
    /// constructor parameter of the writer's type would not compile.
    /// </remarks>
    private CovenantExternalRetentionDisclosureWriter DisclosureWriter => new(dispatcher, settings);

}
