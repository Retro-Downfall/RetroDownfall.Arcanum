using System.Globalization;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Cli;

/// <summary>
/// Composes the registered checks and repairs into one <see cref="DoctorReport"/> (DESIGN §4.4.2).
///
/// <para>Three rules live here rather than in any individual check. First, <b>containment</b>: a
/// check that throws becomes an <see cref="DoctorOutcome.Unavailable"/> finding naming only the
/// exception type, because the whole point of <c>arcanum doctor</c> is to keep working on the
/// machine that is already broken, and because an exception message is an uncontrolled string that
/// may carry a path or a credential. Second, <b>separation</b>: diagnosis never mutates, planning
/// never mutates, and only an explicit <c>--apply</c> reaches
/// <see cref="IDoctorRepair.ApplyAsync"/>. Third, <b>registry integrity</b>: id uniqueness, the
/// <c>subsystem.snake_case</c> namespace, and the detector-exists rule are enforced in the
/// constructor, so a miswired registration fails at startup instead of producing a report that
/// silently omits a subsystem.</para>
/// </summary>
public sealed class DoctorDiagnosticRunner
{

    private readonly IReadOnlyList<IDoctorCheck> _checks;

    private readonly IReadOnlyList<IDoctorRepair> _repairs;

    public DoctorDiagnosticRunner(IEnumerable<IDoctorCheck> checks, IEnumerable<IDoctorRepair> repairs)
    {

        ArgumentNullException.ThrowIfNull(checks);

        ArgumentNullException.ThrowIfNull(repairs);

        _checks = [.. checks];

        _repairs = [.. repairs];

        HashSet<string> checkIds = new(StringComparer.Ordinal);

        foreach (IDoctorCheck check in _checks)
        {

            RequireNamespacedId(check.Id, check.Subsystem, "check");

            if (!checkIds.Add(check.Id))
            {

                throw new InvalidOperationException(
                    $"Duplicate doctor check id '{check.Id}'. Check ids are part of the --json contract and must be unique.");

            }

        }

        HashSet<string> repairIds = new(StringComparer.Ordinal);

        foreach (IDoctorRepair repair in _repairs)
        {

            RequireNamespacedId(repair.Id, repair.Subsystem, "repair");

            if (!repairIds.Add(repair.Id))
            {

                throw new InvalidOperationException(
                    $"Duplicate doctor repair id '{repair.Id}'.");

            }

            if (repair.DetectorIds.Count == 0)
            {

                throw new InvalidOperationException(
                    $"Doctor repair '{repair.Id}' declares no detector. Every repair must have a read-only check that finds what it fixes.");

            }

            foreach (string detectorId in repair.DetectorIds)
            {

                if (!checkIds.Contains(detectorId))
                {

                    throw new InvalidOperationException(
                        $"Doctor repair '{repair.Id}' names detector '{detectorId}', which is not a registered check.");

                }

            }

        }

    }

    /// <summary>Every registered check, in registration order.</summary>
    public IReadOnlyList<IDoctorCheck> Checks => _checks;

    /// <summary>Every registered repair, in registration order.</summary>
    public IReadOnlyList<IDoctorRepair> Repairs => _repairs;

    /// <summary>The lower-cased namespace a check id in <paramref name="subsystem"/> must use.</summary>
    public static string NamespaceOf(DoctorSubsystem subsystem) =>
        subsystem.ToString().ToLowerInvariant();

    /// <summary>
    /// Whether <paramref name="selector"/> names this check, either by its exact id or by its
    /// subsystem. Public because the legacy panel checks are composed outside this runner and must
    /// resolve <c>--only</c> / <c>--skip</c> against the identical rule; two selector
    /// implementations would eventually disagree about which checks a filter covers.
    /// </summary>
    public static bool MatchesSelector(string id, DoctorSubsystem subsystem, string selector) =>
        string.Equals(id, selector, StringComparison.OrdinalIgnoreCase)
        || string.Equals(NamespaceOf(subsystem), selector, StringComparison.OrdinalIgnoreCase);

