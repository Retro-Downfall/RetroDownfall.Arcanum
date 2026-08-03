using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Configuration;

internal sealed class ConfigurationPresetService(
    IConfigurationPresetPersistence persistence,
    IConfigurationPresetCandidateValidator candidateValidator,
    TimeProvider timeProvider,
    IWebResearchCredentialStore? credentialStore = null) : IConfigurationPresetService
{

    private readonly ConfigurationPresetPlanner _planner = new();

    public IReadOnlyList<ConfigurationPresetDefinition> List() =>
        ConfigurationPresetCatalog.All;

    public IReadOnlyList<ConfigurationPresetGlossaryEntry> Glossary() =>
        ConfigurationPresetCatalog.Glossary;

    public ConfigurationPresetDefinition? Find(string idOrName) =>
        ConfigurationPresetCatalog.Find(idOrName);

    public async Task<Result<ConfigurationPresetPlan>> DiffAsync(
        string idOrName,
        CancellationToken cancellationToken = default)
    {

        ConfigurationPresetDefinition? preset = Find(idOrName);

        if (preset is null)
        {

            return Result<ConfigurationPresetPlan>.Failure(NotFound(idOrName));

        }

        Result<ConfigurationPresetSnapshot> snapshot = await persistence
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (snapshot.IsFailure)
        {

            return Result<ConfigurationPresetPlan>.Failure(snapshot.Error);

        }

        ConfigurationPresetPlanningContext context = await BuildContextAsync(
                snapshot.Value,
                RequiresResearchCredential(preset),
                cancellationToken)
            .ConfigureAwait(false);

        Result<ConfigurationPresetPlanningResult> planning = _planner.Plan(
            preset,
            snapshot.Value,
            context);

        return planning.IsSuccess
            ? Result<ConfigurationPresetPlan>.Success(planning.Value.Plan)
            : Result<ConfigurationPresetPlan>.Failure(planning.Error);

    }

    public async Task<Result<ConfigurationPresetApplyResult>> ApplyAsync(
        string idOrName,
        CancellationToken cancellationToken = default)
    {

        ConfigurationPresetDefinition? preset = Find(idOrName);

        if (preset is null)
        {

            return Result<ConfigurationPresetApplyResult>.Failure(NotFound(idOrName));

        }

        Result<ConfigurationPresetSnapshot> snapshot = await persistence
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (snapshot.IsFailure)
        {

            return Result<ConfigurationPresetApplyResult>.Failure(snapshot.Error);

        }

        ConfigurationPresetPlanningContext context = await BuildContextAsync(
                snapshot.Value,
                RequiresResearchCredential(preset),
                cancellationToken)
            .ConfigureAwait(false);

        Result<ConfigurationPresetPlanningResult> planning = _planner.Plan(
            preset,
            snapshot.Value,
            context);

        if (planning.IsFailure)
        {

            return Result<ConfigurationPresetApplyResult>.Failure(planning.Error);

        }

        ConfigurationPresetPlanningResult proposal = planning.Value;

        if (!proposal.Plan.IsApplicable)
        {

            return Result<ConfigurationPresetApplyResult>.Failure(
                MissingPrerequisites(proposal.Plan));

        }

        if (proposal.Plan.IsIdempotent)
        {

            return Result<ConfigurationPresetApplyResult>.Success(
                new ConfigurationPresetApplyResult(
                    proposal.Plan,
                    _planner.Inspect(snapshot.Value, context),
                    Applied: false,
                    AlreadyApplied: true,
                    ConfigurationPresetRollbackStatus.NotRequired));

        }

        Result validation = await candidateValidator
            .ValidateAsync(proposal.CandidateSettings, cancellationToken)
            .ConfigureAwait(false);

        if (validation.IsFailure)
        {

            return Result<ConfigurationPresetApplyResult>.Failure(validation.Error);

        }

        ConfigurationPresetProvenance provenance = new(
            preset.Id,
            preset.Version,
            timeProvider.GetUtcNow(),
            proposal.Plan.OwnedValuesHash,
            proposal.BaselineValues,
            proposal.AppliedValues);

        ConfigurationPresetCommitRequest request = new(
            proposal.CandidateSettings,
            provenance,
            ConfigurationPresetHash.ComputeSettings(snapshot.Value.PersistedSettings));

        Result<ConfigurationPresetCommitResult> commit = await persistence
            .ApplyAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (commit.IsFailure)
        {

            return Result<ConfigurationPresetApplyResult>.Failure(commit.Error);

        }

        ConfigurationPresetInspection inspection = _planner.Inspect(
            commit.Value.Snapshot,
            context);

        return Result<ConfigurationPresetApplyResult>.Success(
            new ConfigurationPresetApplyResult(
                proposal.Plan,
                inspection,
                Applied: true,
                AlreadyApplied: false,
                commit.Value.RollbackStatus));

    }

    public async Task<Result<ConfigurationPresetResetResult>> ResetAsync(
        CancellationToken cancellationToken = default)
    {

        Result<ConfigurationPresetSnapshot> snapshot = await persistence
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (snapshot.IsFailure)
        {

            return Result<ConfigurationPresetResetResult>.Failure(snapshot.Error);

        }

        ConfigurationPresetPlanningContext context = await BuildContextAsync(
                snapshot.Value,
                researchCredentialRequired: false,
                cancellationToken)
            .ConfigureAwait(false);

        if (snapshot.Value.Provenance is not { } provenance)
        {

            return Result<ConfigurationPresetResetResult>.Success(
                new ConfigurationPresetResetResult(
                    _planner.Inspect(snapshot.Value, context),
                    Reset: false,
                    RestoredSettingCount: 0,
                    PreservedDriftCount: 0,
                    ConfigurationPresetRollbackStatus.NotRequired));

        }

        ConfigurationPresetResetCommitRequest request = new(
            provenance,
            ConfigurationPresetHash.ComputeSettings(snapshot.Value.PersistedSettings));

        Result<ConfigurationPresetResetCommitResult> commit = await persistence
            .ResetAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (commit.IsFailure)
        {

            return Result<ConfigurationPresetResetResult>.Failure(commit.Error);

        }

        ConfigurationPresetInspection inspection = _planner.Inspect(
            commit.Value.Snapshot,
            context);

        return Result<ConfigurationPresetResetResult>.Success(
            new ConfigurationPresetResetResult(
                inspection,
                Reset: true,
                commit.Value.RestoredSettingCount,
                commit.Value.PreservedDriftCount,
                commit.Value.RollbackStatus));

    }

    public async Task<Result<ConfigurationPresetInspection>> InspectAsync(
        CancellationToken cancellationToken = default)
    {

        Result<ConfigurationPresetSnapshot> snapshot = await persistence
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (snapshot.IsFailure)
        {

            return Result<ConfigurationPresetInspection>.Failure(snapshot.Error);

        }

        ConfigurationPresetPlanningContext context = await BuildContextAsync(
                snapshot.Value,
                researchCredentialRequired: false,
                cancellationToken)
            .ConfigureAwait(false);

        return Result<ConfigurationPresetInspection>.Success(
            _planner.Inspect(snapshot.Value, context));

    }

    private async Task<ConfigurationPresetPlanningContext> BuildContextAsync(
        ConfigurationPresetSnapshot snapshot,
        bool researchCredentialRequired,
        CancellationToken cancellationToken)
    {

        if (!researchCredentialRequired)
        {

            return new ConfigurationPresetPlanningContext();

        }

        WebBrowsingSettings webResearch = snapshot.Environment.EffectiveSettings
            .ResolveWebBrowsing();

        bool credentialAvailable = !string.IsNullOrWhiteSpace(
            EnvironmentCredentialResolver.ResolveWebResearchApiKey(webResearch));

        if (!credentialAvailable && credentialStore is not null)
        {

            SecretStoreReadResult secret = await credentialStore
                .GetPerplexityApiKeyReadResultAsync(cancellationToken)
                .ConfigureAwait(false);

            credentialAvailable = secret.Status == SecretStoreReadStatus.Ok
                && !string.IsNullOrWhiteSpace(secret.Value);

        }

        return new ConfigurationPresetPlanningContext(
            ResearchCredentialAvailable: credentialAvailable);

    }

    private static bool RequiresResearchCredential(
        ConfigurationPresetDefinition preset) =>
        preset.Prerequisites.Any(static prerequisite =>
            string.Equals(
                prerequisite.Id,
                ConfigurationPresetCatalog.ResearchCredentialPrerequisite,
                StringComparison.Ordinal));

    private static Error NotFound(string? idOrName)
    {

        string available = string.Join(
            ", ",
            ConfigurationPresetCatalog.All.Select(static preset => preset.Id));

        return new Error(
            "Preset.NotFound",
            $"Unknown preset '{idOrName}'. Available presets: {available}.");

    }

    private static Error MissingPrerequisites(ConfigurationPresetPlan plan)
    {

        string missing = string.Join(
            "; ",
            plan.Prerequisites
                .Where(static status =>
                    status.Prerequisite.Required && !status.IsSatisfied)
                .Select(static status =>
                    $"{status.Prerequisite.Description} "
                    + $"Detail: {status.Detail} "
                    + $"Resolve with: {status.Prerequisite.ResolutionCommand}"));

        return new Error(
            "Preset.PrerequisitesMissing",
            $"Preset '{plan.Preset.Id}' is not applicable. {missing}");

    }

}
