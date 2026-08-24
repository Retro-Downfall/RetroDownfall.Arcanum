using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Commands.Tower;

/// <summary>
/// The operator's command line over their own Covenant.
/// </summary>
/// <remarks>
/// Thin over the API, like every other verb group: the host owns authority, leases, and the
/// prepare-then-commit protocol, and the CLI's job is to show an operator what a mutation would do
/// and let them decline it.
///
/// <para>Both mutating verbs prepare first and print the server's own measurement — the compiled hash,
/// the framed byte cost, the affected-Campaign count — before asking for confirmation. Printing what
/// the client believes would defeat the point: the whole reason the token exists is that the server's
/// measurement is the one being committed.</para>
/// </remarks>
public sealed class CovenantCommands(
    ArcanumApiClient apiClient,
    IConsoleDispatcher dispatcher,
    IConfirmationPrompt confirmationPrompt,
    ICliInvocationContext invocationContext)
{

    /// <summary>
    /// Writes one standing preference the operator wants honored.
    /// </summary>
    /// <remarks>
    /// Content comes from <c>--file</c> or standard input, never from an argument. A preference is
    /// exactly the kind of text an operator would not want in their shell history or in the process
    /// list of a shared machine.
    /// </remarks>
    public async Task<int> Set(
        string key,
        Guid? campaignId,
        string? file,
        long expectedRevision,
        bool reactivate,
        CancellationToken cancellationToken)
    {

        Result<string> content = await ReadContentAsync(file, cancellationToken).ConfigureAwait(false);

        if (content.IsFailure)
        {

            return Fail(content.Error, CliExitCode.ConfigurationError);

        }

        Guid mutationId = Guid.CreateVersion7();

        CovenantScope scope = campaignId is null ? CovenantScope.Global : CovenantScope.Campaign;

        Result<CovenantMutationPreflightDto> prepared = await apiClient
            .PrepareCovenantSetAsync(
                new CovenantSetPrepareRequest(
                    scope,
                    campaignId,
                    key,
                    content.Value,
                    expectedRevision,
                    mutationId,
                    reactivate),
                cancellationToken)
            .ConfigureAwait(false);

        if (prepared.IsFailure)
        {

            return Fail(prepared.Error, CliExitCode.GenericError);

        }

        if (!await ConfirmAsync(prepared.Value, "Write", cancellationToken).ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Covenant write cancelled.");

            return (int)CliExitCode.Success;

        }

        Result<CovenantMutationResultDto> committed = await apiClient
            .SetCovenantAsync(
                new CovenantSetRequest(
                    scope,
                    campaignId,
                    key,
                    content.Value,
                    expectedRevision,
                    mutationId,
                    reactivate,
                    prepared.Value.PreflightToken),
                cancellationToken)
            .ConfigureAwait(false);

        return WriteMutation(committed);

    }

    public async Task<int> Retire(
        string key,
        Guid? campaignId,
        CovenantLane lane,
        long expectedRevision,
        CancellationToken cancellationToken)
    {

        Guid mutationId = Guid.CreateVersion7();

        CovenantScope scope = campaignId is null ? CovenantScope.Global : CovenantScope.Campaign;

        Result<CovenantMutationPreflightDto> prepared = await apiClient
            .PrepareCovenantRetireAsync(
                new CovenantRetirePrepareRequest(scope, campaignId, key, lane, expectedRevision, mutationId),
                cancellationToken)
            .ConfigureAwait(false);

        if (prepared.IsFailure)
        {

            return Fail(prepared.Error, CliExitCode.GenericError);

        }

        if (!await ConfirmAsync(prepared.Value, "Retire", cancellationToken).ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Covenant retirement cancelled.");

            return (int)CliExitCode.Success;

        }

        Result<CovenantMutationResultDto> committed = await apiClient
            .RetireCovenantAsync(
                new CovenantRetireRequest(
                    scope,
                    campaignId,
                    key,
                    lane,
                    expectedRevision,
                    mutationId,
                    prepared.Value.PreflightToken),
                cancellationToken)
            .ConfigureAwait(false);

        return WriteMutation(committed);

    }

    public async Task<int> List(
        Guid? campaignId,
        bool allScopes,
        CovenantLane? lane,
        CancellationToken cancellationToken)
    {

        CovenantCursorScopeSelection selection = allScopes
            ? CovenantCursorScopeSelection.AllScopes
            : campaignId is null
                ? CovenantCursorScopeSelection.Global
                : CovenantCursorScopeSelection.Campaign;

        Result<CovenantPageDto> page = await apiClient
            .ListCovenantAsync(
                new CovenantListRequest(
                    selection,
                    campaignId,
                    lane,
                    CovenantLifecycle.Set,
                    EffectiveForCampaignId: campaignId,
                    Limit: 50,
                    Cursor: null),
                cancellationToken)
            .ConfigureAwait(false);

        if (page.IsFailure)
        {

            return Fail(page.Error, CliExitCode.GenericError);

        }

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(page.Value, ArcanumJsonContext.Default.CovenantPageDto);

            return (int)CliExitCode.Success;

        }

        if (page.Value.Items.Length == 0)
        {

            dispatcher.WritePayload("No Covenant entries in that scope.");

            return (int)CliExitCode.Success;

        }

        foreach (CovenantHeadDto item in page.Value.Items)
        {

            dispatcher.WritePayload(
                $"{item.Key}  [{item.Lane}]  revision {item.LaneRevision}  {item.CompiledByteCost} bytes  {item.Origin}");

        }

        if (page.Value.Truncated)
        {

            dispatcher.WriteDiagnostic("More entries exist than this page shows.");

        }

        return (int)CliExitCode.Success;

    }

    public async Task<int> Show(string key, Guid? campaignId, CancellationToken cancellationToken)
    {

        Result<CovenantDetailDto> detail = await apiClient
            .ShowCovenantAsync(
                new CovenantDetailRequest(
                    campaignId is null ? CovenantScope.Global : CovenantScope.Campaign,
                    campaignId,
                    key),
                cancellationToken)
            .ConfigureAwait(false);

        if (detail.IsFailure)
        {

            return Fail(detail.Error, CliExitCode.GenericError);

        }

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(detail.Value, ArcanumJsonContext.Default.CovenantDetailDto);

            return (int)CliExitCode.Success;

        }

        if (detail.Value.EntryId is null)
        {

            dispatcher.WritePayload($"No Covenant entry under '{key}' in that scope.");

            return (int)CliExitCode.Success;

        }

        WriteHead("Confirmed", detail.Value.Confirmed);

        WriteHead("Proposed", detail.Value.Proposed);

        return (int)CliExitCode.Success;

    }

    private void WriteHead(string label, CovenantHeadDto? head)
    {

        // An absent lane is reported, not skipped. "There is no Proposed entry" and "I did not look"
        // are different answers, and silence would read as the second.
        dispatcher.WritePayload(head is null
            ? $"{label}: none"
            : $"{label}: revision {head.LaneRevision}, {head.CompiledByteCost} bytes, {head.Origin}, "
                + $"updated {head.UpdatedAtUtc:u}");

    }

    /// <summary>
    /// Shows the server's own measurement, then asks.
    /// </summary>
    /// <remarks>
    /// A Global mutation reports how many Campaigns it reaches before the question, because "this
    /// applies everywhere" is the fact most likely to surprise someone who meant to change one
    /// project's preference.
    /// </remarks>
    private async Task<bool> ConfirmAsync(
        CovenantMutationPreflightDto preflight,
        string verb,
        CancellationToken cancellationToken)
    {

        dispatcher.WritePayload($"{verb} '{preflight.NormalizedKey}' in {preflight.Scope} scope.");

        dispatcher.WritePayload($"  Current revision: {preflight.CurrentLaneRevision}");

        if (preflight.CompiledByteCost is { } cost)
        {

            dispatcher.WritePayload($"  Compiled cost:    {cost} bytes");

        }

        if (preflight.RenderedHash is { } hash)
        {

            dispatcher.WritePayload($"  Rendered hash:    {hash}");

        }

        dispatcher.WritePayload($"  Affects:          {preflight.Effect.AffectedCampaignCount} Campaign(s)");

        if (preflight.Effect.AppliesToFutureCampaigns)
        {

            dispatcher.WritePayload("  Also applies to Campaigns created later.");

        }

        return await confirmationPrompt
            .PromptForConfirmationAsync($"{verb} this Covenant entry?", cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Reads authored content from a file or standard input, never from an argument.
    /// </summary>
    private static async Task<Result<string>> ReadContentAsync(string? file, CancellationToken cancellationToken)
    {

        if (file is { Length: > 0 })
        {

            return File.Exists(file)
                ? Result<string>.Success(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false))
                : Result<string>.Failure(new Error(
                    ErrorCodes.Validation.InvalidBody,
                    $"No file exists at '{file}'."));

        }

        if (!Console.IsInputRedirected)
        {

            return Result<string>.Failure(new Error(
                ErrorCodes.Validation.InvalidBody,
                "Covenant content comes from --file or piped standard input, not from a command-line argument."));

        }

        string piped = await Console.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(piped)
            ? Result<string>.Failure(new Error(
                ErrorCodes.Validation.InvalidBody,
                "Covenant content was empty."))
            : Result<string>.Success(piped);

    }

    private int WriteMutation(Result<CovenantMutationResultDto> committed)
    {

        if (committed.IsFailure)
        {

            return Fail(committed.Error, CliExitCode.GenericError);

        }

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(committed.Value, ArcanumJsonContext.Default.CovenantMutationResultDto);

            return (int)CliExitCode.Success;

        }

        dispatcher.WritePayload(committed.Value.Replayed
            ? $"Already applied: '{committed.Value.NormalizedKey}' is at revision {committed.Value.ResultingLaneRevision}."
            : $"{committed.Value.Outcome}: '{committed.Value.NormalizedKey}' is now revision {committed.Value.ResultingLaneRevision}.");

        return (int)CliExitCode.Success;

    }

    private int Fail(Error error, CliExitCode exitCode)
    {

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                new CliErrorPayload(error.Message, (int)exitCode),
                CliJsonContext.Default.CliErrorPayload);

            return (int)exitCode;

        }

        dispatcher.WriteDiagnostic(error.Message);

        return (int)exitCode;

    }

}
