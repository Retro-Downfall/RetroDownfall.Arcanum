using System.Collections.Immutable;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Configuration.Presets;

public interface IConfigurationPresetService
{

    IReadOnlyList<ConfigurationPresetDefinition> List();

    IReadOnlyList<ConfigurationPresetGlossaryEntry> Glossary();

    ConfigurationPresetDefinition? Find(string idOrName);

    Task<Result<ConfigurationPresetPlan>> DiffAsync(
        string idOrName,
        CancellationToken cancellationToken = default);

    Task<Result<ConfigurationPresetApplyResult>> ApplyAsync(
        string idOrName,
        CancellationToken cancellationToken = default);

    Task<Result<ConfigurationPresetResetResult>> ResetAsync(
        CancellationToken cancellationToken = default);

    Task<Result<ConfigurationPresetInspection>> InspectAsync(
        CancellationToken cancellationToken = default);

}

public interface IConfigurationPresetPersistence
{

    Task<Result<ConfigurationPresetSnapshot>> ReadAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the exact persisted preset snapshot without recovering or otherwise mutating a
    /// prepared transaction. Implementations fail closed when recovery is required.
    /// </summary>
    Task<Result<ConfigurationPresetSnapshot>> PeekAsync(
        CancellationToken cancellationToken = default);

    Task<Result<ConfigurationPresetCommitResult>> ApplyAsync(
        ConfigurationPresetCommitRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<ConfigurationPresetResetCommitResult>> ResetAsync(
        ConfigurationPresetResetCommitRequest request,
        CancellationToken cancellationToken = default);

}

public interface IConfigurationPresetCandidateValidator
{

    Task<Result> ValidateAsync(
        ArcanumSettings candidate,
        CancellationToken cancellationToken = default);

}

public sealed record ConfigurationPresetDefinition(
    string Id,
    int Version,
    string DisplayName,
    string Purpose,
    ImmutableArray<ConfigurationPresetOwnedSetting> OwnedSettings,
    ConfigurationPresetDisclosure Disclosure,
    ImmutableArray<ConfigurationPresetPrerequisite> Prerequisites,
    ImmutableArray<ConfigurationPresetRecommendation> Recommendations,
    ConfigurationPresetProgressiveDisclosure ProgressiveDisclosure);

public sealed record ConfigurationPresetOwnedSetting(
    string Path,
    string CanonicalJson,
    bool RequiresRestart = true,
    ImmutableArray<string> PrerequisiteIds = default,
    bool IsSafetyBoundary = false)
{

    public ImmutableArray<string> PrerequisiteIds { get; init; } =
        PrerequisiteIds.IsDefault ? [] : PrerequisiteIds;

}

public sealed record ConfigurationPresetDisclosure(
    string Enables,
    string Disables,
    string SecurityImplications,
    string ProviderRequirements,
    string ResourceAndCostBehavior);

public sealed record ConfigurationPresetPrerequisite(
    string Id,
    string Description,
    string ResolutionCommand,
    bool Required);

public sealed record ConfigurationPresetPrerequisiteStatus(
    ConfigurationPresetPrerequisite Prerequisite,
    bool IsSatisfied,
    string Detail);

public sealed record ConfigurationPresetRecommendation(
    string Description,
    string Command,
    bool IsAdvancedFeature = false);

public sealed record ConfigurationPresetProgressiveDisclosure(
    string EssentialChoice,
    ImmutableArray<string> DeferredFeatures,
    string FirstSuccessRecommendation);

public sealed record ConfigurationPresetGlossaryEntry(
    string Term,
    string PlainLanguageMeaning);

[JsonConverter(typeof(JsonStringEnumConverter<ConfigurationPresetEffectiveState>))]

public enum ConfigurationPresetEffectiveState
{

    Custom,

    Active,

    Drifted,

}

[JsonConverter(typeof(JsonStringEnumConverter<ConfigurationPresetValueSource>))]

public enum ConfigurationPresetValueSource
{

    Persisted,

    EnvironmentOverride,

}

[JsonConverter(typeof(JsonStringEnumConverter<ConfigurationPresetRollbackStatus>))]

public enum ConfigurationPresetRollbackStatus
{

