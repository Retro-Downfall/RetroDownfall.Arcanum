using System.Globalization;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Api.Tower;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Cli.Commands.Tower;

/// <summary>
/// The operator's curation verbs over one Saga memory: show it, correct it, retire it, reinstate it,
/// pin it, unpin it.
/// </summary>
/// <remarks>
/// Every verb names Saga in what it asks and in what it reports, because an operator reaching for
/// "retire this" needs to know which of several memory stores they just changed. The sentence each
/// result carries says so and says that nothing else moved.
///
/// <para>The four write verbs render <see cref="SagaCurationResult.Outcome"/> rather than a single
/// success line. Retiring a memory that is already retired and reinstating one that is not retired
/// write nothing and are still successes — the operator asked for a state and the memory is in it — so
/// the distinction between "this call did it" and "it was already so" survives only if the rendering
/// keeps it. Correcting a retired memory is the case that is refused instead, and the refusal comes
/// from the host with its own message rather than being restated here.</para>
/// </remarks>
public sealed partial class MemoryCommands
{

    /// <summary>
    /// Shows one Saga memory: what it says, the digest that proves what was read, and what has been
    /// decided about it.
    /// </summary>
    /// <remarks>
    /// The content hash is printed because it is the value <c>correct</c>, <c>retire</c>, and
    /// <c>reinstate</c> require. It is the host's own digest, carried on the projection and rendered
    /// verbatim; computing it here would agree with the host only for as long as both sides hashed the
    /// same bytes, and nothing in this process could notice when they stopped.
    /// </remarks>
    public async Task<int> SagaShow(
        string id,
        CancellationToken cancellationToken)
    {

        Result<SagaMemoryDetail> result = await apiClient
            .ShowSagaMemoryAsync(id, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(result.Value, ArcanumJsonContext.Default.SagaMemoryDetail);

            return (int)CliExitCode.Success;

        }

        WriteSagaDetail(result.Value);

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Replaces the text of one Saga memory, naming the exact content being corrected.
    /// </summary>
    /// <remarks>
    /// The replacement text arrives through <c>--file</c> or piped standard input, never as an
    /// argument, for the reason <see cref="AuthoredContentReader"/> states.
    /// </remarks>
    public async Task<int> SagaCorrect(
        string id,
        string expectedContentHash,
        string? file,
        CancellationToken cancellationToken)
    {

        Result<string> content = await AuthoredContentReader
            .ReadAsync(file, "Saga memory", cancellationToken)
            .ConfigureAwait(false);

        if (content.IsFailure)
        {

            dispatcher.WriteDiagnostic(content.Error.Message);

            return (int)CliExitCode.ConfigurationError;

        }

        if (!await confirmationPrompt
            .PromptForConfirmationAsync(
                $"Correct Saga memory '{id}'? This changes Saga only; no other memory store is touched.",
                cancellationToken)
            .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Saga correction cancelled.");

            return (int)CliExitCode.Success;

        }

        Result<SagaCurationResult> result = await apiClient
            .CorrectSagaMemoryAsync(
                id,
                new SagaCorrectRequest(expectedContentHash, content.Value),
                cancellationToken)
            .ConfigureAwait(false);

        return WriteCurationResult(
            result,
            id,
            static (outcome, memoryId) => outcome switch
            {

                SagaCurationOutcomeKind.Unchanged =>
                    $"Unchanged: Saga memory '{memoryId}' already holds that text, so nothing was written."
                    + " No other memory store was touched.",

                _ =>
                    $"Corrected Saga memory '{memoryId}'."
                    + " No other memory store was touched.",

            });

    }

    /// <summary>
    /// Takes one Saga memory out of retrieval, keeping it inspectable.
    /// </summary>
    public async Task<int> SagaRetire(
        string id,
        string expectedContentHash,
        CancellationToken cancellationToken)
    {

        if (!await confirmationPrompt
            .PromptForConfirmationAsync(
                $"Retire Saga memory '{id}' from retrieval? It stays inspectable, and no other memory store is touched.",
                cancellationToken)
            .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Saga retirement cancelled.");

            return (int)CliExitCode.Success;

        }

        Result<SagaCurationResult> result = await apiClient
            .RetireSagaMemoryAsync(id, new SagaRetireRequest(expectedContentHash), cancellationToken)
            .ConfigureAwait(false);

        return WriteCurationResult(
            result,
            id,
            static (outcome, memoryId) => outcome switch
            {

                SagaCurationOutcomeKind.AlreadyRetired =>
                    $"Already retired: Saga memory '{memoryId}' was out of retrieval before this call,"
                    + " so nothing was written. No other memory store was touched.",

                _ =>
                    $"Retired Saga memory '{memoryId}'. It stays inspectable and no longer reaches retrieval."
                    + " No other memory store was touched.",

            });

    }

    /// <summary>
    /// Puts a retired Saga memory back into retrieval.
    /// </summary>
    public async Task<int> SagaReinstate(
        string id,
        string expectedContentHash,
        CancellationToken cancellationToken)
    {

        if (!await confirmationPrompt
            .PromptForConfirmationAsync(
                $"Reinstate Saga memory '{id}' into retrieval? No other memory store is touched.",
                cancellationToken)
            .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Saga reinstatement cancelled.");

            return (int)CliExitCode.Success;

        }

        Result<SagaCurationResult> result = await apiClient
            .ReinstateSagaMemoryAsync(id, new SagaReinstateRequest(expectedContentHash), cancellationToken)
            .ConfigureAwait(false);

        return WriteCurationResult(
            result,
            id,
            static (outcome, memoryId) => outcome switch
            {

                SagaCurationOutcomeKind.NotRetired =>
                    $"Not retired: Saga memory '{memoryId}' was already reaching retrieval,"
                    + " so nothing was written. No other memory store was touched.",

                _ =>
                    $"Reinstated Saga memory '{memoryId}'. It reaches retrieval again."
                    + " No other memory store was touched.",

            });

    }

    /// <summary>
    /// Marks one Saga memory durable, so retention will not prune it.
    /// </summary>
    public async Task<int> SagaPin(
        string id,
        CancellationToken cancellationToken)
    {

        if (!await confirmationPrompt
            .PromptForConfirmationAsync(
                $"Pin Saga memory '{id}' so retention will not prune it? No other memory store is touched.",
                cancellationToken)
            .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Saga pin cancelled.");

            return (int)CliExitCode.Success;

        }

        Result<SagaCurationResult> result = await apiClient
            .PinSagaMemoryAsync(id, cancellationToken)
            .ConfigureAwait(false);

        return WriteCurationResult(
            result,
            id,
            static (_, memoryId) =>
                $"Pinned Saga memory '{memoryId}'. Retention will not prune it."
                + " No other memory store was touched.");

    }

    /// <summary>
    /// Releases a pin, so retention may prune this memory again.
    /// </summary>
    public async Task<int> SagaUnpin(
        string id,
        CancellationToken cancellationToken)
    {

        if (!await confirmationPrompt
            .PromptForConfirmationAsync(
                $"Release the pin on Saga memory '{id}', so retention may prune it again? No other memory store is touched.",
                cancellationToken)
            .ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Saga unpin cancelled.");

            return (int)CliExitCode.Success;

        }

        Result<SagaCurationResult> result = await apiClient
            .UnpinSagaMemoryAsync(id, cancellationToken)
            .ConfigureAwait(false);

        return WriteCurationResult(
            result,
            id,
            static (_, memoryId) =>
                $"Unpinned Saga memory '{memoryId}'. Retention may prune it again."
                + " No other memory store was touched.");

    }

    /// <summary>
    /// Renders one write verb's result, or its refusal.
    /// </summary>
    /// <remarks>
    /// <paramref name="describe"/> is the verb's own sentence for an outcome, because the same
    /// <see cref="SagaCurationOutcomeKind"/> means different things to different verbs and a shared
    /// phrasing would have to be vague enough to fit both. Under <c>--json</c> the outcome travels as
    /// its own field, so a machine reader does not depend on the prose at all.
    /// </remarks>
    private int WriteCurationResult(
        Result<SagaCurationResult> result,
        string id,
        Func<SagaCurationOutcomeKind, string, string> describe)
    {

        if (result.IsFailure)
        {

            return WriteError(result.Error);

        }

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(result.Value, ArcanumJsonContext.Default.SagaCurationResult);

            return (int)CliExitCode.Success;

        }

        dispatcher.WritePayload(describe(result.Value.Outcome, id));

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Prints one memory's text, the digest of that text, its lifecycle, and its claim history.
    /// </summary>
    private void WriteSagaDetail(SagaMemoryDetail detail)
    {

        dispatcher.WritePayload($"Saga memory '{detail.Memory.Id}'.");

        dispatcher.WritePayload($"  Retrieval:    {detail.Eligibility}");

        dispatcher.WritePayload(
            $"  Retired:      {Stamp(detail.Lifecycle.RetiredAtUtc) ?? "not retired"}");

        dispatcher.WritePayload(
            $"  Pinned:       {Stamp(detail.Lifecycle.PinnedAtUtc) ?? "not pinned"}");

        dispatcher.WritePayload($"  Content hash: {detail.ContentHash}");

        dispatcher.WritePayload($"  Created:      {Stamp(detail.Memory.CreatedAt)}");

        dispatcher.WritePayload($"  Scope:        {detail.Memory.ScopeKind}");

        if (detail.Memory.Source is { Length: > 0 } source)
        {

            dispatcher.WritePayload($"  Source:       {source}");

        }

        dispatcher.WritePayload("Content:");

        dispatcher.WritePayload(detail.Memory.Content);

        WriteSagaProvenance(detail);

    }

    /// <summary>
    /// Prints the claim governing this memory and the versions behind it, when the Annals holds one.
    /// </summary>
    /// <remarks>
    /// A memory with no claim prints a line saying so rather than nothing at all: silence here reads as
    /// "this memory has never been curated", which is a different statement from "this installation is
    /// not recording claims".
    /// </remarks>
    private void WriteSagaProvenance(SagaMemoryDetail detail)
    {

        if (detail.Claim is not { } claim)
        {

            dispatcher.WritePayload("Provenance:   no claim is recorded for this memory.");

            return;

        }

        dispatcher.WritePayload(
            $"Provenance:   claim '{claim.ClaimId}' at revision "
            + claim.CurrentRevision.ToString(CultureInfo.InvariantCulture)
            + $", last operation {claim.CurrentOperation}.");

        foreach (AnnalClaimVersion version in detail.History)
        {

            dispatcher.WritePayload(
                "  revision "
                + version.Revision.ToString(CultureInfo.InvariantCulture)
                + $"  {version.Operation}  {version.Origin}  recorded {Stamp(version.RecordedAtUtc)}");

        }

    }

    private static string? Stamp(DateTimeOffset? instant) =>
        instant?.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);

}
