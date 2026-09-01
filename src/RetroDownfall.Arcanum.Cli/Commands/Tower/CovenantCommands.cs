using System.Text.Json;

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
    /// The page size every cursor walk asks for.
    /// </summary>
    /// <remarks>
    /// A request size, not a total. Both catalogs are followed to exhaustion, so this only decides how
    /// many round trips a long history costs.
    /// </remarks>
    private const int PageSize = 50;

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

        Result<string> content = await AuthoredContentReader.ReadAsync(file, "Covenant", emptyContentRemedy: null, cancellationToken).ConfigureAwait(false);

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

        if (RevisionConflict(prepared.Value, "write") is { } writeConflict)
        {

            return writeConflict;

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

        if (RevisionConflict(prepared.Value, "retirement") is { } retireConflict)
        {

            return retireConflict;

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

    /// <summary>
    /// Corrects one preference, naming the exact version, revision and compiled hash it replaces.
    /// </summary>
    /// <remarks>
    /// The three target values come off <c>show</c>, which is where an operator reads the preference
    /// they decided was wrong. Requiring all three is what makes a correction a statement about
    /// something they looked at rather than about whatever is current when the request lands.
    /// </remarks>
    public async Task<int> Correct(
        string key,
        Guid? campaignId,
        string? file,
        Guid targetVersionId,
        long expectedRevision,
        string targetRenderedHash,
        CancellationToken cancellationToken)
    {

        Result<string> content = await AuthoredContentReader.ReadAsync(file, "Covenant", emptyContentRemedy: null, cancellationToken).ConfigureAwait(false);

        if (content.IsFailure)
        {

            return Fail(content.Error, CliExitCode.ConfigurationError);

        }

        Guid mutationId = Guid.CreateVersion7();

        CovenantScope scope = campaignId is null ? CovenantScope.Global : CovenantScope.Campaign;

        Result<CovenantMutationPreflightDto> prepared = await apiClient
            .PrepareCovenantCorrectAsync(
                new CovenantCorrectPrepareRequest(
                    scope,
                    campaignId,
                    key,
                    content.Value,
                    targetVersionId,
                    CovenantLane.Confirmed,
                    expectedRevision,
                    targetRenderedHash,
                    mutationId),
                cancellationToken)
            .ConfigureAwait(false);

        if (prepared.IsFailure)
        {

            return Fail(prepared.Error, CliExitCode.GenericError);

        }

        if (RevisionConflict(prepared.Value, "correction") is { } conflict)
        {

            return conflict;

        }

        if (!await ConfirmAsync(prepared.Value, "Correct", cancellationToken).ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic("Covenant correction cancelled.");

            return (int)CliExitCode.Success;

        }

        Result<CovenantMutationResultDto> committed = await apiClient
            .CorrectCovenantAsync(
                new CovenantCorrectRequest(
                    scope,
                    campaignId,
                    key,
                    content.Value,
                    targetVersionId,
                    CovenantLane.Confirmed,
                    expectedRevision,
                    targetRenderedHash,
                    mutationId,
                    prepared.Value.PreflightToken),
                cancellationToken)
            .ConfigureAwait(false);

        return WriteMutation(committed);

    }

    /// <summary>
    /// Pins, unpins, masks, or unmasks one subject, after printing the server's own measurement.
    /// </summary>
    /// <remarks>
    /// The screen is read off the preflight rather than off the flags this process parsed. It is the
    /// last place a mistyped subject can be caught, and printing what the client believes would defeat
    /// the point of a token that binds what the server measured.
    /// </remarks>
    public async Task<int> Curate(
        CovenantCurationKind kind,
        string key,
        Guid? campaignId,
        CovenantLane lane,
        long expectedRevision,
        CancellationToken cancellationToken)
    {

        Guid mutationId = Guid.CreateVersion7();

        CovenantScope scope = campaignId is null ? CovenantScope.Global : CovenantScope.Campaign;

        Result<CovenantCurationPreflightDto> prepared = await apiClient
            .PrepareCovenantCurationAsync(
                new CovenantCurationPrepareRequest(kind, scope, campaignId, key, lane, expectedRevision, mutationId),
                cancellationToken)
            .ConfigureAwait(false);

        if (prepared.IsFailure)
        {

            return Fail(prepared.Error, CliExitCode.GenericError);

        }

        if (CurationRevisionConflict(prepared.Value) is { } conflict)
        {

            return conflict;

        }

        if (!await ConfirmCurationAsync(prepared.Value, cancellationToken).ConfigureAwait(false))
        {

            dispatcher.WriteDiagnostic($"Covenant {kind.ToString().ToLowerInvariant()} cancelled.");

            return (int)CliExitCode.Success;

        }

        Result<CovenantCurationResultDto> committed = await apiClient
            .CurateCovenantAsync(
                new CovenantCurationRequest(
                    kind,
                    scope,
                    campaignId,
                    key,
                    lane,
                    expectedRevision,
                    mutationId,
                    prepared.Value.PreflightToken),
                cancellationToken)
            .ConfigureAwait(false);

        return WriteCuration(committed);

    }

    /// <summary>
    /// Lists one scope's heads, following the server's cursor until the catalog is exhausted.
    /// </summary>
    /// <remarks>
    /// Every page is followed rather than announced. A single page that ended with "more entries
    /// exist" left the rest unreachable: the continuation is an AEAD-sealed cursor, so there is no
    /// value an operator could type to ask for the next one. Following it here matches every other
    /// cursor catalog the CLI walks.
    ///
    /// <para>A cursor that comes back unchanged is a refusal to advance, not a page. Following it
    /// again would loop forever printing the same entries, so it stops and says the listing is
    /// partial.</para>
    /// </remarks>
    public async Task<int> List(
        Guid? campaignId,
        bool allScopes,
        CovenantLane? lane,
        CovenantLifecycle lifecycle,
        CancellationToken cancellationToken)
    {

        CovenantCursorScopeSelection selection = allScopes
            ? CovenantCursorScopeSelection.AllScopes
            : campaignId is null
                ? CovenantCursorScopeSelection.Global
                : CovenantCursorScopeSelection.Campaign;

        List<CovenantHeadDto> items = [];

        string? cursor = null;

        CovenantPageDto last;

        bool stalled;

        while (true)
        {

            Result<CovenantPageDto> page = await apiClient
                .ListCovenantAsync(
                    new CovenantListRequest(
                        selection,
                        campaignId,
                        lane,
                        lifecycle,
                        EffectiveForCampaignId: campaignId,
                        Limit: PageSize,
                        Cursor: cursor),
                    cancellationToken)
                .ConfigureAwait(false);

            if (page.IsFailure)
            {

                return Fail(page.Error, CliExitCode.GenericError);

            }

            items.AddRange(page.Value.Items);

            if (page.Value.NextCursor is not { Length: > 0 } next)
            {

                last = page.Value;

                stalled = false;

                break;

            }

            if (string.Equals(next, cursor, StringComparison.Ordinal))
            {

                last = page.Value;

                stalled = true;

                break;

            }

            cursor = next;

        }

        if (invocationContext.Options.Json)
        {

            // The last page's own search health is carried through rather than invented. Only the
            // entries and the continuation are this loop's to replace: it holds every page, so there
            // is nothing left for a caller to continue from.
            dispatcher.WriteJson(
                new CovenantListPayload(
                    [.. items.Select(Project)],
                    NextCursor: null,
                    stalled,
                    last.Search),
                CliJsonContext.Default.CovenantListPayload);

            return (int)CliExitCode.Success;

        }

        if (items.Count == 0)
        {

            dispatcher.WritePayload("No Covenant entries in that scope.");

            return (int)CliExitCode.Success;

        }

        foreach (CovenantHeadDto item in items)
        {

            dispatcher.WritePayload(
                $"{item.Key}  [{item.Lane}]  revision {item.LaneRevision}  {item.CompiledByteCost} bytes  {item.Origin}");

        }

        if (stalled)
        {

            dispatcher.WriteDiagnostic("The server stopped advancing its cursor; this listing is incomplete.");

        }

        return (int)CliExitCode.Success;

    }

    public async Task<int> Show(string key, Guid? campaignId, bool history, CancellationToken cancellationToken)
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

            List<CovenantVersionDto> versions = [];

            // The documented payload carries a history member, so the flag that fills it has to be
            // honored in the mode a script reads. Reporting an empty array for a key that has a
            // history would be a payload that answers a question nobody asked.
            if (history && detail.Value.EntryId is { } historyEntryId)
            {

                foreach (CovenantLane lane in Lanes(detail.Value))
                {

                    int read = await ReadHistoryAsync(historyEntryId, lane, versions, cancellationToken)
                        .ConfigureAwait(false);

                    if (read != (int)CliExitCode.Success)
                    {

                        return read;

                    }

                }

            }

            dispatcher.WriteJson(
                new CovenantShowPayload(
                    detail.Value.Scope,
                    detail.Value.CampaignId,
                    detail.Value.Key,
                    detail.Value.Confirmed is null ? null : Project(detail.Value.Confirmed),
                    detail.Value.Proposed is null ? null : Project(detail.Value.Proposed),
                    detail.Value.KeyEpoch,
                    [.. versions]),
                CliJsonContext.Default.CovenantShowPayload);

            return (int)CliExitCode.Success;

        }

        if (detail.Value.EntryId is null)
        {

            dispatcher.WritePayload($"No Covenant entry under '{key}' in that scope.");

            return (int)CliExitCode.Success;

        }

        WriteHead("Confirmed", detail.Value.Confirmed);

        WriteHead("Proposed", detail.Value.Proposed);

        if (!history)
        {

            return (int)CliExitCode.Success;

        }

        // Only lanes that have a head are asked about. A version page is keyed by entry and lane, and
        // asking for the history of a lane that was never written would spend an authenticated
        // installation read to be told nothing.
        foreach (CovenantLane lane in Lanes(detail.Value))
        {

            int written = await WriteHistoryAsync(
                    detail.Value.EntryId.Value,
                    lane,
                    cancellationToken)
                .ConfigureAwait(false);

            if (written != (int)CliExitCode.Success)
            {

                return written;

            }

        }

        return (int)CliExitCode.Success;

    }

    private static IEnumerable<CovenantLane> Lanes(CovenantDetailDto detail)
    {

        if (detail.Confirmed is not null)
        {

            yield return CovenantLane.Confirmed;

        }

        if (detail.Proposed is not null)
        {

            yield return CovenantLane.Proposed;

        }

    }

    /// <summary>
    /// Prints one lane's history, newest revision first, following the server's cursor.
    /// </summary>
    /// <remarks>
    /// Operation, origin, and mutation identity are printed beside the revision because those are the
    /// three fields that answer "who changed this preference, and when". The authored content is not
    /// printed and is not in the payload: a history is a record of changes, not a second way to read
    /// what a key says.
    /// </remarks>
    private async Task<int> WriteHistoryAsync(
        Guid entryId,
        CovenantLane lane,
        CancellationToken cancellationToken)
    {

        dispatcher.WritePayload($"{lane} history:");

        List<CovenantVersionDto> versions = [];

        int read = await ReadHistoryAsync(entryId, lane, versions, cancellationToken).ConfigureAwait(false);

        if (read != (int)CliExitCode.Success)
        {

            return read;

        }

        foreach (CovenantVersionDto version in versions)
        {

            dispatcher.WritePayload(
                $"  revision {version.LaneRevision}  {version.Operation}  {version.Origin}  "
                + $"{version.CompiledByteCost} bytes  mutation {version.MutationId}  "
                + $"{version.CreatedAtUtc:u}");

        }

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Follows one lane's version cursor to exhaustion, collecting what it hands back.
    /// </summary>
    /// <remarks>
    /// One walk for both modes. The rendering differs and the reading does not, and a second copy of
    /// the cursor loop would be a second place for the stall guard to be forgotten.
    /// </remarks>
    private async Task<int> ReadHistoryAsync(
        Guid entryId,
        CovenantLane lane,
        List<CovenantVersionDto> into,
        CancellationToken cancellationToken)
    {

        string? cursor = null;

        while (true)
        {

            Result<CovenantVersionPageDto> page = await apiClient
                .ListCovenantVersionsAsync(
                    new CovenantVersionsRequest(entryId, lane, PageSize, cursor),
                    cancellationToken)
                .ConfigureAwait(false);

            if (page.IsFailure)
            {

                return Fail(page.Error, CliExitCode.GenericError);

            }

            into.AddRange(page.Value.Items);

            if (page.Value.NextCursor is not { Length: > 0 } next
                || string.Equals(next, cursor, StringComparison.Ordinal))
            {

                return (int)CliExitCode.Success;

            }

            cursor = next;

        }

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
    /// Projects one wire head into the entry shape the <c>--json</c> promise froze.
    /// </summary>
    /// <remarks>
    /// The two records answer to different contracts: the wire shape belongs to the API and this one
    /// to the documented CLI surface. Emitting the DTO instead handed a script <c>items</c> where the
    /// reference promised <c>entries</c>, and <c>laneRevision</c> and <c>compiledByteCost</c> where it
    /// promised <c>revision</c> and <c>byteCost</c> — close enough to look right and wrong at every
    /// member a caller reads.
    ///
    /// <para>The authored hashes, provenance counts and creation timestamp are wire detail the CLI
    /// payload does not carry. What survives is what the reference lists.</para>
    /// </remarks>
    private static CovenantEntryPayload Project(CovenantHeadDto head) =>
        new(
            head.EntryId,
            head.VersionId,
            head.Scope,
            head.CampaignId,
            head.Key,
            head.Lane,
            head.LaneRevision,
            head.Lifecycle,
            head.Origin,
            head.CompiledByteCost,
            head.Shadow,
            head.Materialization,
            head.UpdatedAtUtc);

    /// <summary>
    /// Refuses a write the commit would refuse, before the operator is asked to approve it.
    /// </summary>
    /// <remarks>
    /// The two numbers are compared here because the confirmation screen cannot tell them apart: it
    /// reports the head, every line of it is true, and it describes a write that the kernel will
    /// refuse the moment it is approved — with a message that names neither the revision the operator
    /// needed nor the flag that carries it. Omitting <c>--expected-revision</c> sends zero, so the
    /// natural command for updating an existing preference is exactly the one that fails.
    ///
    /// <para>An exit code rather than a cancellation. Nobody cancelled anything, and a script that
    /// read success here would go on believing the preference had changed.</para>
    /// </remarks>
    private int? RevisionConflict(CovenantMutationPreflightDto preflight, string noun)
    {

        if (preflight.ExpectedLaneRevision == preflight.CurrentLaneRevision)
        {

            return null;

        }

        dispatcher.WriteDiagnostic(
            $"This {noun} expects revision {preflight.ExpectedLaneRevision}, but the {preflight.Lane} lane "
            + $"is at revision {preflight.CurrentLaneRevision}. It would be refused, so nothing was asked or written.");

        dispatcher.WriteDiagnostic(
            $"Re-run with --expected-revision {preflight.CurrentLaneRevision} to act on what is there now.");

        return (int)CliExitCode.ConfigurationError;

    }

    /// <summary>
    /// Shows the server's own measurement, then asks.
    /// </summary>
    /// <remarks>
    /// A Global mutation reports how many Campaigns it reaches before the question, because "this
    /// applies everywhere" is the fact most likely to surprise someone who meant to change one
    /// project's preference.
    ///
    /// <para>The lane is the server's, read off the preflight rather than off the flag this process
    /// parsed. It is the field that says which of the two standings is about to change, and a
    /// confirmation screen that omitted it could not show an operator that their <c>--lane</c> named
    /// something other than what the request carries.</para>
    ///
    /// <para>The screen is written only outside <c>--json</c>. Every line of it goes to stdout, and
    /// stdout under <c>--json</c> is the payload stream: an approved mutation used to emit these lines
    /// ahead of its document, so the documented automation spelling produced something no JSON parser
    /// would accept.</para>
    /// </remarks>
    private async Task<bool> ConfirmAsync(
        CovenantMutationPreflightDto preflight,
        string verb,
        CancellationToken cancellationToken)
    {

        if (invocationContext.Options.Json)
        {

            WritePlan(preflight);

        }
        else
        {

            dispatcher.WritePayload(
                $"{verb} '{preflight.NormalizedKey}' in the {preflight.Lane} lane, {preflight.Scope} scope.");

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

        }

        return await confirmationPrompt
            .PromptForConfirmationAsync($"{verb} this Covenant entry?", cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Publishes the server's plan as the documented structured payload, on the diagnostic stream.
    /// </summary>
    /// <remarks>
    /// Stdout under <c>--json</c> belongs to the one document a script parses, and the result is that
    /// document — so the plan travels beside the question it belongs to, exactly as the backup-restore
    /// statement does. Publishing it at all is what keeps the server's own measurement on the record
    /// for an unattended run: <c>--yes</c> answers the question, it does not make the compiled hash,
    /// the framed cost, and the affected-Campaign count stop mattering.
    ///
    /// <para>Every number is the preflight's. A client that recomputed one would be publishing a
    /// second opinion about an effect only the server evaluates.</para>
    /// </remarks>
    private void WritePlan(CovenantMutationPreflightDto preflight) =>
        dispatcher.WriteDiagnostic(
            JsonSerializer.Serialize(
                new CovenantMutationPlanPayload(
                    preflight.Scope,
                    preflight.CampaignId,
                    preflight.NormalizedKey,
                    preflight.Lane,
                    preflight.Operation,
                    preflight.MutationId,
                    preflight.RenderedHash,
                    preflight.CompiledByteCost,
                    preflight.CurrentLaneRevision,
                    preflight.Effect.LocalDecision,
                    preflight.Effect.AffectedCampaignCount,
                    preflight.Effect.ExamplesTruncated,
                    preflight.Effect.AppliesToFutureCampaigns,
                    preflight.ExpiresAtUtc),
                CliJsonContext.Default.CovenantMutationPlanPayload));

    private int WriteMutation(Result<CovenantMutationResultDto> committed)
    {

        if (committed.IsFailure)
        {

            return Fail(committed.Error, CliExitCode.GenericError);

        }

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(
                new CovenantMutationResultPayload(
                    committed.Value.MutationId,
                    committed.Value.Outcome,
                    committed.Value.Operation,
                    committed.Value.Scope,
                    committed.Value.CampaignId,
                    committed.Value.NormalizedKey,
                    committed.Value.Lane,
                    committed.Value.ResultingLaneRevision,
                    committed.Value.Replayed),
                CliJsonContext.Default.CovenantMutationResultPayload);

            return (int)CliExitCode.Success;

        }

        dispatcher.WritePayload(committed.Value.Replayed
            ? $"Already applied: '{committed.Value.NormalizedKey}' is at revision {committed.Value.ResultingLaneRevision}."
            : $"{committed.Value.Outcome}: '{committed.Value.NormalizedKey}' is now revision {committed.Value.ResultingLaneRevision}.");

        return (int)CliExitCode.Success;

    }

    /// <summary>
    /// Prints the server's measurement of one curation change, then asks.
    /// </summary>
    /// <remarks>
    /// The broader-scope sentence is the line that matters. Masking a Global key and retiring a
    /// Campaign entry are opposite answers to "what applies here afterwards", and only one of them
    /// leaves the operator with nothing.
    ///
    /// <para>Suppressed under <c>--json</c> for the same reason the mutation screen is: these lines go
    /// to stdout, which is where the one JSON document has to be alone.</para>
    /// </remarks>
    private async Task<bool> ConfirmCurationAsync(
        CovenantCurationPreflightDto preflight,
        CancellationToken cancellationToken)
    {

        if (!invocationContext.Options.Json)
        {

            dispatcher.WritePayload(
                $"{preflight.Kind} '{preflight.NormalizedKey}' in the {preflight.Lane} lane, {preflight.Scope} scope.");

            dispatcher.WritePayload($"  Current state:    pinned={preflight.IsPinned}, masked={preflight.IsMasked}");

            dispatcher.WritePayload($"  Current revision: {preflight.CurrentRevision}");

            if (!preflight.ChangesAnything)
            {

                dispatcher.WritePayload("  This subject is already in that state; committing records the request and changes nothing.");

            }

            if (preflight.GlobalConfirmedSuppressed)
            {

                dispatcher.WritePayload("  The Global entry for this key stops applying in this Campaign, and nothing replaces it.");

            }

            if (preflight.GlobalConfirmedResurfaces)
            {

                dispatcher.WritePayload("  The Global entry for this key starts applying in this Campaign again.");

            }

        }

        return await confirmationPrompt
            .PromptForConfirmationAsync($"{preflight.Kind} this Covenant subject?", cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Refuses a curation change whose expected revision the server says is already wrong.
    /// </summary>
    /// <remarks>
    /// Before the question is put, not after. The kernel compares exactly these two numbers, so asking
    /// first would render a confirmation screen every line of which is true and which describes a
    /// change that cannot succeed.
    /// </remarks>
    private int? CurationRevisionConflict(CovenantCurationPreflightDto preflight) =>
        preflight.CurrentRevision == preflight.ExpectedRevision
            ? null
            : Fail(
                new Error(
                    ErrorCodes.Covenant.RevisionConflict,
                    $"This Covenant subject is at curation revision {preflight.CurrentRevision} and the request expected "
                        + $"{preflight.ExpectedRevision}. Pass --expected-revision {preflight.CurrentRevision}."),
                CliExitCode.GenericError);

    private int WriteCuration(Result<CovenantCurationResultDto> committed)
    {

        if (committed.IsFailure)
        {

            return Fail(committed.Error, CliExitCode.GenericError);

        }

        if (invocationContext.Options.Json)
        {

            dispatcher.WriteJson(committed.Value, ArcanumJsonContext.Default.CovenantCurationResultDto);

            return (int)CliExitCode.Success;

        }

        dispatcher.WritePayload(committed.Value.Replayed
            ? $"Already applied: '{committed.Value.NormalizedKey}' is pinned={committed.Value.IsPinned}, masked={committed.Value.IsMasked}."
            : $"{committed.Value.Outcome}: '{committed.Value.NormalizedKey}' is now pinned={committed.Value.IsPinned}, masked={committed.Value.IsMasked}.");

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