    NotRequired,

    SnapshotCreated,

    Restored,

    Failed,

}

public sealed record ConfigurationPresetDiffRow(
    string Path,
    string PersistedValue,
    string EffectiveValue,
    string ProposedPersistedValue,
    ConfigurationPresetValueSource CurrentSource,
    string? EnvironmentVariable,
    bool EnvironmentOverrideIsEffective,
    bool OwnedByPreset,
    ImmutableArray<string> PrerequisiteIds,
    bool RequiresRestart,
    bool PersistedValueChanges,
    bool EffectiveValueChanges);

public sealed record ConfigurationPresetPlan(
    ConfigurationPresetDefinition Preset,
    ConfigurationPresetEffectiveState State,
    ImmutableArray<ConfigurationPresetDiffRow> Diff,
    ImmutableArray<ConfigurationPresetPrerequisiteStatus> Prerequisites,
    string OwnedValuesHash,
    bool IsApplicable,
    bool IsIdempotent,
    ConfigurationPresetCompletionSummary CompletionSummary);

public sealed record ConfigurationPresetPlanningResult(
    ConfigurationPresetPlan Plan,
    ArcanumSettings CandidateSettings,
    ImmutableArray<ConfigurationPresetBaselineValue> BaselineValues,
    ImmutableArray<ConfigurationPresetBaselineValue> AppliedValues);

public sealed record ConfigurationPresetCompletionSummary(
    string ActivePreset,
    string ProviderAndModel,
    string WorkspaceAndCampaign,
    ImmutableArray<string> EnabledMemorySources,
    string ToolPolicy,
    string PrivacyState,
    string NextRecommendedCommand);

public sealed record ConfigurationPresetInspection(
    ConfigurationPresetEffectiveState State,
    ConfigurationPresetDefinition? ActivePreset,
    int? AppliedVersion,
    DateTimeOffset? AppliedAt,
    string? OwnedValuesHash,
    ImmutableArray<ConfigurationPresetDiffRow> Drift,
    ConfigurationPresetCompletionSummary CompletionSummary);

public sealed record ConfigurationPresetApplyResult(
    ConfigurationPresetPlan Plan,
    ConfigurationPresetInspection Inspection,
    bool Applied,
    bool AlreadyApplied,
    ConfigurationPresetRollbackStatus RollbackStatus);

public sealed record ConfigurationPresetResetResult(
    ConfigurationPresetInspection Inspection,
    bool Reset,
    int RestoredSettingCount,
    int PreservedDriftCount,
    ConfigurationPresetRollbackStatus RollbackStatus);

public sealed record ConfigurationPresetProvenance(
    string PresetId,
    int Version,
    DateTimeOffset AppliedAt,
    string OwnedValuesHash,
    ImmutableArray<ConfigurationPresetBaselineValue> BaselineValues,
    ImmutableArray<ConfigurationPresetBaselineValue> AppliedValues);

public sealed record ConfigurationPresetBaselineValue(
    string Path,
    string CanonicalJson);

public sealed record ConfigurationPresetSnapshot(
    ArcanumSettings PersistedSettings,
    ConfigurationEnvironmentSnapshot Environment,
    ConfigurationPresetProvenance? Provenance);

public sealed record ConfigurationPresetCommitRequest(
    ArcanumSettings CandidateSettings,
    ConfigurationPresetProvenance Provenance,
    string ExpectedCurrentHash);

public sealed record ConfigurationPresetCommitResult(
    ConfigurationPresetRollbackStatus RollbackStatus,
    ConfigurationPresetSnapshot Snapshot);

public sealed record ConfigurationPresetResetCommitRequest(
    ConfigurationPresetProvenance Provenance,
    string ExpectedCurrentHash);

public sealed record ConfigurationPresetResetCommitResult(
    ConfigurationPresetRollbackStatus RollbackStatus,
    ConfigurationPresetSnapshot Snapshot,
    int RestoredSettingCount,
    int PreservedDriftCount);

public sealed record ConfigurationPresetPlanningContext(
    bool ResearchCredentialAvailable = false,
    string? Workspace = null,
    string? Campaign = null);
