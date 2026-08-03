using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Configuration.Presets;

/// <summary>
/// Builds deterministic preset candidates and transparent diffs without performing I/O.
/// </summary>
public sealed class ConfigurationPresetPlanner(
    ConfigurationValidator? validator = null)
{

    private static readonly ConfigurationPresetPrerequisite ValidConfigurationPrerequisite =
        new(
            "valid-configuration",
            "The complete candidate configuration must pass semantic validation.",
            "arcanum config validate",
            Required: true);

    private static readonly ConfigurationPresetPrerequisite EnvironmentOverridePrerequisite =
        new(
            "environment-override",
            "Environment overrides must not contradict the preset's safety and privacy boundaries.",
            "Remove or change the listed environment override, then run preset diff again.",
            Required: true);

    private readonly ConfigurationValidator _validator = validator ?? new ConfigurationValidator();

    public Result<ConfigurationPresetPlanningResult> Plan(
        ConfigurationPresetDefinition preset,
        ConfigurationPresetSnapshot snapshot,
        ConfigurationPresetPlanningContext? context = null)
    {

        ArgumentNullException.ThrowIfNull(preset);

        ArgumentNullException.ThrowIfNull(snapshot);

        context ??= new ConfigurationPresetPlanningContext();

        ArcanumSettings candidate = ConfigurationPathAccessor.Clone(snapshot.PersistedSettings);

        ImmutableArray<ConfigurationPresetBaselineValue>.Builder baselineValues =
            ImmutableArray.CreateBuilder<ConfigurationPresetBaselineValue>();

        foreach (ConfigurationPresetOwnedSetting setting in preset.OwnedSettings)
        {

            string persistedValue;

            try
            {

                persistedValue = ConfigurationPathAccessor.GetCanonicalValue(
                    snapshot.PersistedSettings,
                    setting.Path);

            }
            catch (ArgumentException exception)
            {

                return InvalidDefinition(preset, setting, exception.Message);

            }

            baselineValues.Add(new ConfigurationPresetBaselineValue(
                setting.Path,
                persistedValue));

            ConfigurationPathUpdate update = ConfigurationPathAccessor.SetCanonicalValue(
                candidate,
                setting.Path,
                setting.CanonicalJson);

            if (!update.IsSuccess)
            {

                return InvalidDefinition(preset, setting, update.Error!);

            }

            candidate = update.Settings!;

        }

        ImmutableArray<ConfigurationPresetBaselineValue> appliedValues =
            preset.OwnedSettings
                .Select(setting => new ConfigurationPresetBaselineValue(
                    setting.Path,
                    ConfigurationPathAccessor.GetCanonicalValue(candidate, setting.Path)))
                .ToImmutableArray();

        ImmutableArray<ConfigurationPresetPrerequisiteStatus>.Builder prerequisites =
            ImmutableArray.CreateBuilder<ConfigurationPresetPrerequisiteStatus>();

        ArcanumSettings effectiveCandidate = BuildEffectiveCandidate(
            preset,
            snapshot,
            candidate);

        foreach (ConfigurationPresetPrerequisite prerequisite in preset.Prerequisites)
        {

            prerequisites.Add(EvaluatePrerequisite(
                prerequisite,
                effectiveCandidate,
                context));

        }

        ConfigurationPresetPrerequisiteStatus? environmentStatus =
            EvaluateEnvironmentOverrides(preset, snapshot, candidate);

        if (environmentStatus is not null)
        {

            prerequisites.Add(environmentStatus);

        }

        Result semanticValidation = _validator.Validate(candidate);

        Result effectiveValidation = _validator.Validate(effectiveCandidate);

        if (semanticValidation.IsFailure || effectiveValidation.IsFailure)
        {

            string detail = semanticValidation.IsFailure
                ? BuildValidationDetail(semanticValidation)
                : "The effective environment-overridden candidate is invalid: "
                    + BuildValidationDetail(effectiveValidation);

            prerequisites.Add(new ConfigurationPresetPrerequisiteStatus(
                ValidConfigurationPrerequisite,
                IsSatisfied: false,
                Detail: detail));

        }

        ImmutableArray<ConfigurationPresetDiffRow> diff = BuildPlanDiff(
            preset,
            snapshot,
            candidate);

        string ownedValuesHash = ConfigurationPresetHash.ComputeOwnedValues(
            candidate,
            preset.OwnedSettings);

        ConfigurationPresetInspection current = Inspect(snapshot, context);

        bool targetIsActive = snapshot.Provenance is { } active
            && string.Equals(active.PresetId, preset.Id, StringComparison.OrdinalIgnoreCase)
            && active.Version == preset.Version;

        ConfigurationPresetEffectiveState targetState = targetIsActive
            ? current.State
            : ConfigurationPresetEffectiveState.Custom;

        bool applicable = prerequisites.All(static status =>
            !status.Prerequisite.Required || status.IsSatisfied);

        bool idempotent = snapshot.Provenance is { } provenance
            && string.Equals(provenance.PresetId, preset.Id, StringComparison.OrdinalIgnoreCase)
            && provenance.Version == preset.Version
            && string.Equals(
                provenance.OwnedValuesHash,
                ownedValuesHash,
                StringComparison.Ordinal)
            && diff.All(static row => !row.PersistedValueChanges);

        ConfigurationPresetCompletionSummary completion = BuildCompletionSummary(
            preset,
            effectiveCandidate,
            context);

        ConfigurationPresetPlan plan = new(
            preset,
            targetState,
            diff,
            prerequisites.ToImmutable(),
            ownedValuesHash,
            applicable,
            idempotent,
            completion);

        return Result<ConfigurationPresetPlanningResult>.Success(
            new ConfigurationPresetPlanningResult(
                plan,
                candidate,
                baselineValues.ToImmutable(),
                appliedValues));

    }

    private static ArcanumSettings BuildEffectiveCandidate(
        ConfigurationPresetDefinition preset,
        ConfigurationPresetSnapshot snapshot,
        ArcanumSettings persistedCandidate)
    {

        ArcanumSettings effective = ConfigurationPathAccessor.Clone(
            snapshot.Environment.EffectiveSettings);

        foreach (ConfigurationPresetOwnedSetting setting in preset.OwnedSettings)
        {

            if (snapshot.Environment.FindEffective(setting.Path) is not null)
            {

                continue;

            }

            ConfigurationPathUpdate update = ConfigurationPathAccessor.SetCanonicalValue(
                effective,
                setting.Path,
                ConfigurationPathAccessor.GetCanonicalValue(
                    persistedCandidate,
                    setting.Path));

            if (update.IsSuccess)
            {

                effective = update.Settings!;

            }

        }

        return effective;

    }

    private static ConfigurationPresetPrerequisiteStatus? EvaluateEnvironmentOverrides(
        ConfigurationPresetDefinition preset,
        ConfigurationPresetSnapshot snapshot,
        ArcanumSettings persistedCandidate)
    {

        string[] masked = preset.OwnedSettings
            .Where(static setting => setting.IsSafetyBoundary)
            .Select(setting => new
            {

                Setting = setting,

                Override = snapshot.Environment.FindEffective(setting.Path),

            })
            .Where(candidate =>
                candidate.Override is not null
                && !CanonicalEquals(
                    ConfigurationPathAccessor.GetCanonicalValue(
                        snapshot.Environment.EffectiveSettings,
                        candidate.Setting.Path),
                    ConfigurationPathAccessor.GetCanonicalValue(
                        persistedCandidate,
                        candidate.Setting.Path)))
            .Select(candidate =>
                $"{candidate.Override!.VariableName} masks {candidate.Setting.Path}")
            .ToArray();

        return masked.Length == 0
            ? null
            : new ConfigurationPresetPrerequisiteStatus(
                EnvironmentOverridePrerequisite,
                IsSatisfied: false,
                Detail: string.Join("; ", masked));

    }

    public ConfigurationPresetInspection Inspect(
        ConfigurationPresetSnapshot snapshot,
        ConfigurationPresetPlanningContext? context = null)
    {

        ArgumentNullException.ThrowIfNull(snapshot);

        context ??= new ConfigurationPresetPlanningContext();

        ConfigurationPresetProvenance? provenance = snapshot.Provenance;

        if (provenance is null)
        {

            return new ConfigurationPresetInspection(
                ConfigurationPresetEffectiveState.Custom,
                ActivePreset: null,
                AppliedVersion: null,
                AppliedAt: null,
                OwnedValuesHash: null,
                Drift: [],
                BuildCompletionSummary(
                    preset: null,
                    snapshot.Environment.EffectiveSettings,
                    context));

        }

        ConfigurationPresetDefinition? preset = ConfigurationPresetCatalog.FindVersion(
            provenance.PresetId,
            provenance.Version);

        ImmutableArray<ConfigurationPresetDiffRow> drift = BuildInspectionDrift(
            preset,
            snapshot,
            provenance);

        ConfigurationPresetEffectiveState state = drift.IsEmpty
            ? ConfigurationPresetEffectiveState.Active
            : ConfigurationPresetEffectiveState.Drifted;

        return new ConfigurationPresetInspection(
            state,
            preset,
            provenance.Version,
            provenance.AppliedAt,
            provenance.OwnedValuesHash,
            drift,
            BuildCompletionSummary(
                preset,
                snapshot.Environment.EffectiveSettings,
                context));

    }

    private static ImmutableArray<ConfigurationPresetDiffRow> BuildPlanDiff(
        ConfigurationPresetDefinition preset,
        ConfigurationPresetSnapshot snapshot,
        ArcanumSettings candidate)
    {

        ImmutableArray<ConfigurationPresetDiffRow>.Builder diff =
            ImmutableArray.CreateBuilder<ConfigurationPresetDiffRow>();

        foreach (ConfigurationPresetOwnedSetting setting in preset.OwnedSettings)
        {

            string persisted = ConfigurationPathAccessor.GetCanonicalValue(
                snapshot.PersistedSettings,
                setting.Path);

            string effective = ConfigurationPathAccessor.GetCanonicalValue(
                snapshot.Environment.EffectiveSettings,
                setting.Path);

            string proposed = ConfigurationPathAccessor.GetCanonicalValue(
                candidate,
                setting.Path);

            ConfigurationEnvironmentOverride? presentEnvironment = snapshot.Environment.Find(
                setting.Path);

            ConfigurationEnvironmentOverride? effectiveEnvironment =
                snapshot.Environment.FindEffective(
                setting.Path);

            diff.Add(new ConfigurationPresetDiffRow(
                setting.Path,
                persisted,
                effective,
                proposed,
                effectiveEnvironment is null
                    ? ConfigurationPresetValueSource.Persisted
                    : ConfigurationPresetValueSource.EnvironmentOverride,
                effectiveEnvironment?.VariableName ?? presentEnvironment?.VariableName,
                effectiveEnvironment is not null,
                OwnedByPreset: true,
                Normalize(setting.PrerequisiteIds),
                setting.RequiresRestart,
                PersistedValueChanges: !CanonicalEquals(persisted, proposed),
                EffectiveValueChanges: effectiveEnvironment is null
                    && !CanonicalEquals(effective, proposed)));

        }

        return diff.ToImmutable();

    }

    private static ImmutableArray<ConfigurationPresetDiffRow> BuildInspectionDrift(
        ConfigurationPresetDefinition? preset,
        ConfigurationPresetSnapshot snapshot,
        ConfigurationPresetProvenance provenance)
    {

        ImmutableArray<ConfigurationPresetDiffRow>.Builder drift =
            ImmutableArray.CreateBuilder<ConfigurationPresetDiffRow>();

        foreach (ConfigurationPresetBaselineValue applied in provenance.AppliedValues)
        {

            string persisted;

            string effective;

            try
            {

                persisted = ConfigurationPathAccessor.GetCanonicalValue(
                    snapshot.PersistedSettings,
                    applied.Path);

                effective = ConfigurationPathAccessor.GetCanonicalValue(
                    snapshot.Environment.EffectiveSettings,
                    applied.Path);

            }
            catch (ArgumentException)
            {

                persisted = "<removed>";

                effective = "<removed>";

            }

            bool persistedChanged = !CanonicalEquals(persisted, applied.CanonicalJson);

            bool effectiveChanged = !CanonicalEquals(effective, applied.CanonicalJson);

            if (!persistedChanged && !effectiveChanged)
            {

                continue;

            }

            ConfigurationPresetOwnedSetting? owned = preset?.OwnedSettings.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Path,
                    applied.Path,
                    StringComparison.OrdinalIgnoreCase));

            ConfigurationEnvironmentOverride? presentEnvironment = snapshot.Environment.Find(
                applied.Path);

            ConfigurationEnvironmentOverride? effectiveEnvironment =
                snapshot.Environment.FindEffective(
                applied.Path);

            drift.Add(new ConfigurationPresetDiffRow(
                applied.Path,
                persisted,
                effective,
                applied.CanonicalJson,
                effectiveEnvironment is null
                    ? ConfigurationPresetValueSource.Persisted
                    : ConfigurationPresetValueSource.EnvironmentOverride,
                effectiveEnvironment?.VariableName ?? presentEnvironment?.VariableName,
                effectiveEnvironment is not null,
                OwnedByPreset: true,
                owned is null ? [] : Normalize(owned.PrerequisiteIds),
                owned?.RequiresRestart ?? true,
                persistedChanged,
                effectiveChanged));

        }

        return drift.ToImmutable();

    }

    private static ConfigurationPresetPrerequisiteStatus EvaluatePrerequisite(
        ConfigurationPresetPrerequisite prerequisite,
        ArcanumSettings settings,
        ConfigurationPresetPlanningContext context)
    {

        (bool satisfied, string detail) = prerequisite.Id switch
        {
            ConfigurationPresetCatalog.ProviderModelPrerequisite =>
                EvaluateProviderModel(settings),
            ConfigurationPresetCatalog.WorkspacePrerequisite =>
                EvaluateWorkspace(settings, context),
            ConfigurationPresetCatalog.ResearchCredentialPrerequisite =>
                context.ResearchCredentialAvailable
                    ? (true, "The research credential is available.")
                    : (false, "The research credential is not available."),
            ConfigurationPresetCatalog.LoopbackProviderPrerequisite =>
                EvaluateLoopbackProvider(settings),
            ConfigurationPresetCatalog.PositiveBudgetPrerequisite =>
                settings.Cost.Budget.Enabled && settings.Cost.Budget.DailyLimitUsd > 0m
                    ? (true, $"Daily budget enforcement is enabled at {settings.Cost.Budget.DailyLimitUsd:0.00} USD.")
                    : (false, "Enable budget enforcement with an explicit daily limit greater than zero."),
            _ => (false, $"Unknown prerequisite '{prerequisite.Id}'."),
        };

        return new ConfigurationPresetPrerequisiteStatus(
            prerequisite,
            satisfied,
            detail);

    }

    private static (bool Satisfied, string Detail) EvaluateProviderModel(
        ArcanumSettings settings)
    {

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            targetModel: null,
            out ProviderSettings? provider,
            out string model);

        return resolved
            ? (true, $"Selected {provider!.Name} / {model}.")
            : (false, "No configured provider advertises the selected model.");

    }

    private static (bool Satisfied, string Detail) EvaluateWorkspace(
        ArcanumSettings settings,
        ConfigurationPresetPlanningContext context)
    {

        string? workspace = string.IsNullOrWhiteSpace(context.Workspace)
            ? settings.Workspaces.DefaultRoot
            : context.Workspace;

        return string.IsNullOrWhiteSpace(workspace)
            ? (false, "No workspace is selected or configured.")
            : (true, $"Workspace: {workspace}");

    }

    private static (bool Satisfied, string Detail) EvaluateLoopbackProvider(
        ArcanumSettings settings)
    {

        bool resolved = ProviderResolver.TryResolveProviderForModel(
            settings,
            targetModel: null,
            out ProviderSettings? provider,
            out string model);

        if (!resolved)
        {

            return (false, "No configured provider advertises the selected model.");

        }

        bool loopback = Uri.TryCreate(
                provider!.Endpoint?.Trim(),
                UriKind.Absolute,
                out Uri? endpoint)
            && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps)
            && endpoint.IsLoopback;

        return loopback
            ? (true, $"Selected loopback provider {provider.Name} / {model}.")
            : (false, $"Selected provider '{provider.Name}' is not a loopback endpoint.");

    }

    private static ConfigurationPresetCompletionSummary BuildCompletionSummary(
        ConfigurationPresetDefinition? preset,
        ArcanumSettings settings,
        ConfigurationPresetPlanningContext context)
    {

        string providerAndModel = ProviderResolver.TryResolveProviderForModel(
            settings,
            targetModel: null,
            out ProviderSettings? provider,
            out string model)
            ? $"{provider!.Name} / {model}"
            : "Not configured";

        string workspace = string.IsNullOrWhiteSpace(context.Workspace)
            ? settings.Workspaces.DefaultRoot ?? "Not selected"
            : context.Workspace;

        string campaign = string.IsNullOrWhiteSpace(context.Campaign)
            ? "Not selected"
            : context.Campaign;

        List<string> memorySources = [];

        AddWhen(memorySources, settings.Features.Lexicon, "Lexicon");

        AddWhen(memorySources, settings.Features.ArchiveSearch, "Archive search");

        AddWhen(memorySources, settings.Features.Saga, "Saga");

        AddWhen(memorySources, settings.Features.SessionSearch, "Session search");

        AddWhen(memorySources, settings.Features.CodebaseRetrieval, "Weave codebase retrieval");

        AddWhen(memorySources, settings.Features.AttachmentRetrieval, "Attachment retrieval");

        string toolPolicy = settings.Security.Ward.Enabled
            ? settings.Security.Ward.UnattendedMode
                ? "Ward enabled; unattended requests auto-deny actions that need approval; Sanctum path boundaries remain active."
                : "Ward enabled; actions may request approval; Sanctum path boundaries remain active."
            : "Ward disabled; Sanctum path boundaries remain active.";

        string privacy = string.Join(
            "; ",
            settings.Host.ListenAny ? "network host binding enabled" : "loopback host binding",
            settings.Features.WebBrowsing ? "external web research enabled" : "external web research disabled",
            settings.Features.EnterpriseTelemetry ? "enterprise telemetry enabled" : "enterprise telemetry disabled",
            settings.Security.AllowUnsandboxedToolChildren
                ? "unsandboxed tool children allowed"
                : "unsandboxed tool children denied");

        string nextCommand = preset?.Recommendations.FirstOrDefault()?.Command
            ?? "arcanum config show";

        return new ConfigurationPresetCompletionSummary(
            preset?.DisplayName ?? "Custom",
            providerAndModel,
            $"Workspace: {workspace}; Campaign: {campaign}",
            [.. memorySources],
            toolPolicy,
            privacy,
            nextCommand);

    }

    private static string BuildValidationDetail(Result validation)
    {

        if (validation.Error.Details is not { Count: > 0 } details)
        {

            return validation.Error.Message;

        }

        return string.Join(
            "; ",
            details.Take(5).Select(static detail =>
                $"{detail.Pointer}: {detail.Detail}"));

    }

    private static void AddWhen(List<string> values, bool condition, string value)
    {

        if (condition)
        {

            values.Add(value);

        }

    }

    private static ImmutableArray<string> Normalize(ImmutableArray<string> values) =>
        values.IsDefault ? [] : values;

    private static bool CanonicalEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static Result<ConfigurationPresetPlanningResult> InvalidDefinition(
        ConfigurationPresetDefinition preset,
        ConfigurationPresetOwnedSetting setting,
        string detail) =>
        Result<ConfigurationPresetPlanningResult>.Failure(
            new Error(
                "Configuration.Preset.InvalidDefinition",
                $"Preset '{preset.Id}' v{preset.Version} has invalid owned setting '{setting.Path}': {detail}"));

}
