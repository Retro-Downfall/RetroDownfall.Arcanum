using System.Collections.Immutable;

using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Configuration.Presets;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Cli.Services.Setup;

/// <summary>
/// Everything the wizard read before it started proposing changes. Held so a plan can be recomputed
/// after any step without re-reading the world, and so the committer can compare against exactly the
/// state the operator reviewed.
/// </summary>
internal sealed record SetupPlanContext(
    ConfigurationCommandSnapshot Snapshot,
    ConfigurationPresetProvenance? Provenance,
    SetupCredentialPresence ProviderCredential,
    SetupCredentialPresence WebResearchCredential);

/// <summary>
/// Whether a credential is usable right now, and where it comes from. Carries no value.
/// </summary>
internal readonly record struct SetupCredentialPresence(bool Present, bool FromEnvironment)
{

    public static SetupCredentialPresence Absent { get; } = new(false, false);

}

internal sealed record SetupPlan(
    ArcanumSettings Candidate,
    SetupPlanPayload Payload,
    SetupCredentialPresence ProviderCredential,
    SetupCredentialPresence WebResearchCredential,
    bool ProviderCredentialWillReplaceExisting,
    bool WebResearchCredentialWillReplaceExisting)
{

    public bool IsApplicable => Payload.IsApplicable;

}

internal interface ISetupPlanner
{

    Task<Result<SetupPlanContext>> ReadAsync(CancellationToken cancellationToken);

    SetupDraft Seed(SetupPlanContext context);

    /// <summary>
    /// Credential presence for the provider name the operator has drafted, which may differ from the
    /// provider that is configured today.
    /// </summary>
    Task<SetupCredentialPresence> ProviderCredentialPresenceAsync(
        string? providerName,
        string? credentialEnvironmentVariable,
        CancellationToken cancellationToken);

    Task<SetupPlan> PlanAsync(
        SetupPlanContext context,
        SetupDraft draft,
        SetupConnectivityResult connectivity,
        SetupStep step,
        CancellationToken cancellationToken);

}

