using System.Collections.Immutable;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Cli.Commands.Configuration;

internal sealed class PresetCommands(
    IConfigurationPresetService presetService,
    IConsoleDispatcher console,
    ICliInvocationContext invocationContext,
    IGrimoireCliInitialization initialization)
{

    public async Task<int> List(CancellationToken cancellationToken)
    {

        IReadOnlyList<ConfigurationPresetDefinition> definitions = presetService.List();

        ConfigurationPresetInspection? inspection = await InspectOrReportAsync(
                cancellationToken)
            .ConfigureAwait(false);

        ConfigurationPresetListItemPayload[] items = definitions
            .Select(definition => BuildListItem(definition, inspection))
            .ToArray();

        ConfigurationPresetListPayload payload = new(
            items,
            inspection?.CompletionSummary);

        if (invocationContext.Options.Json)
        {

            console.WriteJson(
                payload,
                CliJsonContext.Default.ConfigurationPresetListPayload);

        }
        else
        {

            WriteList(payload);

        }

        return (int)CliExitCode.Success;

    }

    public async Task<int> Show(
        string name,
        CancellationToken cancellationToken)
    {

        ConfigurationPresetDefinition? definition = presetService.Find(name);

        if (definition is null)
        {

            return UnknownPreset(name);

        }

        ConfigurationPresetInspection? inspection = await InspectOrReportAsync(
                cancellationToken)
            .ConfigureAwait(false);

        bool isActive = IsActive(definition, inspection);

        ConfigurationPresetShowPayload payload = new(
            RedactDefinition(definition),
            EffectiveState(definition, inspection),
            isActive,
            presetService.Glossary().ToArray());

        if (invocationContext.Options.Json)
        {

            console.WriteJson(
                payload,
                CliJsonContext.Default.ConfigurationPresetShowPayload);

        }
        else
        {

            WriteShow(payload);

        }

        return (int)CliExitCode.Success;

    }

    public async Task<int> Diff(
        string name,
        CancellationToken cancellationToken)
    {

        Result<ConfigurationPresetPlan> result = await RunPresetInteractionAsync(
                token => presetService.DiffAsync(name, token),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return Failure(result.Error);

        }

        ConfigurationPresetPlan plan = RedactPlan(result.Value);

        if (invocationContext.Options.Json)
        {

            console.WriteJson(
                plan,
                CliJsonContext.Default.ConfigurationPresetPlan);

        }
        else
        {

            WritePlan(plan);

        }

        return (int)CliExitCode.Success;

    }

    public async Task<int> Apply(
        string name,
        CancellationToken cancellationToken)
    {

        Result<ConfigurationPresetApplyResult> result = await RunPresetInteractionAsync(
                token => presetService.ApplyAsync(name, token),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return Failure(result.Error);

        }

        ConfigurationPresetApplyResult applied = RedactApplyResult(result.Value);

        if (invocationContext.Options.Json)
        {

            console.WriteJson(
                applied,
                CliJsonContext.Default.ConfigurationPresetApplyResult);

        }
        else
        {

            WriteApply(applied);

        }

        return ApplyExitCode(applied);

    }

    public async Task<int> Reset(CancellationToken cancellationToken)
    {

        Result<ConfigurationPresetResetResult> result = await RunPresetInteractionAsync(
                presetService.ResetAsync,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            return Failure(result.Error);

        }

        ConfigurationPresetResetResult reset = RedactResetResult(result.Value);

        if (invocationContext.Options.Json)
        {

            console.WriteJson(
                reset,
                CliJsonContext.Default.ConfigurationPresetResetResult);

        }
        else
        {

            WriteReset(reset);

        }

        return reset.RollbackStatus == ConfigurationPresetRollbackStatus.Failed
            ? (int)CliExitCode.ConfigurationError
            : (int)CliExitCode.Success;

    }

    private async Task<ConfigurationPresetInspection?> InspectOrReportAsync(
        CancellationToken cancellationToken)
    {

        Result<ConfigurationPresetInspection> result = await RunPresetInteractionAsync(
                presetService.InspectAsync,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {

            return result.Value;

        }

        console.WriteDiagnostic(
            $"Effective preset state is unavailable: {result.Error.Message}");

        return null;

    }

    private Task<T> RunPresetInteractionAsync<T>(
        Func<CancellationToken, Task<T>> interaction,
        CancellationToken cancellationToken) =>
        initialization.RunExclusiveAsync(
            (_, token) => interaction(token),
            cancellationToken);

    private void WriteList(ConfigurationPresetListPayload payload)
    {

        console.WritePayload("Onboarding presets");

        foreach (ConfigurationPresetListItemPayload item in payload.Presets)
        {

            console.WritePayload(
                $"{item.Id} v{item.Version} — {item.DisplayName} [{item.EffectiveState}]");

            console.WritePayload($"  {item.Purpose}");

        }

        console.WritePayload(
            "Use 'arcanum preset show <name>' for settings, prerequisites, and disclosures.");

    }

    private void WriteShow(ConfigurationPresetShowPayload payload)
    {

        ConfigurationPresetDefinition preset = payload.Preset;

        console.WritePayload(
            $"{preset.DisplayName} ({preset.Id} v{preset.Version}) [{payload.EffectiveState}]");

        console.WritePayload($"Purpose: {preset.Purpose}");

        console.WritePayload($"Enables: {preset.Disclosure.Enables}");

        console.WritePayload($"Disables: {preset.Disclosure.Disables}");

        console.WritePayload(
            $"Security implications: {preset.Disclosure.SecurityImplications}");

        console.WritePayload(
            $"Provider requirements: {preset.Disclosure.ProviderRequirements}");

        console.WritePayload(
            $"Resource and cost behavior: {preset.Disclosure.ResourceAndCostBehavior}");

        console.WritePayload("Owned settings:");

        foreach (ConfigurationPresetOwnedSetting setting in preset.OwnedSettings)
        {

            console.WritePayload(
                $"  {setting.Path} = {setting.CanonicalJson} "
                + $"(restart: {YesNo(setting.RequiresRestart)})");

        }

        WritePrerequisiteDefinitions(preset.Prerequisites);

        console.WritePayload(
            $"Essential first choice: {preset.ProgressiveDisclosure.EssentialChoice}");

        if (!preset.ProgressiveDisclosure.DeferredFeatures.IsDefaultOrEmpty)
        {

            console.WritePayload(
                "Deferred advanced features: "
                + string.Join(", ", preset.ProgressiveDisclosure.DeferredFeatures));

        }

        console.WritePayload(
            "After first success: "
            + preset.ProgressiveDisclosure.FirstSuccessRecommendation);

        if (!preset.Recommendations.IsDefaultOrEmpty)
        {

            console.WritePayload("Recommendations:");

            foreach (ConfigurationPresetRecommendation recommendation in preset.Recommendations)
            {

                string advanced = recommendation.IsAdvancedFeature ? " [advanced]" : string.Empty;

                console.WritePayload(
                    $"  {recommendation.Description}{advanced}: {recommendation.Command}");

            }

        }

        console.WritePayload("Plain-language glossary:");

        foreach (ConfigurationPresetGlossaryEntry entry in payload.Glossary)
        {

            console.WritePayload(
                $"  {entry.Term} — {entry.PlainLanguageMeaning}");

        }

    }

    private void WritePlan(ConfigurationPresetPlan plan)
    {

        console.WritePayload(
            $"Preset diff: {plan.Preset.DisplayName} ({plan.Preset.Id} v{plan.Preset.Version})");

        console.WritePayload($"Effective state: {plan.State}");

        console.WritePayload($"Applicable: {YesNo(plan.IsApplicable)}");

        console.WritePayload($"Idempotent: {YesNo(plan.IsIdempotent)}");

        WritePrerequisites(plan.Prerequisites);

        if (plan.Diff.IsDefaultOrEmpty)
        {

            console.WritePayload("No owned setting changes.");

        }
        else
        {

            console.WritePayload("Owned setting diff:");

            foreach (ConfigurationPresetDiffRow row in plan.Diff)
            {

                WriteDiffRow(row);

            }

        }

        WriteCompletionSummary(plan.CompletionSummary);

    }

    private void WriteApply(ConfigurationPresetApplyResult result)
    {

        if (result.AlreadyApplied)
        {

            console.WritePayload(
                "Preset already applied at this version; configuration is unchanged.");

        }
        else if (result.Applied)
        {

            console.WritePayload("Preset applied atomically.");

        }
        else
        {

            console.WritePayload("Preset was not applied.");

        }

        console.WritePayload($"Rollback status: {result.RollbackStatus}");

        WritePrerequisites(result.Plan.Prerequisites);

        WriteCompletionSummary(result.Inspection.CompletionSummary);

    }

    private void WriteReset(ConfigurationPresetResetResult result)
    {

        console.WritePayload(
            result.Reset
                ? "Active preset baseline restored."
                : "No active preset baseline required restoration.");

        console.WritePayload($"Restored settings: {result.RestoredSettingCount}");

        console.WritePayload($"Preserved user drift: {result.PreservedDriftCount}");

        console.WritePayload($"Rollback status: {result.RollbackStatus}");

        WriteCompletionSummary(result.Inspection.CompletionSummary);

    }

    private void WriteDiffRow(ConfigurationPresetDiffRow row)
    {

        console.WritePayload($"  {row.Path}");

        console.WritePayload($"    Persisted value: {row.PersistedValue}");

        console.WritePayload($"    Effective value: {row.EffectiveValue}");

        console.WritePayload(
            $"    Proposed persisted value: {row.ProposedPersistedValue}");

        console.WritePayload($"    Current source: {row.CurrentSource}");

        console.WritePayload(
            "    Environment override: "
            + (row.EnvironmentVariable is null
                ? "none"
                : $"{row.EnvironmentVariable} "
                    + $"(effective: {YesNo(row.EnvironmentOverrideIsEffective)})"));

        console.WritePayload($"    Owned by preset: {YesNo(row.OwnedByPreset)}");

        console.WritePayload(
            "    Prerequisites: "
            + (row.PrerequisiteIds.IsDefaultOrEmpty
                ? "none"
                : string.Join(", ", row.PrerequisiteIds)));

        console.WritePayload($"    Restart required: {YesNo(row.RequiresRestart)}");

        console.WritePayload(
            $"    Persisted value changes: {YesNo(row.PersistedValueChanges)}");

        console.WritePayload(
            $"    Effective value changes: {YesNo(row.EffectiveValueChanges)}");

    }

    private void WritePrerequisiteDefinitions(
        IEnumerable<ConfigurationPresetPrerequisite> prerequisites)
    {

        ConfigurationPresetPrerequisite[] materialized = prerequisites.ToArray();

        if (materialized.Length == 0)
        {

            console.WritePayload("Prerequisites: none");

            return;

        }

        console.WritePayload("Prerequisites:");

        foreach (ConfigurationPresetPrerequisite prerequisite in materialized)
        {

            string requirement = prerequisite.Required ? "required" : "optional";

            console.WritePayload(
                $"  [{requirement}] {prerequisite.Description}");

            console.WritePayload($"    Setup: {prerequisite.ResolutionCommand}");

        }

    }

    private void WritePrerequisites(
        IEnumerable<ConfigurationPresetPrerequisiteStatus> prerequisites)
    {

        ConfigurationPresetPrerequisiteStatus[] materialized = prerequisites.ToArray();

        if (materialized.Length == 0)
        {

            console.WritePayload("Prerequisites: none");

            return;

        }

        console.WritePayload("Prerequisites:");

        foreach (ConfigurationPresetPrerequisiteStatus status in materialized)
        {

            string state = status.IsSatisfied ? "ready" : "missing";

            string requirement = status.Prerequisite.Required ? "required" : "optional";

            console.WritePayload(
                $"  [{state}, {requirement}] {status.Prerequisite.Description}");

            console.WritePayload($"    Detail: {status.Detail}");

            if (!status.IsSatisfied)
            {

                console.WritePayload(
                    $"    Setup: {status.Prerequisite.ResolutionCommand}");

            }

        }

    }

    private void WriteCompletionSummary(ConfigurationPresetCompletionSummary summary)
    {

        console.WritePayload("Setup completion summary");

        console.WritePayload($"  Active preset: {summary.ActivePreset}");

        console.WritePayload($"  Provider/model: {summary.ProviderAndModel}");

        console.WritePayload($"  Workspace/campaign: {summary.WorkspaceAndCampaign}");

        console.WritePayload(
            "  Enabled memory sources: "
            + (summary.EnabledMemorySources.IsDefaultOrEmpty
                ? "none"
                : string.Join(", ", summary.EnabledMemorySources)));

        console.WritePayload($"  Tool policy: {summary.ToolPolicy}");

        console.WritePayload($"  Privacy state: {summary.PrivacyState}");

        console.WritePayload(
            $"  Next recommended command: {summary.NextRecommendedCommand}");

    }

    private int UnknownPreset(string name)
    {

        string available = string.Join(
            ", ",
            presetService.List().Select(static preset => preset.Id));

        console.WriteDiagnostic(
            $"Unknown preset '{name}'. Available presets: {available}.");

        return (int)CliExitCode.ConfigurationError;

    }

    private int Failure(Error error)
    {

        console.WriteDiagnostic(error.Message);

        if (error.Details is { Count: > 0 })
        {

            foreach (ConfigurationValidationError detail in error.Details)
            {

                console.WriteDiagnostic($"{detail.Pointer}: {detail.Detail}");

            }

        }

        return error.Code.StartsWith("Connection.", StringComparison.Ordinal)
            ? (int)CliExitCode.NetworkError
            : (int)CliExitCode.ConfigurationError;

    }

    private static ConfigurationPresetListItemPayload BuildListItem(
        ConfigurationPresetDefinition definition,
        ConfigurationPresetInspection? inspection) =>
        new(
            definition.Id,
            definition.Version,
            definition.DisplayName,
            definition.Purpose,
            EffectiveState(definition, inspection),
            IsActive(definition, inspection));

    private static string EffectiveState(
        ConfigurationPresetDefinition definition,
        ConfigurationPresetInspection? inspection) =>
        IsActive(definition, inspection)
            ? inspection!.State.ToString()
            : inspection is null
                ? "Unavailable"
                : "Available";

    private static bool IsActive(
        ConfigurationPresetDefinition definition,
        ConfigurationPresetInspection? inspection) =>
        string.Equals(
            definition.Id,
            inspection?.ActivePreset?.Id,
            StringComparison.OrdinalIgnoreCase);

    private static int ApplyExitCode(ConfigurationPresetApplyResult result) =>
        result.RollbackStatus == ConfigurationPresetRollbackStatus.Failed
        || (!result.Applied && !result.AlreadyApplied)
            ? (int)CliExitCode.ConfigurationError
            : (int)CliExitCode.Success;

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static ConfigurationPresetApplyResult RedactApplyResult(
        ConfigurationPresetApplyResult result) =>
        result with
        {

            Plan = RedactPlan(result.Plan),

            Inspection = RedactInspection(result.Inspection),

        };

    private static ConfigurationPresetResetResult RedactResetResult(
        ConfigurationPresetResetResult result) =>
        result with { Inspection = RedactInspection(result.Inspection) };

    private static ConfigurationPresetInspection RedactInspection(
        ConfigurationPresetInspection inspection) =>
        inspection with
        {

            ActivePreset = inspection.ActivePreset is null
                ? null
                : RedactDefinition(inspection.ActivePreset),

            Drift = RedactRows(inspection.Drift),

        };

    private static ConfigurationPresetPlan RedactPlan(ConfigurationPresetPlan plan) =>
        plan with
        {

            Preset = RedactDefinition(plan.Preset),

            Diff = RedactRows(plan.Diff),

        };

    private static ConfigurationPresetDefinition RedactDefinition(
        ConfigurationPresetDefinition definition) =>
        definition with
        {

            OwnedSettings = definition.OwnedSettings
                .Select(static setting => ConfigurationPathAccessor.IsSensitive(setting.Path)
                    ? setting with { CanonicalJson = "***" }
                    : setting)
                .ToImmutableArray(),

        };

    private static ImmutableArray<ConfigurationPresetDiffRow> RedactRows(
        ImmutableArray<ConfigurationPresetDiffRow> rows) =>
        rows
            .Select(static row => ConfigurationPathAccessor.IsSensitive(row.Path)
                ? row with
                {

                    PersistedValue = "***",

                    EffectiveValue = "***",

                    ProposedPersistedValue = "***",

                }
                : row)
            .ToImmutableArray();

}
