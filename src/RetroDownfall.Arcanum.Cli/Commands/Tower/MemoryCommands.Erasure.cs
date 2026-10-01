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
/// The operator's erase verbs over one Saga memory and one Lexicon entry.
/// </summary>
/// <remarks>
/// Each verb runs the same five steps: show, prepare, render the host's plan and what local erasure
/// cannot revoke, confirm, apply. The target is read off <c>show</c> and forwarded unchanged, so the
/// operator never transcribes a hash or a version identity, and the show response itself is never
/// printed — the plan names counts and states, never the content being erased.
///
/// <para>The mutation identity is generated once, before prepare, and the apply carries exactly the
/// prepared fields plus the token. That identity is what lets the host answer a repeated apply from its
/// receipt rather than erasing twice.</para>
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

            return WriteError(shown.Error);

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

            return WriteLexiconInputError(invalid.Error.Message);

        }

        Result<LexiconEntryDetail> shown = await apiClient
            .ShowLexiconAsync(new(name, scope), cancellationToken)
            .ConfigureAwait(false);

        if (shown.IsFailure)
        {

            return WriteError(shown.Error);

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

            return WriteError(prepared.Error);

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

            int exitCode = WriteError(result.Error);

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
    /// The shared disclosure writer, built from this command's own dispatcher and settings.
    /// </summary>
    /// <remarks>
    /// Built here rather than injected: the writer is internal and this class is public, so a
    /// constructor parameter of the writer's type would not compile.
    /// </remarks>
    private CovenantExternalRetentionDisclosureWriter DisclosureWriter => new(dispatcher, settings);

}