/// <summary>
/// Turns the in-memory draft into a validated candidate configuration, a precise redacted diff, and
/// the completion summary — without writing anything. The preset authority is reused verbatim: the
/// preset plan is computed against the <em>candidate</em> configuration, so prerequisites and the
/// completion summary reflect what the operator will actually have after the commit.
/// </summary>
internal sealed class SetupPlanner(
    IConfigurationCommandService configurationService,
    IConfigurationPresetPersistence presetPersistence,
    IProviderCredentialStore providerCredentialStore,
    IWebResearchCredentialStore webResearchCredentialStore) : ISetupPlanner
{

    private readonly ConfigurationPresetPlanner _presetPlanner = new();

    public async Task<Result<SetupPlanContext>> ReadAsync(CancellationToken cancellationToken)
    {

        Result<ConfigurationCommandSnapshot> snapshot = await configurationService
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (snapshot.IsFailure)
        {

            return Result<SetupPlanContext>.Failure(snapshot.Error);

        }

        ArcanumSettings settings = snapshot.Value.Settings;

        ConfigurationPresetProvenance? provenance = null;

        Result<ConfigurationPresetSnapshot> presetSnapshot = await presetPersistence
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (presetSnapshot.IsSuccess)
        {

            provenance = presetSnapshot.Value.Provenance;

        }

        ProviderSettings? provider = ResolveCurrentProvider(settings);

        SetupCredentialPresence providerCredential = await ProviderCredentialPresenceAsync(
                provider?.Name,
                provider?.CredentialEnvironmentVariable,
                cancellationToken)
            .ConfigureAwait(false);

        WebBrowsingSettings webResearch = settings.ResolveWebBrowsing();

        bool researchFromEnvironment =
            EnvironmentCredentialResolver.ResolveWebResearchApiKey(webResearch) is not null;

        bool researchStored = false;

        if (!researchFromEnvironment)
        {

            SecretStoreReadResult stored = await webResearchCredentialStore
                .GetPerplexityApiKeyReadResultAsync(cancellationToken)
                .ConfigureAwait(false);

            researchStored = stored.Status == SecretStoreReadStatus.Ok;

        }

        return Result<SetupPlanContext>.Success(
            new SetupPlanContext(
                snapshot.Value,
                provenance,
                providerCredential,
                new SetupCredentialPresence(
                    researchStored || researchFromEnvironment,
                    researchFromEnvironment)));

    }

    public async Task<SetupCredentialPresence> ProviderCredentialPresenceAsync(
        string? providerName,
        string? credentialEnvironmentVariable,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(providerName))
        {

            return SetupCredentialPresence.Absent;

        }

        ProviderSettings probe = new()
        {

            Name = providerName.Trim(),

            CredentialEnvironmentVariable = credentialEnvironmentVariable,

        };

        if (EnvironmentCredentialResolver.ResolveProviderApiKey(probe) is not null)
        {

            return new SetupCredentialPresence(true, true);

        }

        SecretStoreReadResult stored = await providerCredentialStore
            .GetApiKeyReadResultAsync(probe.Name, cancellationToken)
            .ConfigureAwait(false);

        return new SetupCredentialPresence(stored.Status == SecretStoreReadStatus.Ok, false);

    }

    /// <summary>
    /// Seeds the draft from the installation as it exists today, so re-running setup and accepting
    /// every default is a no-op rather than a reset.
    /// </summary>
    public SetupDraft Seed(SetupPlanContext context)
    {

        ArcanumSettings settings = context.Snapshot.Settings;

        ProviderSettings? provider = ResolveCurrentProvider(settings);

        return new SetupDraft
        {

            Edition = settings.Edition,

            ListenAny = settings.Host.ListenAny,

            ProviderName = provider?.Name ?? string.Empty,

            ProviderEndpoint = provider?.Endpoint ?? string.Empty,

            Model = settings.DefaultModel
                ?? provider?.Models.FirstOrDefault()?.Name
                ?? string.Empty,

            ProviderCredential = SetupCredentialAction.Unchanged,

            WebResearchRequested = context.WebResearchCredential.Present
                || settings.ResolveWebBrowsing().Enabled,

            WebResearchCredential = SetupCredentialAction.Unchanged,

            WorkspaceRoot = settings.Workspaces.DefaultRoot,

            PresetId = context.Provenance?.PresetId ?? "general-assistant",

        };

    }

    public async Task<SetupPlan> PlanAsync(
        SetupPlanContext context,
        SetupDraft draft,
        SetupConnectivityResult connectivity,
        SetupStep step,
        CancellationToken cancellationToken)
    {

        SetupCredentialPresence providerCredential = await ProviderCredentialPresenceAsync(
                draft.ProviderName,
                draft.ProviderCredential == SetupCredentialAction.Reference
                    ? draft.ProviderCredentialEnvironmentVariable
                    : null,
                cancellationToken)
            .ConfigureAwait(false);

        ArcanumSettings candidate = BuildCandidate(context.Snapshot.Settings, draft);

        List<string> blockers = [];

        List<string> notes = [];

        if (string.IsNullOrWhiteSpace(draft.ProviderName))
        {

            blockers.Add("A provider name is required.");

        }

        if (string.IsNullOrWhiteSpace(draft.ProviderEndpoint))
        {

            blockers.Add("An OpenAI-compatible provider endpoint is required.");

        }

        if (string.IsNullOrWhiteSpace(draft.Model))
        {

            blockers.Add("A model is required.");

        }

        if (!connectivity.IsUsable && !draft.AllowUnreachableProvider)
        {

            blockers.Add(
                $"Provider validation reported {connectivity.Status}: {connectivity.Detail} "
                + "Fix the provider, or re-run with --allow-unreachable-provider to commit anyway.");

        }
        else if (!connectivity.IsUsable)
        {

            notes.Add(
                $"Provider validation reported {connectivity.Status}; committing anyway was "
                + "explicitly requested.");

        }

        ConfigurationPresetDefinition? preset = ConfigurationPresetCatalog.Find(draft.PresetId);

        if (preset is null)
        {

            blockers.Add(
                $"Unknown preset '{draft.PresetId}'. Available presets: "
                + string.Join(", ", ConfigurationPresetCatalog.All.Select(static p => p.Id))
                + ".");

        }

        bool researchCredentialAvailable = draft.WebResearchCredential switch
        {
            SetupCredentialAction.Store => true,
            SetupCredentialAction.Reference => true,
            SetupCredentialAction.Clear => false,
            _ => context.WebResearchCredential.Present,
        };

        ConfigurationPresetPlanningContext presetContext = new(
            researchCredentialAvailable,
            string.IsNullOrWhiteSpace(draft.WorkspaceRoot) ? null : draft.WorkspaceRoot,
            string.IsNullOrWhiteSpace(draft.CampaignName) ? null : draft.CampaignName);

        ConfigurationPresetPlan? presetPlan = null;

        if (preset is not null)
        {

            Result<ConfigurationPresetPlanningResult> planning = _presetPlanner.Plan(
                preset,
                new ConfigurationPresetSnapshot(
                    candidate,
                    ConfigurationEnvironmentResolver.Resolve(candidate),
                    context.Provenance),
                presetContext);

            if (planning.IsFailure)
            {

                blockers.Add(planning.Error.Message);

            }
            else
            {

                presetPlan = planning.Value.Plan;

                foreach (ConfigurationPresetPrerequisiteStatus status in presetPlan.Prerequisites)
                {

                    if (status.Prerequisite.Required && !status.IsSatisfied)
                    {

                        blockers.Add(
                            $"Preset prerequisite '{status.Prerequisite.Id}' is unmet: {status.Detail} "
                            + $"Resolve with: {status.Prerequisite.ResolutionCommand}");

                    }

                }

            }

        }

        if (draft.WebResearchRequested
            && preset is not null
            && !string.Equals(preset.Id, "research", StringComparison.OrdinalIgnoreCase)
            && !candidate.ResolveWebBrowsing().Enabled)
        {

            notes.Add(
                "The web-research credential is stored, but web research stays disabled under the "
                + $"'{preset.Id}' preset. Enable it with 'arcanum preset apply research'.");

        }

        SetupChangePayload[] changes = ConfigurationPathAccessor
            .Diff(context.Snapshot.Settings, candidate)
            .Select(static difference =>
                new SetupChangePayload(difference.Path, difference.Before, difference.After))
            .ToArray();

        SetupCredentialPlanPayload[] credentials = BuildCredentialPlan(
            providerCredential,
            context.WebResearchCredential,
            draft);

        bool credentialChanges = credentials.Any(static credential =>
            !string.Equals(credential.Action, "unchanged", StringComparison.Ordinal));

        bool presetChanges = presetPlan is { IsIdempotent: false };

        SetupCompletionSummaryPayload summary = BuildSummary(
            candidate,
            draft,
            preset,
            presetPlan);

        SetupPlanPayload payload = new(
            step.ToString(),
            blockers.Count == 0,
            changes.Length == 0 && !credentialChanges && !presetChanges,
            changes,
            credentials,
            new SetupConnectivityPayload(
                connectivity.Status.ToString(),
                connectivity.LatencyMs,
                connectivity.ModelsAdvertised,
                connectivity.SelectedModelAdvertised,
                connectivity.Detail),
            [.. blockers],
            [.. notes],
            summary);

        return new SetupPlan(
            candidate,
            payload,
            providerCredential,
            context.WebResearchCredential,
            draft.ProviderCredential == SetupCredentialAction.Store
                && providerCredential.Present
                && !providerCredential.FromEnvironment,
            draft.WebResearchCredential == SetupCredentialAction.Store
                && context.WebResearchCredential.Present
                && !context.WebResearchCredential.FromEnvironment);

    }

    /// <summary>
    /// Applies only the paths the wizard declares ownership of. Everything else on the persisted
    /// snapshot is carried through untouched, so re-running setup never resets unrelated
    /// customization.
    /// </summary>
    internal static ArcanumSettings BuildCandidate(ArcanumSettings persisted, SetupDraft draft)
    {

        ArcanumSettings candidate = ConfigurationPathAccessor.Clone(persisted);

        candidate.Edition = draft.Edition;

        candidate.Host.ListenAny = draft.ListenAny;

        if (!string.IsNullOrWhiteSpace(draft.ProviderName))
        {

            candidate.Providers = UpsertProvider(candidate.Providers, draft);

        }

        if (!string.IsNullOrWhiteSpace(draft.Model))
        {

            candidate.DefaultModel = draft.Model.Trim();

        }

        if (!string.IsNullOrWhiteSpace(draft.WorkspaceRoot))
        {

            candidate.Workspaces.DefaultRoot = draft.WorkspaceRoot.Trim();

        }

        if (draft.WebResearchCredential == SetupCredentialAction.Reference
            && !string.IsNullOrWhiteSpace(draft.WebResearchCredentialEnvironmentVariable))
        {

            candidate.Integrations.WebResearch.CredentialEnvironmentVariable =
                draft.WebResearchCredentialEnvironmentVariable.Trim();

        }

        return candidate;

    }

    private static ProviderSettings[] UpsertProvider(
        ProviderSettings[] providers,
        SetupDraft draft)
    {

        string name = draft.ProviderName.Trim();

        List<ProviderSettings> updated = [.. providers ?? []];

        int index = updated.FindIndex(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

        ProviderSettings provider = index >= 0
            ? updated[index]
            : new ProviderSettings { Name = name };

        provider.Name = name;

        provider.Type = AiProviderKind.OpenAICompatible;

        if (!string.IsNullOrWhiteSpace(draft.ProviderEndpoint))
        {

            provider.Endpoint = draft.ProviderEndpoint.Trim();

        }

        if (draft.ProviderCredential == SetupCredentialAction.Reference
            && !string.IsNullOrWhiteSpace(draft.ProviderCredentialEnvironmentVariable))
        {

            provider.CredentialEnvironmentVariable =
                draft.ProviderCredentialEnvironmentVariable.Trim();

        }

        if (!string.IsNullOrWhiteSpace(draft.Model))
        {

            string model = draft.Model.Trim();

            if (!provider.Models.Any(entry =>
                    string.Equals(entry.Name, model, StringComparison.OrdinalIgnoreCase)))
            {

                provider.Models = [.. provider.Models, new ModelEntry(model)];

            }

        }

        if (index >= 0)
        {

            updated[index] = provider;

        }
        else
        {

            updated.Add(provider);

        }

        return [.. updated];

    }

    private static SetupCredentialPlanPayload[] BuildCredentialPlan(
        SetupCredentialPresence providerCredential,
        SetupCredentialPresence webResearchCredential,
        SetupDraft draft)
    {

        List<SetupCredentialPlanPayload> credentials = [];

        string providerName = string.IsNullOrWhiteSpace(draft.ProviderName)
            ? "(unnamed)"
            : draft.ProviderName.Trim();

        credentials.Add(
            new SetupCredentialPlanPayload(
                "inference-provider",
                providerName,
                Action(draft.ProviderCredential, providerCredential.Present),
                Storage(draft.ProviderCredential, providerCredential.FromEnvironment),
                draft.ProviderCredential == SetupCredentialAction.Reference
                    ? draft.ProviderCredentialEnvironmentVariable
                    : null));

        if (draft.WebResearchRequested
            || draft.WebResearchCredential != SetupCredentialAction.Unchanged)
        {

            credentials.Add(
                new SetupCredentialPlanPayload(
                    "web-research",
                    "perplexity",
                    Action(draft.WebResearchCredential, webResearchCredential.Present),
                    Storage(
                        draft.WebResearchCredential,
                        webResearchCredential.FromEnvironment),
                    draft.WebResearchCredential == SetupCredentialAction.Reference
                        ? draft.WebResearchCredentialEnvironmentVariable
                        : null));

        }

        return [.. credentials];

    }

    private static string Action(SetupCredentialAction action, bool present) =>
        action switch
        {
            SetupCredentialAction.Store => present ? "replace" : "store",
            SetupCredentialAction.Reference => "reference",
            SetupCredentialAction.Clear => present ? "clear" : "unchanged",
            _ => "unchanged",
        };

    private static string Storage(SetupCredentialAction action, bool fromEnvironment) =>
        action switch
        {
            SetupCredentialAction.Store => "os-credential-store-with-encrypted-mirror",
            SetupCredentialAction.Reference => "environment-reference",
            _ => fromEnvironment ? "environment-reference" : "os-credential-store-with-encrypted-mirror",
        };

    private static SetupCompletionSummaryPayload BuildSummary(
        ArcanumSettings candidate,
        SetupDraft draft,
        ConfigurationPresetDefinition? preset,
        ConfigurationPresetPlan? presetPlan)
    {

        ConfigurationPresetCompletionSummary? presetSummary = presetPlan?.CompletionSummary;

        List<string> network =
        [
            candidate.Host.ListenAny
                ? "Host binds all network interfaces (HTTPS required)"
                : "Host binds loopback only",
            candidate.ResolveWebBrowsing().Enabled
                ? "Native web research enabled"
                : "Native web research disabled",
        ];

        ImmutableArray<string> memory = presetSummary?.EnabledMemorySources ?? [];

        string workspace = string.IsNullOrWhiteSpace(draft.WorkspaceRoot)
            ? candidate.Workspaces.DefaultRoot ?? "Not selected"
            : draft.WorkspaceRoot.Trim();

        string campaign = string.IsNullOrWhiteSpace(draft.CampaignName)
            ? "Not selected"
            : draft.CampaignName.Trim();

        return new SetupCompletionSummaryPayload(
            preset is null ? "Unknown" : $"{preset.DisplayName} ({preset.Id} v{preset.Version})",
            presetSummary?.ProviderAndModel
                ?? $"Provider: {draft.ProviderName}; Model: {draft.Model}",
            SetupProviderProbe.ClassifyEndpoint(draft.ProviderEndpoint).ToString(),
            $"Workspace: {workspace}; Campaign: {campaign}",
            [.. network],
            memory.IsDefaultOrEmpty ? ["none"] : [.. memory],
            presetSummary?.ToolPolicy ?? "Ward enabled; unsandboxed tool children disabled",
            presetSummary?.PrivacyState
                ?? (candidate.Host.ListenAny ? "network host binding enabled" : "loopback host binding"),
            presetSummary?.NextRecommendedCommand ?? "arcanum run \"Hello\"");

    }

    private static ProviderSettings? ResolveCurrentProvider(ArcanumSettings settings) =>
        ProviderResolver.TryResolveProviderForModel(
            settings,
            targetModel: null,
            out ProviderSettings? provider,
            out _)
            ? provider
            : (settings.Providers ?? []).FirstOrDefault();

}