    /// <param name="alsoKnown">
    /// Checks that exist but are not registered here — the legacy panel checks. They are not run by
    /// this runner, but a selector naming one must still be accepted, or <c>--only mcp</c> would be
    /// rejected as unknown purely because that check is composed elsewhere.
    /// </param>
    public async Task<Result<DoctorReport>> RunAsync(
        DoctorRunRequest request,
        CancellationToken cancellationToken,
        IReadOnlyList<DoctorCatalogCheck>? alsoKnown = null)
    {

        ArgumentNullException.ThrowIfNull(request);

        if (request.Apply && request.Repairs.Count == 0)
        {

            return Result<DoctorReport>.Failure(new Error(
                DoctorErrors.ApplyWithoutRepairCode,
                "--apply needs a repair to apply. Choose one with '--repair <id>', or list the available repairs with 'arcanum doctor list'."));

        }

        Result<IReadOnlyList<IDoctorCheck>> selected = SelectChecks(request, alsoKnown);

        if (selected.IsFailure)
        {

            return Result<DoctorReport>.Failure(selected.Error);

        }

        Result<IReadOnlyList<IDoctorRepair>> repairs = SelectRepairs(request);

        if (repairs.IsFailure)
        {

            return Result<DoctorReport>.Failure(repairs.Error);

        }

        List<DoctorCheck> results = [];

        foreach (IDoctorCheck check in selected.Value)
        {

            cancellationToken.ThrowIfCancellationRequested();

            results.Add(await InspectAsync(check, request, cancellationToken).ConfigureAwait(false));

        }

        List<DoctorRepairResult> repairResults = [];

        foreach (IDoctorRepair repair in repairs.Value)
        {

            cancellationToken.ThrowIfCancellationRequested();

            repairResults.Add(
                await RunRepairAsync(repair, request.Apply, cancellationToken).ConfigureAwait(false));

        }

        DoctorOutcome outcome = DoctorOutcomes.Aggregate(
            results.Select(static check => check.Outcome ?? DoctorOutcome.Healthy));

        bool repairFailed = repairResults.Any(static result => result.State == DoctorRepairState.Failed);

        bool healthy = !repairFailed && !DoctorOutcomes.IsFailure(outcome, request.Strict);

        return Result<DoctorReport>.Success(
            new DoctorReport(healthy, results, outcome, repairResults));

    }

    /// <summary>
    /// Resolves <c>--only</c> / <c>--skip</c> against check ids and subsystem names. An unrecognized
    /// selector fails the command rather than silently running a smaller diagnostic: a typo that
    /// quietly skipped the subsystem an operator was trying to inspect is worse than an error.
    /// </summary>
    public Result<IReadOnlyList<IDoctorCheck>> SelectChecks(
        DoctorRunRequest request,
        IReadOnlyList<DoctorCatalogCheck>? alsoKnown = null)
    {

        ArgumentNullException.ThrowIfNull(request);

        Result unknown = RequireKnownSelectors([.. request.Only, .. request.Skip], alsoKnown ?? []);

        if (unknown.IsFailure)
        {

            return Result<IReadOnlyList<IDoctorCheck>>.Failure(unknown.Error);

        }

        IEnumerable<IDoctorCheck> selected = _checks;

        if (request.Only.Count > 0)
        {

            selected = selected.Where(check => request.Only.Any(selector => Matches(check, selector)));

        }

        if (request.Skip.Count > 0)
        {

            selected = selected.Where(check => !request.Skip.Any(selector => Matches(check, selector)));

        }

        return Result<IReadOnlyList<IDoctorCheck>>.Success([.. selected]);

    }

    public Result<IReadOnlyList<IDoctorRepair>> SelectRepairs(DoctorRunRequest request)
    {

        ArgumentNullException.ThrowIfNull(request);

        List<IDoctorRepair> selected = [];

        foreach (string id in request.Repairs)
        {

            IDoctorRepair? repair = _repairs.FirstOrDefault(
                candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));

            if (repair is null)
            {

                return Result<IReadOnlyList<IDoctorRepair>>.Failure(UnknownSelector(id));

            }

            if (!selected.Contains(repair))
            {

                selected.Add(repair);

            }

        }

