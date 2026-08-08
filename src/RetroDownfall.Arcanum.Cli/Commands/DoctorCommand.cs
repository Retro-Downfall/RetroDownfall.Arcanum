using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Diagnostics;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Diagnostics;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace RetroDownfall.Arcanum.Cli.Commands;

[ExcludeFromCodeCoverage] // Reason: interactive diagnostics command with environment-specific probes; not unit-testable without full host. The check, repair, and aggregation logic it composes lives in Core/Infrastructure and is covered there.
public sealed class DoctorCommand(
    IOptions<ArcanumSettings> options,
    IHttpClientFactory httpClientFactory,
    ISecretStore secretStore,
    IProviderCredentialStore providerCredentialStore,
    IWebResearchCredentialStore webResearchCredentialStore,
    IEncryptedBlobDiagnostics encryptedBlobDiagnostics,
    IThemePalette themePalette,
    ICliEnvironment cliEnvironment,
    IConsoleDispatcher consoleDispatcher,
    DoctorDiagnosticRunner diagnosticRunner,
    IConfirmationPrompt confirmationPrompt)
{

    private const string OkGlyph = "\u2713";

    private const string WarnGlyph = "!";

    private const string FailGlyph = "\u2717";

    /// <summary>
    /// Run subsystem diagnostics, plan safe repairs, and name the exact remediation command.
    /// </summary>
    /// <param name="request">Which checks to run, which repairs to plan, and whether to apply them.</param>
    /// <param name="fixPermissions">Legacy alias for applying the owner-only permission repair.</param>
    /// <param name="json">Emit the report as JSON to stdout for programmatic consumption.</param>
    public async Task<int> Run(
        DoctorRunRequest request,
        bool fixPermissions,
        bool json,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(request);

        // --fix-permissions applies exactly one narrowing, idempotent repair and has never prompted,
        // so automation that already passes it keeps working. Critically it grants that exemption to
        // itself alone: any other --repair the operator named on the same line still needs --apply
        // and still goes through confirmation, or the alias would become a way to silently
        // force-apply an unrelated repair.
        if (request.Apply && request.Repairs.Count > 0)
        {

            bool confirmed = await ConfirmRepairsAsync(request, json, cancellationToken).ConfigureAwait(false);

            if (!confirmed)
            {

                consoleDispatcher.WriteDiagnostic(
                    "Cancelled. Nothing was changed. Re-run without --apply to see the plan again.");

                return (int)CliExitCode.Success;

            }

        }

        Result<DoctorReport> report = fixPermissions
            ? await BuildWithPermissionRepairAsync(request, cancellationToken).ConfigureAwait(false)
            : await BuildReportAsync(request, cancellationToken).ConfigureAwait(false);

        if (report.IsFailure)
        {

            consoleDispatcher.WriteDiagnostic(report.Error.Message);

            return (int)CliExitCode.ConfigurationError;

        }

        // --fix-permissions kept its pre-#33 exit contract: it reports whether the repair succeeded,
        // not whether the whole installation is healthy. A fresh install has always had failing
        // local checks, and scripts that run it to harden permissions must not start failing.
        bool healthy = fixPermissions
            ? (report.Value.Repairs ?? []).All(
                static repair => repair.State != DoctorRepairState.Failed)
            : report.Value.Healthy;

        if (json)
        {

            consoleDispatcher.WriteJson(
                report.Value,
                ArcanumJsonContext.Default.DoctorReport);

            return healthy ? (int)CliExitCode.Success : (int)CliExitCode.GenericError;

        }

        return await RunHumanAsync(request, report.Value, healthy, cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// The <c>--fix-permissions</c> path: the operator's own request runs unchanged, and the
    /// owner-only repair is applied alongside it. Kept separate from the request rather than merged
    /// into <see cref="DoctorRunRequest.Repairs"/> so the alias can never flip another repair's
    /// <c>Apply</c>, which is exactly the bypass this shape exists to prevent.
    /// </summary>
    private async Task<Result<DoctorReport>> BuildWithPermissionRepairAsync(
        DoctorRunRequest request,
        CancellationToken cancellationToken)
    {

        Result<DoctorReport> report = await BuildReportAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (report.IsFailure)
        {

            return report;

        }

        IDoctorRepair? permissions = diagnosticRunner.Repairs.FirstOrDefault(
            static repair => repair.Id == PermissionApplyOwnerOnlyRepair.RepairId);

        if (permissions is null)
        {

            return report;

        }

        DoctorRepairResult applied;

        try
        {

            applied = await permissions.ApplyAsync(cancellationToken).ConfigureAwait(false);

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception exception)
        {

            applied = new DoctorRepairResult(
                permissions.Id,
                DoctorRepairState.Failed,
                "The owner-only permission repair could not be applied.",
                [],
                exception.GetType().Name);

        }

        return Result<DoctorReport>.Success(report.Value with
        {
            Repairs =
            [
                .. (report.Value.Repairs ?? []).Where(result => result.RepairId != permissions.Id),
                applied,
            ],
        });

    }

    /// <summary>
    /// <c>arcanum doctor list</c> \u2014 every diagnostic and repair id, so an operator can discover the
    /// exact value to pass to <c>--only</c>, <c>--skip</c>, or <c>--repair</c> instead of guessing.
    /// </summary>
    public Task<int> List(bool json, CancellationToken cancellationToken)
    {

        DoctorCatalog catalog = BuildCatalog();

        if (json)
        {

            consoleDispatcher.WriteJson(catalog, ArcanumJsonContext.Default.DoctorCatalog);

            return Task.FromResult((int)CliExitCode.Success);

        }

        Table checks = new();

        checks.Border(TableBorder.None);

        checks.HideHeaders();

        checks.AddColumn(new TableColumn(string.Empty).NoWrap());

        checks.AddColumn(new TableColumn(string.Empty).NoWrap());

        checks.AddColumn(new TableColumn(string.Empty));

        foreach (DoctorCatalogCheck check in catalog.Checks)
        {

            checks.AddRow(
                themePalette.HighlightMarkup(Markup.Escape(check.Id)),
                themePalette.MutedMarkup(Markup.Escape(check.Subsystem.ToString())),
                themePalette.TextMarkup(Markup.Escape(
                    check.RequiresNetwork ? check.Name + " (needs --include-network)" : check.Name)));

        }

        WritePanel("Diagnostics", checks);

        AnsiConsole.WriteLine();

        Table repairs = new();

        repairs.Border(TableBorder.None);

        repairs.HideHeaders();

        repairs.AddColumn(new TableColumn(string.Empty).NoWrap());

        repairs.AddColumn(new TableColumn(string.Empty));

        foreach (DoctorCatalogRepair repair in catalog.Repairs)
        {

            repairs.AddRow(
                themePalette.HighlightMarkup(Markup.Escape(repair.Id)),
                themePalette.TextMarkup(Markup.Escape(repair.Description)));

        }

        WritePanel("Repairs", repairs);

        return Task.FromResult((int)CliExitCode.Success);

    }

    /// <summary>
    /// <c>arcanum doctor explain &lt;id&gt;</c> \u2014 what one diagnostic reads, or what one repair
    /// changes and which detector justifies it.
    /// </summary>
    public Task<int> Explain(string id, bool json, CancellationToken cancellationToken)
    {

        DoctorCatalog catalog = BuildCatalog();

        DoctorCatalogCheck? check = catalog.Checks.FirstOrDefault(
            candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));

        DoctorCatalogRepair? repair = catalog.Repairs.FirstOrDefault(
            candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));

        if (check is null && repair is null)
        {

            consoleDispatcher.WriteDiagnostic(
                $"'{id}' is not a known diagnostic id, subsystem, or repair id. Run 'arcanum doctor list' to see every id.");

            return Task.FromResult((int)CliExitCode.ConfigurationError);

        }

        if (json)
        {

            consoleDispatcher.WriteJson(
                new DoctorCatalog(
                    check is null ? [] : [check],
                    repair is null ? [] : [repair]),
                ArcanumJsonContext.Default.DoctorCatalog);

            return Task.FromResult((int)CliExitCode.Success);

        }

        if (check is not null)
        {

            WritePanel(
                check.Id,
                BuildLabelTable(
                    ("Subsystem", check.Subsystem.ToString()),
                    ("Reports", check.Name),
                    ("Network", check.RequiresNetwork ? "yes \u2014 requires --include-network" : "no"),
                    ("Run it", $"arcanum doctor --only {check.Id}")));

        }

        if (repair is not null)
        {

            if (check is not null)
            {

                AnsiConsole.WriteLine();

            }

            WritePanel(
                repair.Id,
                BuildLabelTable(
                    ("Subsystem", repair.Subsystem.ToString()),
                    ("Changes", repair.Description),
                    ("Detected by", string.Join(", ", repair.DetectorIds)),
                    ("Plan it", $"arcanum doctor --repair {repair.Id}"),
                    ("Apply it", $"arcanum doctor --repair {repair.Id} --apply")));

        }

        return Task.FromResult((int)CliExitCode.Success);

    }

    private DoctorCatalog BuildCatalog() =>
        new(
            [
                .. LegacyDoctorChecks.Catalog,
                .. diagnosticRunner.Checks.Select(static check => new DoctorCatalogCheck(
                    check.Id,
                    check.Name,
                    check.Subsystem,
                    check.RequiresNetwork)),
            ],
            [
                .. diagnosticRunner.Repairs.Select(static repair => new DoctorCatalogRepair(
                    repair.Id,
                    repair.Subsystem,
                    repair.Description,
                    repair.DetectorIds)),
            ]);

    private async Task<bool> ConfirmRepairsAsync(
        DoctorRunRequest request,
        bool json,
        CancellationToken cancellationToken)
    {

        // Show the plan before asking. A confirmation prompt that does not say what will change is
        // not consent, and the plan is free \u2014 it is the same side-effect-free call --repair makes.
        Result<DoctorReport> plan = await BuildReportAsync(
                request with { Apply = false, Only = [], Skip = [] },
                cancellationToken)
            .ConfigureAwait(false);

        if (plan.IsSuccess && !json)
        {

            foreach (DoctorRepairResult result in plan.Value.Repairs ?? [])
            {

                WriteRepairPanel(result);

                AnsiConsole.WriteLine();

            }

        }

        return await confirmationPrompt
            .PromptForConfirmationAsync(
                $"Apply {request.Repairs.Count} repair(s) to this Arcanum installation?",
                cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// The legacy panel checks and the registered subsystem checks, in one report. The legacy twelve
    /// come first so the <c>--json</c> document keeps its historical head, and the registered checks
    /// are appended in registration order.
    /// </summary>
    private async Task<Result<DoctorReport>> BuildReportAsync(
        DoctorRunRequest request,
        CancellationToken cancellationToken)
    {

        Result<DoctorReport> registered = await diagnosticRunner
            .RunAsync(request, cancellationToken, LegacyDoctorChecks.Catalog)
            .ConfigureAwait(false);

        if (registered.IsFailure)
        {

            return registered;

        }

        // The pre-#33 checks come first so the --json document keeps its historical head, and they
        // resolve the selection themselves rather than being filtered afterwards — see
        // BuildLegacyChecksAsync for why that distinction matters.
        List<DoctorCheck> checks =
        [
            .. (await BuildLegacyChecksAsync(request, cancellationToken).ConfigureAwait(false))
                .Select(LegacyDoctorChecks.Enrich),
        ];

        checks.AddRange(registered.Value.Checks);

        if (checks.Count == 0)
        {

            // An empty report that says "healthy" is the worst possible answer: a CI gate would pass
            // on a diagnostic that inspected nothing at all.
            return Result<DoctorReport>.Failure(new Error(
                DoctorErrors.UnknownSelectorCode,
                "That combination of --only and --skip selects no diagnostics, so nothing would be "
                + "checked. Run 'arcanum doctor list' to see every id."));

        }

        DoctorOutcome outcome = DoctorOutcomes.Aggregate(
            checks.Select(static check => check.Outcome ?? DoctorOutcome.Healthy));

        IReadOnlyList<DoctorRepairResult> repairs = registered.Value.Repairs ?? [];

        bool healthy = !repairs.Any(static repair => repair.State == DoctorRepairState.Failed)
            && !DoctorOutcomes.IsFailure(outcome, request.Strict);

        return Result<DoctorReport>.Success(new DoctorReport(healthy, checks, outcome, repairs));

    }

    /// <summary>
    /// Whether a legacy check survives <c>--only</c> / <c>--skip</c>, using
    /// <see cref="DoctorDiagnosticRunner.MatchesSelector"/> so the two halves of the report can
    /// never disagree about what a selector covers.
    /// </summary>
    private static bool IsSelected(DoctorCheck check, DoctorRunRequest request) =>
        check.Id is null || check.Subsystem is null
            ? request.Only.Count == 0
            : IsSelected(check.Id, check.Subsystem.Value, request);

    private static bool IsSelected(string id, DoctorSubsystem subsystem, DoctorRunRequest request)
    {

        bool included = request.Only.Count == 0
            || request.Only.Any(selector =>
                DoctorDiagnosticRunner.MatchesSelector(id, subsystem, selector));

        bool excluded = request.Skip.Any(selector =>
            DoctorDiagnosticRunner.MatchesSelector(id, subsystem, selector));

        return included && !excluded;

    }

    private async Task<int> RunHumanAsync(
        DoctorRunRequest request,
        DoctorReport report,
        bool healthy,
        CancellationToken cancellationToken)
    {

        // Every panel is rendered from the report that was already computed. The decorated legacy
        // panels used to be written by a second, independent pass over the same probes, which meant
        // a plain `arcanum doctor` issued three /api/health requests instead of one and could print
        // a panel that disagreed with the summary underneath it — two samples, two answers.
        WriteVersionPanel();

        foreach (DoctorCheck check in report.Checks.Where(static check => check.Id is not null))
        {

            AnsiConsole.WriteLine();

            WriteFindingPanel(check);

        }

        foreach (DoctorRepairResult repair in report.Repairs ?? [])
        {

            AnsiConsole.WriteLine();

            WriteRepairPanel(repair);

        }

        AnsiConsole.WriteLine();

        WriteSummary(report, healthy, request.Strict);

        await Task.CompletedTask.ConfigureAwait(false);

        return healthy ? (int)CliExitCode.Success : (int)CliExitCode.GenericError;

    }

    private void WriteFindingPanel(DoctorCheck check)
    {

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        DoctorOutcome outcome = check.Outcome ?? DoctorOutcome.Healthy;

        table.AddRow(
            OutcomeGlyph(outcome),
            themePalette.TextMarkup(Markup.Escape(check.Detail ?? string.Empty)));

        foreach (DoctorRemedy remedy in check.Remedies ?? [])
        {

            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape("\u2192")),
                themePalette.HighlightLabelMarkup(
                    Markup.Escape(remedy.Command),
                    Markup.Escape(remedy.Explanation)));

        }

        WritePanel($"{check.Name} ({check.Id})", table);

    }

    private void WriteRepairPanel(DoctorRepairResult repair)
    {

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddRow(
            repair.State switch
            {
                DoctorRepairState.Applied or DoctorRepairState.AlreadyConverged =>
                    themePalette.HighlightMarkup(Markup.Escape(OkGlyph)),
                DoctorRepairState.Failed => themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
                _ => themePalette.MutedMarkup(Markup.Escape(WarnGlyph)),
            },
            themePalette.TextMarkup(Markup.Escape(repair.Summary)));

        foreach (DoctorRepairStep step in repair.Steps)
        {

            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape("\u00b7")),
                themePalette.MutedMarkup(Markup.Escape($"{step.Target}: {step.Before} \u2192 {step.After}")));

        }

        if (!string.IsNullOrWhiteSpace(repair.Failure))
        {

            table.AddRow(
                themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
                themePalette.ErrorMarkup(Markup.Escape(repair.Failure)));

        }

        WritePanel($"Repair: {repair.RepairId} ({repair.State})", table);

    }

    private void WriteSummary(DoctorReport report, bool healthy, bool strict)
    {

        int unhealthy = report.Checks.Count(static check => check.Outcome == DoctorOutcome.Unhealthy);

        int degraded = report.Checks.Count(static check =>
            check.Outcome is DoctorOutcome.Degraded or DoctorOutcome.Unavailable);

        int skipped = report.Checks.Count(static check => check.Outcome == DoctorOutcome.Skipped);

        Table table = BuildLabelTable(
            ("Outcome", (report.Outcome ?? DoctorOutcome.Healthy).ToString()),
            ("Checks", $"{report.Checks.Count} run, {unhealthy} unhealthy, {degraded} needing attention, {skipped} skipped"),
            ("Exit code", healthy ? "0" : "1"),
            ("Strict", strict ? "on \u2014 anything short of healthy exits nonzero" : "off \u2014 only an unhealthy check exits nonzero"),
            ("Discover ids", "arcanum doctor list"));

        WritePanel("Summary", table);

    }

    private string OutcomeGlyph(DoctorOutcome outcome) => outcome switch
    {
        DoctorOutcome.Unhealthy => themePalette.ErrorMarkup(Markup.Escape(FailGlyph)),
        DoctorOutcome.Degraded or DoctorOutcome.Unavailable =>
            themePalette.MutedMarkup(Markup.Escape(WarnGlyph)),
        DoctorOutcome.Skipped => themePalette.MutedMarkup(Markup.Escape("-")),
        _ => themePalette.HighlightMarkup(Markup.Escape(OkGlyph)),
    };

    /// <summary>
    /// The twelve pre-#33 checks, each run only when the selection actually wants it.
    ///
    /// <para>Per-check gating is not an optimization detail: several of these probe the network, load
    /// the tokenizer, scan encrypted blob storage, or read the OS keychain. Running the whole batch
    /// and filtering the results afterwards would mean <c>--skip host</c> still issued the HTTP
    /// request it was told to avoid — filtering results after paying for them is not filtering.</para>
    /// </summary>
    private async Task<IReadOnlyList<DoctorCheck>> BuildLegacyChecksAsync(
        DoctorRunRequest request,
        CancellationToken cancellationToken)
    {

        List<DoctorCheck> checks = [];

        void AddIfSelected(string id, DoctorSubsystem subsystem, Func<DoctorCheck> build)
        {

            if (IsSelected(id, subsystem, request))
            {

                checks.Add(build());

            }

        }

        AddIfSelected("system.version", DoctorSubsystem.System, BuildVersionCheck);

        AddIfSelected("paths.required", DoctorSubsystem.Paths, () => BuildPathsCheck().Check);

        AddIfSelected(
            "configuration.file",
            DoctorSubsystem.Configuration,
            () => BuildArcanumConfigCheck().Check);

        if (IsSelected("credentials.providers", DoctorSubsystem.Credentials, request))
        {

            checks.Add(
                await BuildProviderCredentialsCheckAsync(cancellationToken).ConfigureAwait(false));

        }

        AddIfSelected("mcp.global_config", DoctorSubsystem.Mcp, () => BuildMcpConfigCheck().Check);

        AddIfSelected("system.tokenizer", DoctorSubsystem.System, () => BuildTokenizerCheck().Check);

        AddIfSelected(
            "runtime.tool_child_sandbox",
            DoctorSubsystem.Runtime,
            BuildToolChildSandboxCheck);

        AddIfSelected("credentials.master_key", DoctorSubsystem.Credentials, BuildMasterKeyCheck);

        if (IsSelected("storage.file_encryption", DoctorSubsystem.Storage, request))
        {

            checks.Add((await BuildFileEncryptionCheckAsync(cancellationToken).ConfigureAwait(false)).Check);

        }

        AddIfSelected("weave.embeddings", DoctorSubsystem.Weave, BuildEmbeddingsCheck);

        // The durable-operation summary is derived from the same probe as API health, so the probe
        // runs when either is selected and is never issued twice.
        bool wantsApi = IsSelected("host.api_health", DoctorSubsystem.Host, request);

        bool wantsDurable = IsSelected("operations.durable_state", DoctorSubsystem.Operations, request);

        if (wantsApi || wantsDurable)
        {

            (_, DoctorCheck apiCheck, DoctorProbeResult apiProbe) =
                await BuildApiReachabilityCheckAsync(cancellationToken).ConfigureAwait(false);

            if (wantsApi)
            {

                checks.Add(apiCheck);

            }

            if (wantsDurable)
            {

                checks.Add(
                    BuildDurableOperationsCheck(apiProbe.Kind == DoctorProbeKind.Ok, apiProbe.Detail));

            }

        }

        return checks;

    }

    private void WriteVersionPanel()
    {

        (string version, Table table) = BuildVersionPanelCore();

        WritePanel("System", table);

    }

    private (string Version, Table Table) BuildVersionPanelCore()
    {

        string version = RetroDownfall.Arcanum.Core.ArcanumBuildInfo.InformationalVersion;

        int plus = version.IndexOf('+');

        if (plus >= 0)
        {
            version = version[..plus];
        }

        Table table = BuildLabelTable(
            ("Arcanum", version),
            ("OS", RuntimeInformation.OSDescription),
            ("Runtime", RuntimeInformation.FrameworkDescription),
            ("Process arch", RuntimeInformation.ProcessArchitecture.ToString()),
            ("Interactive TTY", cliEnvironment.IsInteractive ? "yes" : "no (piped)"),
            ("Color enabled", cliEnvironment.ColorEnabled ? "yes" : "no"));

        return (version, table);

    }

    private DoctorCheck BuildVersionCheck()
    {

        (string version, _) = BuildVersionPanelCore();

        return new DoctorCheck(
            "Version",
            "ok",
            $"Arcanum {version}, OS {RuntimeInformation.OSDescription}, Runtime {RuntimeInformation.FrameworkDescription}");

    }

    /// <summary>
    /// The paths Arcanum cannot run without. The database and the secret store are created by the
    /// first <c>arcanum serve</c>, not by a directory repair, so the remedy names the command that
    /// actually produces them — recommending a repair that cannot clear the finding is worse than
    /// recommending nothing.
    /// </summary>
    private (bool Healthy, DoctorCheck Check) BuildPathsCheck()
    {

        List<string> missing = [];

        foreach ((string label, string path) in new[]
        {
            ("Grimoire directory", ArcanumPaths.GrimoireDirectory),
            ("Grimoire database", ArcanumPaths.GrimoireDatabaseFile),
            ("API key store", ArcanumPaths.ApiKeyStoreFile),
        })
        {

            bool exists = path == ArcanumPaths.GrimoireDirectory
                ? Directory.Exists(path)
                : File.Exists(path);

            if (!exists)
            {

                missing.Add(label);

            }

        }

        if (missing.Count == 0)
        {

            return (true, new DoctorCheck("Paths", "ok", "Every required path is present."));

        }

        return (
            false,
            new DoctorCheck(
                "Paths",
                "fail",
                $"{missing.Count} required path(s) missing: {string.Join(", ", missing)}. "
                + "They are created the first time the host starts."));

    }

    private (bool Healthy, DoctorCheck Check) BuildArcanumConfigCheck()
    {

        string configFile = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json");

        if (!File.Exists(configFile))
        {

            return (true, new DoctorCheck("Configuration", "warn", $"{configFile} not found (optional)"));

        }

        try
        {

            ConfigurationBootstrapper.ValidateArcanumConfigurationFile(configFile);

            return (true, new DoctorCheck("Configuration", "ok", $"{configFile} valid JSON"));

        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {

            return (false, new DoctorCheck("Configuration", "fail", $"{configFile}: {ex.Message}"));

        }

    }

    private async Task<DoctorCheck> BuildProviderCredentialsCheckAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CredentialReferenceStatus> references =
            await BuildCredentialReferenceStatusesAsync(cancellationToken).ConfigureAwait(false);
        int present = references.Count(static reference => reference.IsPresent);
        int corrupt = references.Count(static reference => reference.IsCorrupt);
        int missingExplicit = references.Count(
            static reference => reference.IsExplicit && !reference.IsPresent);

        return new DoctorCheck(
            "ProviderCredentials",
            missingExplicit == 0 && corrupt == 0 ? "ok" : "warn",
            $"{present}/{references.Count} credential(s) resolve from an environment reference or "
            + $"the secure store; {missingExplicit} explicit reference(s) are missing; "
            + $"{corrupt} stored credential(s) are corrupt; values are never shown.");
    }

    /// <summary>
    /// Reports presence and source for every credential identity this installation actually uses.
    /// Resolution order matches the run time (<see cref="IProviderApiKeyResolver"/>): the environment
    /// reference first, then the OS-backed secure store. Values are never read into the report.
    /// </summary>
    private async Task<IReadOnlyList<CredentialReferenceStatus>> BuildCredentialReferenceStatusesAsync(
        CancellationToken cancellationToken)
    {
        List<CredentialReferenceStatus> references = [];

        foreach (ProviderSettings provider in options.Value.Providers ?? [])
        {
            string environmentVariable =
                EnvironmentCredentialResolver
                    .GetProviderApiKeyEnvironmentVariableName(provider);
            string label = string.IsNullOrWhiteSpace(provider.Name)
                ? "Unnamed provider"
                : provider.Name;
            bool explicitReference =
                !string.IsNullOrWhiteSpace(provider.CredentialEnvironmentVariable);

            if (EnvironmentCredentialResolver.ResolveProviderApiKey(provider) is not null)
            {
                references.Add(new CredentialReferenceStatus(
                    label,
                    environmentVariable,
                    IsPresent: true,
                    explicitReference,
                    IsCorrupt: false,
                    "set from the environment reference (value not shown)"));

                continue;
            }

            SecretStoreReadResult stored = string.IsNullOrWhiteSpace(provider.Name)
                ? SecretStoreReadResult.Missing()
                : await providerCredentialStore
                    .GetApiKeyReadResultAsync(provider.Name, cancellationToken)
                    .ConfigureAwait(false);

            references.Add(new CredentialReferenceStatus(
                label,
                environmentVariable,
                stored.Status == SecretStoreReadStatus.Ok,
                explicitReference,
                stored.Status == SecretStoreReadStatus.Corrupted,
                stored.Status switch
                {
                    SecretStoreReadStatus.Ok => "set in the secure store (value not shown)",
                    SecretStoreReadStatus.Corrupted =>
                        "stored but undecryptable; re-run 'arcanum setup' or "
                        + $"'arcanum key provider set {label}'",
                    _ => "not set",
                }));
        }

        WebBrowsingSettings webResearch = options.Value.ResolveWebBrowsing();

        if (webResearch.Enabled)
        {
            string environmentVariable =
                EnvironmentCredentialResolver
                    .GetWebResearchApiKeyEnvironmentVariableName(webResearch);

            if (EnvironmentCredentialResolver.ResolveWebResearchApiKey(webResearch) is not null)
            {
                references.Add(new CredentialReferenceStatus(
                    "Web research",
                    environmentVariable,
                    IsPresent: true,
                    !string.IsNullOrWhiteSpace(webResearch.CredentialEnvironmentVariable),
                    IsCorrupt: false,
                    "set from the environment reference (value not shown)"));
            }
            else
            {
                SecretStoreReadResult stored = await webResearchCredentialStore
                    .GetPerplexityApiKeyReadResultAsync(cancellationToken)
                    .ConfigureAwait(false);

                references.Add(new CredentialReferenceStatus(
                    "Web research",
                    environmentVariable,
                    stored.Status == SecretStoreReadStatus.Ok,
                    !string.IsNullOrWhiteSpace(webResearch.CredentialEnvironmentVariable),
                    stored.Status == SecretStoreReadStatus.Corrupted,
                    stored.Status switch
                    {
                        SecretStoreReadStatus.Ok => "set in the secure store (value not shown)",
                        SecretStoreReadStatus.Corrupted =>
                            "stored but undecryptable; re-run 'arcanum setup' or "
                            + "'arcanum key provider set perplexity'",
                        _ => "not set",
                    }));
            }
        }

        HttpsSettings https = options.Value.Host?.Https ?? new HttpsSettings();

        if (https.Enabled && string.IsNullOrWhiteSpace(https.PrivateKeyPath))
        {
            string environmentVariable =
                EnvironmentCredentialResolver
                    .GetHttpsCertificatePasswordEnvironmentVariableName(https);
            bool httpsPresent =
                EnvironmentCredentialResolver.ResolveHttpsCertificatePassword(https) is not null;

            references.Add(new CredentialReferenceStatus(
                "HTTPS PFX",
                environmentVariable,
                httpsPresent,
                !string.IsNullOrWhiteSpace(
                    https.CertificatePasswordEnvironmentVariable),
                IsCorrupt: false,
                httpsPresent
                    ? "set from the environment reference (value not shown)"
                    : "not set (environment reference only)"));
        }

        return references;
    }

    private (bool Healthy, DoctorCheck Check) BuildMcpConfigCheck()
    {

        string globalMcpPath = ArcanumPaths.GlobalMcpConfigFile;

        if (!File.Exists(globalMcpPath))
        {

            return (true, new DoctorCheck("MCP", "warn", $"{globalMcpPath} not found (optional)"));

        }

        try
        {
            byte[] raw = File.ReadAllBytes(globalMcpPath);

            using JsonDocument doc = JsonDocument.Parse(raw);

            int serverCount = 0;

            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("mcpServers", out JsonElement servers)
                && servers.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty _ in servers.EnumerateObject())
                {
                    serverCount++;
                }
            }

            return (true, new DoctorCheck("MCP", "ok", $"{serverCount} server entry/entries parsed"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (false, new DoctorCheck("MCP", "fail", $"{globalMcpPath}: {ex.Message}"));
        }

    }

    private (bool Healthy, DoctorCheck Check) BuildTokenizerCheck()
    {

        string encoding = string.IsNullOrWhiteSpace(
            options.Value.ResolveIntelligence().TokenizerEncoding)
            ? "o200k_base"
            : options.Value.ResolveIntelligence().TokenizerEncoding.Trim();

        try
        {
            Tokenizer tokenizer = TiktokenTokenizer.CreateForEncoding(encoding);

            int tokenCount = tokenizer.CountTokens("arcanum doctor smoke test");

            return (true, new DoctorCheck("Tokenizer", "ok", $"{encoding} smoke test counted {tokenCount} tokens"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, new DoctorCheck("Tokenizer", "fail", $"{encoding} failed: {ex.Message}"));
        }

    }

    private DoctorCheck BuildToolChildSandboxCheck()
    {

        bool escapeHatch = options.Value.Security?.AllowUnsandboxedToolChildren ?? false;

        ToolChildSandboxStatus status = ToolChildSandboxCapabilityReporter.BuildForCurrentHost(escapeHatch);

        string checkStatus = status.IsHealthDegraded ? "warn" : "ok";

        string detail =
            $"{status.Platform}; FS={status.FilesystemJailMode}; rlimits={status.ResourceLimitsMode}; "
            + $"network={status.NetworkIsolationMode}; escapeHatch={status.EscapeHatchEnabled}. "
            + status.OperatorGuidance;

        return new DoctorCheck("ToolChildSandbox", checkStatus, detail);

    }

    private DoctorCheck BuildMasterKeyCheck()
    {

        (bool present, string detail, string guidance) = ProbeMasterKeyPresence();

        return new DoctorCheck(
            "MasterApiKey",
            present ? "ok" : "warn",
            $"{detail} {guidance}");

    }

    private async Task<(bool Healthy, DoctorCheck Check)> BuildFileEncryptionCheckAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            FileEncryptionDiagnostics result = await encryptedBlobDiagnostics
                .InspectAsync(cancellationToken)
                .ConfigureAwait(false);
            return (
                result.IsHealthy,
                new DoctorCheck(
                    "FileEncryption",
                    result.IsHealthy ? "ok" : "fail",
                    result.Detail));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (
                false,
                new DoctorCheck(
                    "FileEncryption",
                    "fail",
                    $"File-encryption diagnostics failed ({ex.GetType().Name})."));
        }
    }

    private (bool Present, string Detail, string Guidance) ProbeMasterKeyPresence()
    {

        try
        {

            // Synchronous probe via secret store — never print the key.
            string? key = secretStore.GetApiKeyAsync().GetAwaiter().GetResult();

            if (!string.IsNullOrWhiteSpace(key))
            {

                return (
                    true,
                    "Master API key is readable from the Arcanum secret store.",
                    "Use `arcanum key show` only when you need to copy it; Prefer The Forge OS credential store for desktop.");

            }

        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {

            return (
                false,
                $"Could not read master API key ({ex.GetType().Name}).",
                BuildKeyRecoveryGuidance());

        }

        return (
            false,
            "Master API key is not present or not readable.",
            BuildKeyRecoveryGuidance());

    }

    private static string BuildKeyRecoveryGuidance()
    {

        if (OperatingSystem.IsLinux())
        {

            return "Run `arcanum key set`, install/start Secret Service (libsecret) for The Forge, "
                + "or set THEFORGE_ARCANUM_KEY as a private-beta process-only workaround.";

        }

        return "Run `arcanum key set` or paste the key in The Forge when prompted. "
            + "Optional private-beta override: THEFORGE_ARCANUM_KEY (process-only, not persisted).";

    }

    private (bool InformationalOk, Table Table) BuildEmbeddingsPanelCore()
    {

        EmbeddingSettings embeddings = options.Value.ResolveEmbeddings();

        bool enabled = embeddings.Enabled;

        string provider = string.IsNullOrWhiteSpace(embeddings.Provider) ? "(none)" : embeddings.Provider.Trim();

        string model = string.IsNullOrWhiteSpace(embeddings.Model) ? "(none)" : embeddings.Model.Trim();

        // Doctor runs outside the API host process — report configured posture + managed beta default.
        string mode = !enabled
            ? "disabled"
            : (string.IsNullOrWhiteSpace(embeddings.Provider) || string.IsNullOrWhiteSpace(embeddings.Model)
                ? "unavailable"
                : "managed");

        string guidance = mode == "managed"
            ? "When the API host is running without sqlite-vec, Divination uses managed SIMD fallback "
              + "and streams the complete candidate set through bounded top-K memory. "
              + "Confirm live mode via GET /api/meta embeddingsVectorMode."
            : mode == "disabled"
                ? "Set Arcanum:Features:Embeddings=true and configure Arcanum:Integrations:Embeddings:Provider and Arcanum:Integrations:Embeddings:Model to enable The Weave."
                : "Configure Arcanum:Integrations:Embeddings:Provider and Arcanum:Integrations:Embeddings:Model.";

        Table table = BuildLabelTable(
            ("Enabled", enabled ? "yes" : "no"),
            ("Provider", provider),
            ("Model", model),
            ("Vector mode (configured default)", mode),
            ("Managed row budget", "none (complete streamed scan)"),
            ("Guidance", guidance));

        return (true, table);

    }

    private DoctorCheck BuildEmbeddingsCheck()
    {

        EmbeddingSettings embeddings = options.Value.ResolveEmbeddings();

        string mode = !embeddings.Enabled
            ? "disabled"
            : (string.IsNullOrWhiteSpace(embeddings.Provider) || string.IsNullOrWhiteSpace(embeddings.Model)
                ? "unavailable"
                : "managed");

        return new DoctorCheck(
            "Embeddings",
            mode is "disabled" or "managed" ? "ok" : "warn",
            $"enabled={embeddings.Enabled}; mode={mode}; budget=none");

    }

    /// <summary>
    /// Pure mapping from the health probe to the <c>DurableOperations</c> check. Degrades to a
    /// warning naming the repair verb whenever the host could not be reached.
    /// </summary>
    internal static DoctorCheck BuildDurableOperationsCheck(bool hostReachable, string? detail)
    {

        if (!hostReachable)
        {

            return new DoctorCheck(
                "DurableOperations",
                "warn",
                "Host unreachable, so durable-operation state could not be read. Start 'arcanum serve', then re-run 'arcanum doctor' or 'arcanum operation list --state ReconciliationRequired'.");

        }

        if (string.IsNullOrWhiteSpace(detail))
        {

            return new DoctorCheck(
                "DurableOperations",
                "warn",
                "The host did not report durable-operation state. Inspect it with 'arcanum operation list'.");

        }

        return new DoctorCheck(
            "DurableOperations",
            "ok",
            detail
                + " Repair anything stale or requiring reconciliation with 'arcanum operation list --state ReconciliationRequired'.");

    }

    private async Task<(bool Healthy, DoctorCheck Check, DoctorProbeResult Probe)> BuildApiReachabilityCheckAsync(CancellationToken cancellationToken)
    {

        HostSettings host = options.Value.Host;

        bool listenAny = ArcanumEnvironment.IsHostAnyEnabled(host.ListenAny);

        int timeoutSeconds = ArcanumSettingClamps.DoctorHealthTimeoutSeconds(
            ArcanumRuntimeDefaults.CliDoctorHealthTimeoutSeconds);

        string targetUrl = ArcanumLocalApiAddress.ResolveHealthProbeUrl(host);

        HttpClient client = httpClientFactory.CreateClient(ArcanumApiClient.RequestHttpClientName);

        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        DoctorProbeResult probe = await ProbeApiReachabilityAsync(
                client,
                new Uri(targetUrl),
                apiKey,
                timeoutSeconds,
                cancellationToken)
            .ConfigureAwait(false);

        (bool Healthy, DoctorCheck Check) mapped = probe.Kind switch
        {
            DoctorProbeKind.Ok => (true, new DoctorCheck(
                "API Health",
                "ok",
                $"Reachable: {targetUrl} (HTTP {probe.HttpStatus}); "
                + $"DurableOperations: {probe.Detail ?? "status unavailable"}")),
            DoctorProbeKind.Unauthorized => (false, new DoctorCheck("API Health", "fail", $"Reached {targetUrl} but auth failed (HTTP {probe.HttpStatus})")),
            DoctorProbeKind.UnexpectedStatus => (false, new DoctorCheck("API Health", "fail", $"{targetUrl} returned HTTP {probe.HttpStatus}")),
            DoctorProbeKind.Timeout => (true, new DoctorCheck("API Health", "warn", $"Timed out after {timeoutSeconds}s: {targetUrl}")),
            DoctorProbeKind.Unreachable => (true, new DoctorCheck(
                "API Health",
                "warn",
                listenAny
                    ? $"Not reachable: {targetUrl} (ListenAny is HTTPS-only; trust the TLS certificate or use a SAN that matches localhost)"
                    : $"Not reachable: {targetUrl}")),
            DoctorProbeKind.Cancelled => (true, new DoctorCheck("API Health", "warn", "Cancelled by operator")),
            _ => (false, new DoctorCheck("API Health", "fail", "Unknown probe result")),
        };

        return (mapped.Healthy, mapped.Check, probe);

    }

    private async Task<DoctorProbeResult> ProbeApiReachabilityAsync(
        HttpClient client,
        Uri healthUrl,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {

        Task<DoctorProbeResult> RunProbeAsync()
        {
            return ProbeApiReachabilityInnerAsync(client, healthUrl, apiKey, timeoutSeconds, cancellationToken);
        }

        if (!cliEnvironment.IsInteractive || !cliEnvironment.ColorEnabled)
        {
            return await RunProbeAsync().ConfigureAwait(false);
        }

        DoctorProbeResult result = default;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(themePalette.HighlightStyle())
            .StartAsync(
                $"Probing /api/health (timeout {timeoutSeconds}s)...",
                async _ =>
                {
                    result = await RunProbeAsync().ConfigureAwait(false);
                })
            .ConfigureAwait(false);

        return result;

    }

    private static async Task<DoctorProbeResult> ProbeApiReachabilityInnerAsync(
        HttpClient client,
        Uri healthUrl,
        string? apiKey,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {

        try
        {
            HealthProbeResult probe = await ArcanumHealthProbe
                .ProbeAsync(
                    client,
                    healthUrl,
                    apiKey,
                    TimeSpan.FromSeconds(timeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);

            return MapHealthProbe(probe);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DoctorProbeResult(DoctorProbeKind.Cancelled, 0, null);
        }

    }

    private static DoctorProbeResult MapHealthProbe(HealthProbeResult probe) =>
        probe.State switch
        {
            HealthProbeState.Healthy => new DoctorProbeResult(
                DoctorProbeKind.Ok,
                probe.StatusCode ?? 200,
                probe.DurableOperationsDetail),
            HealthProbeState.Unauthorized => new DoctorProbeResult(
                DoctorProbeKind.Unauthorized,
                probe.StatusCode ?? 401,
                null),
            HealthProbeState.UnhealthyStatus => new DoctorProbeResult(
                DoctorProbeKind.UnexpectedStatus,
                probe.StatusCode ?? 0,
                probe.Error),
            HealthProbeState.Timeout => new DoctorProbeResult(DoctorProbeKind.Timeout, 0, probe.Error),
            HealthProbeState.ConnectionRefused
                or HealthProbeState.NetworkUnreachable
                or HealthProbeState.DnsFailure
                or HealthProbeState.TlsFailure => new DoctorProbeResult(
                    DoctorProbeKind.Unreachable,
                    0,
                    probe.Error),
            _ => new DoctorProbeResult(DoctorProbeKind.Unreachable, 0, probe.Error),
        };

    private Table BuildLabelTable(params (string Label, string Value)[] rows)
    {

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        foreach ((string label, string value) in rows)
        {
            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape(label + ":")),
                themePalette.TextMarkup(Markup.Escape(value)));
        }

        return table;

    }

    private void WritePanel(string title, IRenderable content)
    {

        Panel panel = new(content)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape(title))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HeadingStyle(),
            Padding = new Padding(1, 0, 1, 0),
            Expand = true,
        };

        AnsiConsole.Write(panel);

    }

    private readonly record struct CredentialReferenceStatus(
        string Label,
        string EnvironmentVariable,
        bool IsPresent,
        bool IsExplicit,
        bool IsCorrupt,
        string Detail);

    private enum DoctorProbeKind
    {

        Ok,

        Unauthorized,

        UnexpectedStatus,

        Timeout,

        Unreachable,

        Cancelled,
    }

    private readonly record struct DoctorProbeResult(DoctorProbeKind Kind, int HttpStatus, string? Detail);

}
