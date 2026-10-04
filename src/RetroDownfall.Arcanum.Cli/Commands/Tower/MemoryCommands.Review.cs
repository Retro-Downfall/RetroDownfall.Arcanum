using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Cli.Commands.Tower;

public sealed partial class MemoryCommands
{
    public async Task<int> SagaReviewList(
        Guid? campaignId,
        bool unresolved,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (campaignId is not null && unresolved)
        {
            return ReviewInputError("--campaign and --unresolved cannot be combined.");
        }

        SagaReviewListRequest request = new(
            unresolved
                ? SagaMemoryScopeKind.LegacyUnresolved
                : campaignId is null ? SagaMemoryScopeKind.Global : SagaMemoryScopeKind.Campaign,
            campaignId,
            limit,
            cursor);

        if (request.Validate() is { IsFailure: true } invalid)
        {
            return ReviewInputError(invalid.Error.Message);
        }

        Result<SagaReviewPageDto> result = await apiClient
            .ListSagaReviewAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return WriteError(result.Error);
        }

        if (CliInvocationContext.Current.Json)
        {
            dispatcher.WriteJson(result.Value, ArcanumJsonContext.Default.SagaReviewPageDto);
        }
        else
        {
            WriteSagaReviewPage(result.Value);
        }

        return (int)CliExitCode.Success;
    }

    public async Task<int> LexiconReviewList(
        Guid? campaignId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        LexiconReviewListRequest request = new(
            ExactLexiconScope(campaignId),
            limit,
            cursor);

        if (request.Validate() is { IsFailure: true } invalid)
        {
            return ReviewInputError(invalid.Error.Message);
        }

        Result<LexiconReviewPageDto> result = await apiClient
            .ListLexiconReviewAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return WriteError(result.Error);
        }

        if (CliInvocationContext.Current.Json)
        {
            dispatcher.WriteJson(result.Value, ArcanumJsonContext.Default.LexiconReviewPageDto);
        }
        else
        {
            WriteLexiconReviewPage(result.Value);
        }

        return (int)CliExitCode.Success;
    }

    public async Task<int> CovenantReviewList(
        Guid? campaignId,
        string lane,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse(lane, ignoreCase: true, out CovenantLane parsedLane)
            || parsedLane is not (CovenantLane.Confirmed or CovenantLane.Proposed))
        {
            return ReviewInputError("--lane must be confirmed or proposed.");
        }

        CovenantReviewListRequest request = new(
            campaignId is null ? CovenantScope.Global : CovenantScope.Campaign,
            campaignId,
            parsedLane,
            limit,
            cursor);

        if (request.Validate() is { IsFailure: true } invalid)
        {
            return ReviewInputError(invalid.Error.Message);
        }

        Result<CovenantReviewPageDto> result = await apiClient
            .ListCovenantReviewAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return WriteError(result.Error);
        }

        if (CliInvocationContext.Current.Json)
        {
            dispatcher.WriteJson(result.Value, ArcanumJsonContext.Default.CovenantReviewPageDto);
        }
        else
        {
            WriteCovenantReviewPage(result.Value);
        }

        return (int)CliExitCode.Success;
    }

    public async Task<int> SagaReviewApply(string file, CancellationToken cancellationToken)
    {
        Result<SagaReviewBulkPrepareRequest> request = await ReadReviewFileAsync(
            file,
            ArcanumJsonContext.Default.SagaReviewBulkPrepareRequest,
            "Saga",
            cancellationToken).ConfigureAwait(false);

        if (request.IsFailure)
        {
            return ReviewInputError(request.Error.Message);
        }

        Result<MemoryReviewBulkPlanDto> plan = await apiClient
            .PrepareSagaReviewAsync(request.Value, cancellationToken)
            .ConfigureAwait(false);

        return await ConfirmAndApplyAsync(
            plan,
            prepared => apiClient.ApplySagaReviewAsync(
                new SagaReviewBulkApplyRequest(request.Value, prepared.PreparedPlanToken),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> LexiconReviewApply(string file, CancellationToken cancellationToken)
    {
        Result<LexiconReviewBulkPrepareRequest> request = await ReadReviewFileAsync(
            file,
            ArcanumJsonContext.Default.LexiconReviewBulkPrepareRequest,
            "Lexicon",
            cancellationToken).ConfigureAwait(false);

        if (request.IsFailure)
        {
            return ReviewInputError(request.Error.Message);
        }

        Result<MemoryReviewBulkPlanDto> plan = await apiClient
            .PrepareLexiconReviewAsync(request.Value, cancellationToken)
            .ConfigureAwait(false);

        return await ConfirmAndApplyAsync(
            plan,
            prepared => apiClient.ApplyLexiconReviewAsync(
                new LexiconReviewBulkApplyRequest(request.Value, prepared.PreparedPlanToken),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CovenantReviewApply(string file, CancellationToken cancellationToken)
    {
        Result<CovenantReviewBulkPrepareRequest> request = await ReadReviewFileAsync(
            file,
            ArcanumJsonContext.Default.CovenantReviewBulkPrepareRequest,
            "Covenant",
            cancellationToken).ConfigureAwait(false);

        if (request.IsFailure)
        {
            return ReviewInputError(request.Error.Message);
        }

        Result<MemoryReviewBulkPlanDto> plan = await apiClient
            .PrepareCovenantReviewAsync(request.Value, cancellationToken)
            .ConfigureAwait(false);

        return await ConfirmAndApplyAsync(
            plan,
            prepared => apiClient.ApplyCovenantReviewAsync(
                new CovenantReviewBulkApplyRequest(request.Value, prepared.PreparedPlanToken),
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ConfirmAndApplyAsync(
        Result<MemoryReviewBulkPlanDto> prepared,
        Func<MemoryReviewBulkPlanDto, Task<Result<MemoryReviewBulkResultDto>>> apply,
        CancellationToken cancellationToken)
    {
        if (prepared.IsFailure)
        {
            return WriteError(prepared.Error);
        }

        WriteReviewPlan(prepared.Value);

        // Releasing an erasure fingerprint is the unsafe direction, so it is part of what is approved:
        // a warning beside the question, on the diagnostic stream, in every mode.
        foreach (MemoryReviewBulkPlanItemDto item in prepared.Value.Items)
        {
            switch (item.ReleasesErasureFingerprint)
            {
                case true:
                    dispatcher.WriteDiagnostic(
                        $"Event {item.EventSequence} ({item.SubjectId}) releases an erasure fingerprint: "
                        + "extraction or agents may write this again in its scope.");
                    break;

                case null:
                    dispatcher.WriteDiagnostic(
                        $"Event {item.EventSequence} ({item.SubjectId}): {MemoryErasureRenderer.UncheckedFingerprints}");
                    break;
            }
        }

        string prompt = prepared.Value.Action is MemoryReviewAction.Confirm
            ? $"Acknowledge that these {prepared.Value.Items.Length} exact {prepared.Value.Store} version(s) were reviewed? This changes no content, lifecycle, or Covenant lane."
            : $"Apply {prepared.Value.Action.ToString().ToLowerInvariant()} to {prepared.Value.Items.Length} exact {prepared.Value.Store} version(s)? No other memory store is touched.";

        if (!CliInvocationContext.Current.Yes
            && !await confirmationPrompt.PromptForConfirmationAsync(prompt, cancellationToken).ConfigureAwait(false))
        {
            dispatcher.WriteDiagnostic($"{prepared.Value.Store} bulk review cancelled; nothing was applied.");

            return (int)CliExitCode.Success;
        }

        Result<MemoryReviewBulkResultDto> result = await apply(prepared.Value).ConfigureAwait(false);

        if (result.IsFailure)
        {
            return WriteError(result.Error);
        }

        if (CliInvocationContext.Current.Json)
        {
            dispatcher.WriteJson(result.Value, ArcanumJsonContext.Default.MemoryReviewBulkResultDto);
        }
        else
        {
            dispatcher.WritePayload(
                $"{result.Value.Action} completed for {result.Value.Items.Length} exact {result.Value.Store} version(s). "
                + $"Reviewed through event {result.Value.ReviewedThroughEventSequence}. "
                + $"Replayed: {(result.Value.Replayed ? "yes" : "no")}. No other memory store was touched.");

            foreach (MemoryReviewBulkItemResultDto item in result.Value.Items)
            {
                switch (item.ReleasedErasureFingerprint)
                {
                    case true:
                        dispatcher.WritePayload(
                            $"Released an erasure fingerprint for event {item.EventSequence} ({item.SubjectId}).");
                        break;

                    case null:
                        dispatcher.WritePayload(
                            $"Event {item.EventSequence} ({item.SubjectId}): {MemoryErasureRenderer.UncheckedFingerprints}");
                        break;
                }
            }
        }

        return (int)CliExitCode.Success;
    }

    private async Task<Result<TRequest>> ReadReviewFileAsync<TRequest>(
        string file,
        JsonTypeInfo<TRequest> typeInfo,
        string store,
        CancellationToken cancellationToken)
        where TRequest : class
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return InvalidReviewFile<TRequest>("Review apply requires --file <path|->.");
        }

        if (file == "-" && !CliInvocationContext.Current.Yes)
        {
            return InvalidReviewFile<TRequest>("Review apply with --file - requires --yes before reading standard input.");
        }

        Result<string> authored = await AuthoredContentReader
            .ReadAsync(file, $"{store} review decision", emptyContentRemedy: null, cancellationToken)
            .ConfigureAwait(false);

        if (authored.IsFailure)
        {
            return Result<TRequest>.Failure(authored.Error);
        }

        try
        {
            TRequest? request = JsonSerializer.Deserialize(authored.Value, typeInfo);

            return request is null
                ? InvalidReviewFile<TRequest>($"{store} review decisions require one JSON request object.")
                : Result<TRequest>.Success(request);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return InvalidReviewFile<TRequest>($"{store} review decisions are not valid JSON for this store.");
        }
    }

    private static Result<T> InvalidReviewFile<T>(string message) =>
        Result<T>.Failure(new Error(ErrorCodes.Validation.InvalidBody, message));

    private int ReviewInputError(string message)
    {
        dispatcher.WriteDiagnostic(message);

        if (CliInvocationContext.Current.Json)
        {
            dispatcher.WriteJson(
                new CliErrorPayload(message, (int)CliExitCode.ConfigurationError),
                CliJsonContext.Default.CliErrorPayload);
        }

        return (int)CliExitCode.ConfigurationError;
    }

    private void WriteReviewPlan(MemoryReviewBulkPlanDto plan)
    {
        Action<string> write = CliInvocationContext.Current.Json
            ? dispatcher.WriteDiagnostic
            : dispatcher.WritePayload;

        write($"Store: {plan.Store}; action: {plan.Action}; exact versions: {plan.Items.Length}.");

        foreach (MemoryReviewBulkPlanItemDto item in plan.Items)
        {
            write($"  event {item.EventSequence}: {item.SubjectId} @ {item.VersionId} ({item.Scope}; {item.Origin}; {item.Source})");
        }

        write($"Prepared plan expires {plan.ExpiresAtUtc:u}.");
    }

    private void WriteSagaReviewPage(SagaReviewPageDto page)
    {
        foreach (SagaReviewItemDto item in page.Items)
        {
            dispatcher.WritePayload(
                $"event {item.EventSequence}: Saga {item.SubjectId} revision {item.Revision} "
                + $"[{item.Operation}; {item.Origin}; {ReviewScope(item.ScopeKind, item.CampaignId)}]");

            dispatcher.WritePayload($"  source: {item.Source ?? item.SourceSessionId?.ToString("D") ?? "not recorded"}");

            dispatcher.WritePayload($"  version: {item.VersionId}; current: {(item.IsCurrent ? "yes" : "no")}");

            dispatcher.WritePayload($"  observation: {item.ObservationToken}");
        }

        WriteReviewPageFooter(page.Items.Length, page.ReviewedThroughEventSequence, page.FrozenThroughEventSequence, page.NextCursor);
    }

    private void WriteLexiconReviewPage(LexiconReviewPageDto page)
    {
        foreach (LexiconReviewItemDto item in page.Items)
        {
            dispatcher.WritePayload(
                $"event {item.EventSequence}: Lexicon {item.EntryId:D} revision {item.Revision} "
                + $"[{item.Operation}; {item.Origin}; {LexiconReviewScope(item.Scope)}]");

            dispatcher.WritePayload($"  source session: {item.SourceSessionId?.ToString("D") ?? "not recorded"}");

            dispatcher.WritePayload($"  version: {item.VersionId}; current: {(item.IsCurrent ? "yes" : "no")}");

            dispatcher.WritePayload($"  observation: {item.ObservationToken}");
        }

        WriteReviewPageFooter(page.Items.Length, page.ReviewedThroughEventSequence, page.FrozenThroughEventSequence, page.NextCursor);
    }

    private void WriteCovenantReviewPage(CovenantReviewPageDto page)
    {
        foreach (CovenantReviewItemDto item in page.Items)
        {
            dispatcher.WritePayload(
                $"event {item.EventSequence}: Covenant {item.EntryId:D} key {item.Key} "
                + $"[{item.Operation}; {item.Origin}; {item.Scope}/{item.Lane}]");

            dispatcher.WritePayload(
                $"  source: turn {item.SourceTurnId?.ToString("D") ?? "not recorded"}; "
                + $"tool {item.SourceToolCallId ?? "not recorded"}");

            dispatcher.WritePayload($"  version: {item.VersionId:D}; current: {(item.IsCurrent ? "yes" : "no")}");

            dispatcher.WritePayload($"  observation: {item.ObservationToken}");
        }

        WriteReviewPageFooter(page.Items.Length, page.ReviewedThroughEventSequence, page.FrozenThroughEventSequence, page.NextCursor);
    }

    private void WriteReviewPageFooter(int count, long reviewedThrough, long frozenThrough, string? nextCursor)
    {
        if (count == 0)
        {
            dispatcher.WritePayload("No unreviewed versions in this exact scope.");
        }

        dispatcher.WritePayload(
            $"Reviewed through event {reviewedThrough}; page frozen through event {frozenThrough}.");

        if (nextCursor is not null)
        {
            dispatcher.WritePayload($"Next cursor: {nextCursor}");
        }
    }

    private static string ReviewScope(SagaMemoryScopeKind scope, Guid? campaignId) =>
        scope is SagaMemoryScopeKind.Campaign
            ? $"Campaign {campaignId:D}"
            : scope.ToString();

    private static string LexiconReviewScope(LexiconCurationScope scope) =>
        scope.Kind is LexiconScopeKind.Campaign
            ? $"Campaign {scope.CampaignId:D}"
            : "Global";
}