        return Result<IReadOnlyList<IDoctorRepair>>.Success(selected);

    }

    private async Task<DoctorCheck> InspectAsync(
        IDoctorCheck check,
        DoctorRunRequest request,
        CancellationToken cancellationToken)
    {

        if (check.RequiresNetwork && !request.IncludeNetwork)
        {

            return Project(
                check,
                new DoctorFinding(
                    DoctorOutcome.Skipped,
                    "Network probes are opt-in. Re-run with --include-network to check this subsystem."));

        }

        try
        {

            DoctorFinding finding = await check.InspectAsync(cancellationToken).ConfigureAwait(false);

            return Project(check, finding);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception exception)
        {

            // Only the type name: an exception message is an uncontrolled string that may carry a
            // credential, a raw response body, or a path outside the installation.
            return Project(
                check,
                new DoctorFinding(
                    DoctorOutcome.Unavailable,
                    $"The '{check.Id}' check could not complete ({exception.GetType().Name}). "
                    + "Re-run with ARCANUM_LOG_LEVEL=Debug for the full diagnostic."));

        }

    }

    private static async Task<DoctorRepairResult> RunRepairAsync(
        IDoctorRepair repair,
        bool apply,
        CancellationToken cancellationToken)
    {

        try
        {

            return apply
                ? await repair.ApplyAsync(cancellationToken).ConfigureAwait(false)
                : await repair.PlanAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception exception)
        {

            return new DoctorRepairResult(
                repair.Id,
                DoctorRepairState.Failed,
                apply ? "The repair could not be applied." : "The repair could not be planned.",
                [],
                exception.GetType().Name);

        }

    }

    private static DoctorCheck Project(IDoctorCheck check, DoctorFinding finding) =>
        new(
            check.Name,
            DoctorOutcomes.ToLegacyStatus(finding.Outcome),
            finding.Detail,
            check.Id,
            check.Subsystem,
            finding.Outcome,
            finding.Remedies ?? []);

    private Result RequireKnownSelectors(
        IReadOnlyList<string> selectors,
        IReadOnlyList<DoctorCatalogCheck> alsoKnown)
    {

        foreach (string selector in selectors)
        {

            if (_checks.Any(check => Matches(check, selector))
                || alsoKnown.Any(check => MatchesSelector(check.Id, check.Subsystem, selector)))
            {

                continue;

            }

            // A repair id is a real id, but not a diagnostic. Accepting it here would run zero
            // checks and report a clean bill of health — the exact false all-clear a CI gate would
            // then trust. 'arcanum doctor list' prints both columns, so this is the likely mistake.
            if (_repairs.Any(repair => string.Equals(repair.Id, selector, StringComparison.OrdinalIgnoreCase)))
            {

                return Result.Failure(new Error(
                    DoctorErrors.UnknownSelectorCode,
                    $"'{selector}' is a repair id, not a diagnostic. Use '--repair {selector}' to plan it, "
                    + "or 'arcanum doctor list' to see the diagnostic ids."));

            }

            return Result.Failure(UnknownSelector(selector));

        }

        return Result.Success();

    }

    private static Error UnknownSelector(string selector) =>
        new(
            DoctorErrors.UnknownSelectorCode,
            $"'{selector}' is not a known diagnostic id, subsystem, or repair id. Run 'arcanum doctor list' to see every id.");

    private static bool Matches(IDoctorCheck check, string selector) =>
        MatchesSelector(check.Id, check.Subsystem, selector);

    private static void RequireNamespacedId(string id, DoctorSubsystem subsystem, string kind)
    {

        string expected = NamespaceOf(subsystem) + ".";

        if (string.IsNullOrWhiteSpace(id)
            || !id.StartsWith(expected, StringComparison.Ordinal)
            || id.Length == expected.Length)
        {

            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Doctor {kind} id '{id}' must start with its subsystem namespace '{expected}'."));

        }

    }

}
