using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services.Setup;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Configuration.Presets;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Non-interactive inputs. Credential <em>values</em> are deliberately absent: a secret may only
/// arrive on redirected stdin or as an environment reference, never in argv where it would land in
/// the process table and shell history.
/// </summary>
internal sealed record SetupCommandOptions(
    bool Plan = false,
    bool Apply = false,
    string? Preset = null,
    string? ProviderName = null,
    string? ProviderEndpoint = null,
    string? Model = null,
    string? ProviderKeyEnvironmentVariable = null,
    bool ProviderKeyStdin = false,
    bool ClearProviderKey = false,
    bool? Research = null,
    string? ResearchKeyEnvironmentVariable = null,
    bool ResearchKeyStdin = false,
    string? Workspace = null,
    string? Campaign = null,
    string? Edition = null,
    bool? ListenAny = null,
    bool AllowUnreachableProvider = false);

internal interface ISetupCommand
{

    Task<int> RunAsync(
        SetupCommandOptions options,
        CancellationToken cancellationToken);

}

/// <summary>
/// <c>arcanum setup</c> — the guided, resumable installation workflow (issue #19).
/// </summary>
/// <remarks>
/// Composes the existing authorities rather than adding a second configuration model: the
/// configuration reader/validator/atomic writer, the outbound endpoint guard, the OS-backed
/// credential stores, the preset engine, and the CLI context store. Every choice lives in an
/// in-memory <see cref="SetupDraft"/> until the operator accepts the final plan, so Ctrl+C, EOF, a
/// validation failure, or a failed dependency check all leave the prior configuration, credentials,
/// context, and workspace registry byte-for-byte unchanged.
/// </remarks>
internal sealed class SetupCommand(
    ISetupPlanner planner,
    ISetupCommitter committer,
    ISetupProviderProbe probe,
    IProviderApiKeyResolver apiKeyResolver,
    ISetupPrompt prompt,
    IConsoleDispatcher console,
    ICliInvocationContext invocationContext,
    IGrimoireCliInitialization initialization) : ISetupCommand
{

    private const string OpenAiTemplate = "openai";

    private const string LocalTemplate = "local";

    private const string CustomTemplate = "custom";

    public async Task<int> RunAsync(
        SetupCommandOptions options,
        CancellationToken cancellationToken)
    {

        if (options.Plan && options.Apply)
        {

            console.WriteDiagnostic("Use either --plan or --apply, not both.");

            return (int)CliExitCode.ConfigurationError;

        }

        Result<SetupPlanContext> read = await planner
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {

            console.WriteDiagnostic(read.Error.Message);

            return (int)CliExitCode.ConfigurationError;

        }

        SetupPlanContext context = read.Value;

        SetupDraft draft;

        try
        {

            draft = await ApplyOptionsAsync(planner.Seed(context), options, cancellationToken)
                .ConfigureAwait(false);

        }
        catch (SetupInputException exception)
        {

            console.WriteDiagnostic(exception.Message);

            return (int)CliExitCode.ConfigurationError;

        }

        return options.Plan || options.Apply
            ? await RunNonInteractiveAsync(context, draft, options, cancellationToken)
                .ConfigureAwait(false)
            : await RunInteractiveAsync(context, draft, cancellationToken).ConfigureAwait(false);

    }

    private async Task<int> RunNonInteractiveAsync(
        SetupPlanContext context,
        SetupDraft draft,
        SetupCommandOptions options,
        CancellationToken cancellationToken)
    {

        SetupConnectivityResult connectivity = await ProbeAsync(draft, cancellationToken)
            .ConfigureAwait(false);

        SetupPlan plan = await planner
            .PlanAsync(context, draft, connectivity, SetupStep.Review, cancellationToken)
            .ConfigureAwait(false);

        if (options.Plan)
        {

            Report(new SetupResultPayload(false, SetupStep.Review.ToString(), plan.Payload, [], [], null, null));

            return plan.IsApplicable
                ? (int)CliExitCode.Success
                : (int)CliExitCode.ConfigurationError;

        }

        if (!plan.IsApplicable)
        {

            Report(new SetupResultPayload(false, SetupStep.Review.ToString(), plan.Payload, [], [], "The plan is not applicable.", null));

            return (int)CliExitCode.ConfigurationError;

        }

        SetupCommitOutcome outcome = await CommitUnderExclusiveAsync(
                context,
                draft,
                plan,
                cancellationToken)
            .ConfigureAwait(false);

        Report(
            new SetupResultPayload(
                outcome.Committed,
                outcome.FailedStep.ToString(),
                plan.Payload,
                outcome.Applied,
                outcome.RolledBack,
                outcome.Failure,
                outcome.Recovery));

        return outcome.Committed
            ? (int)CliExitCode.Success
            : (int)CliExitCode.ConfigurationError;

    }

    private async Task<int> RunInteractiveAsync(
        SetupPlanContext context,
        SetupDraft draft,
        CancellationToken cancellationToken)
    {

        SetupStateMachine machine = new();

        SetupConnectivityResult connectivity = SetupConnectivityResult.NotAttempted;

        SetupPlan? plan = null;

        SetupCommitOutcome? outcome = null;

        prompt.Write("Arcanum guided setup. Nothing is written until you accept the final plan.");

        try
        {

            while (!machine.IsComplete)
            {

                SetupNavigation navigation;

                switch (machine.Current)
                {

                    case SetupStep.Edition:

                        navigation = Adopt(
                            ref draft,
                            await RunEditionStepAsync(draft, cancellationToken)
                                .ConfigureAwait(false));

                        break;

                    case SetupStep.Provider:

                        navigation = Adopt(
                            ref draft,
                            await RunProviderStepAsync(draft, cancellationToken)
                                .ConfigureAwait(false));

                        break;

                    case SetupStep.ProviderCredential:

                        navigation = Adopt(
                            ref draft,
                            await RunProviderCredentialStepAsync(draft, cancellationToken)
                                .ConfigureAwait(false));

                        break;

                    case SetupStep.WebResearchCredential:

                        navigation = Adopt(
                            ref draft,
                            await RunWebResearchStepAsync(context, draft, cancellationToken)
                                .ConfigureAwait(false));

                        break;

                    case SetupStep.Connectivity:

                        navigation = ApplyConnectivity(
                            ref draft,
                            ref connectivity,
                            await RunConnectivityStepAsync(draft, cancellationToken)
                                .ConfigureAwait(false));

                        break;

                    case SetupStep.Workspace:

                        navigation = Adopt(
                            ref draft,
                            await RunWorkspaceStepAsync(draft, cancellationToken)
                                .ConfigureAwait(false));

                        break;

                    case SetupStep.Preset:

                        navigation = Adopt(
                            ref draft,
                            await RunPresetStepAsync(draft, cancellationToken)
                                .ConfigureAwait(false));

                        break;

                    case SetupStep.Review:

                        navigation = await RunReviewStepAsync(
                                context,
                                draft,
                                connectivity,
                                value => plan = value,
                                cancellationToken)
                            .ConfigureAwait(false);

                        break;

                    case SetupStep.Commit:

                        navigation = await RunCommitStepAsync(
                                context,
                                draft,
                                plan!,
                                value => outcome = value,
                                cancellationToken)
                            .ConfigureAwait(false);

                        break;

                    default:

                        navigation = SetupNavigation.Advance;

                        break;

                }

                switch (navigation)
                {

                    case SetupNavigation.Advance:

                        _ = machine.MoveNext();

                        break;

                    case SetupNavigation.Back:

                        _ = machine.MoveBack();

                        break;

                    case SetupNavigation.Retry:

                        machine.Reenter(machine.Current);

                        break;

                    default:

                        return Abort();

                }

            }

        }
        catch (OperationCanceledException)
        {

            return Abort();

        }

        if (plan is null)
        {

            return Abort();

        }

        Report(
            new SetupResultPayload(
                outcome?.Committed ?? false,
                (outcome?.FailedStep ?? SetupStep.Review).ToString(),
                plan.Payload,
                outcome?.Applied ?? [],
                outcome?.RolledBack ?? [],
                outcome?.Failure,
                outcome?.Recovery));

        return outcome is { Committed: true }
            ? (int)CliExitCode.Success
            : (int)CliExitCode.ConfigurationError;

    }

    private int Abort()
    {

        console.WriteDiagnostic(
            "Setup was cancelled. No configuration, credential, context, or workspace change was made.");

        return (int)CliExitCode.Cancelled;

    }

    private static SetupNavigation Adopt(ref SetupDraft draft, SetupStepResult result)
    {

        if (result.Draft is not null)
        {

            draft = result.Draft;

        }

        return result.Navigation;

    }

    private static SetupNavigation ApplyConnectivity(
        ref SetupDraft draft,
        ref SetupConnectivityResult connectivity,
        SetupConnectivityStepResult result)
    {

        connectivity = result.Connectivity;

        if (result.Draft is not null)
        {

            draft = result.Draft;

        }

        return result.Navigation;

    }

    private async Task<SetupStepResult> RunEditionStepAsync(
        SetupDraft draft,
        CancellationToken cancellationToken)
    {

        string? edition = await prompt.SelectAsync(
                "Step 1/8 — Runtime edition",
                [
                    new SetupChoice(
                        "local",
                        "Local",
                        "Shipped default; host process tools and diagnostic surfaces stay gated."),
                    new SetupChoice(
                        "development",
                        "Development",
                        "Unlocks gated surfaces only when accompanying startup flags are also set."),
                ],
                draft.Edition == ArcanumEdition.Development ? "development" : "local",
                cancellationToken)
            .ConfigureAwait(false);

        if (edition is null)
        {

            return SetupStepResult.Abort;

        }

        bool? loopbackOnly = await prompt.ConfirmAsync(
                "Keep the host bound to loopback only (recommended)?",
                !draft.ListenAny,
                cancellationToken)
            .ConfigureAwait(false);

        if (loopbackOnly is null)
        {

            return SetupStepResult.Abort;

        }

        return SetupStepResult.Next(
            draft with
            {

                Edition = string.Equals(edition, "development", StringComparison.OrdinalIgnoreCase)
                    ? ArcanumEdition.Development
                    : ArcanumEdition.Local,

                ListenAny = !loopbackOnly.Value,

            });

    }

    private async Task<SetupStepResult> RunProviderStepAsync(
        SetupDraft draft,
        CancellationToken cancellationToken)
    {

        string? template = await prompt.SelectAsync(
                "Step 2/8 — Inference provider (OpenAI-compatible endpoints only)",
                [
                    new SetupChoice(
                        OpenAiTemplate,
                        "OpenAI",
                        "The hosted OpenAI API at https://api.openai.com/v1."),
                    new SetupChoice(
                        LocalTemplate,
                        "Local / Ollama",
                        "A local model server exposing an OpenAI-compatible /v1 endpoint."),
                    new SetupChoice(
                        CustomTemplate,
                        "Custom",
                        "Any other OpenAI-compatible endpoint you supply."),
                ],
                DefaultTemplate(draft),
                cancellationToken)
            .ConfigureAwait(false);

        if (template is null)
        {

            return SetupStepResult.Abort;

        }

        (string defaultName, string defaultEndpoint, string defaultModel) = template switch
        {
            LocalTemplate => ("ollama", "http://127.0.0.1:11434/v1", "llama3.1"),
            CustomTemplate => (draft.ProviderName, draft.ProviderEndpoint, draft.Model),
            _ => ("openai", "https://api.openai.com/v1", "gpt-4o-mini"),
        };

        string? name = await prompt.AskAsync(
                "Provider name",
                string.IsNullOrWhiteSpace(draft.ProviderName) ? defaultName : draft.ProviderName,
                cancellationToken)
            .ConfigureAwait(false);

        if (name is null)
        {

            return SetupStepResult.Abort;

        }

        string? endpoint = await prompt.AskAsync(
                "Provider endpoint",
                string.IsNullOrWhiteSpace(draft.ProviderEndpoint)
                    ? defaultEndpoint
                    : draft.ProviderEndpoint,
                cancellationToken)
            .ConfigureAwait(false);

        if (endpoint is null)
        {

            return SetupStepResult.Abort;

        }

        string? model = await prompt.AskAsync(
                "Model",
                string.IsNullOrWhiteSpace(draft.Model) ? defaultModel : draft.Model,
                cancellationToken)
            .ConfigureAwait(false);

        return model is null
            ? SetupStepResult.Abort
            : SetupStepResult.Next(
                draft with
                {

                    ProviderName = name.Trim(),

                    ProviderEndpoint = endpoint.Trim(),

                    Model = model.Trim(),

                });

    }

    private async Task<SetupStepResult> RunProviderCredentialStepAsync(
        SetupDraft draft,
        CancellationToken cancellationToken)
    {

        List<SetupChoice> choices =
        [
            new(
                "value",
                "Enter an API key now",
                "Stored in the OS credential manager with an owner-only encrypted mirror."),
            new(
                "environment",
                "Reference an environment variable",
                "No secret is read or written; the named variable is resolved at run time."),
            new(
                "none",
                "No credential",
                "For keyless local model servers."),
        ];

        SetupCredentialPresence presence = await planner
            .ProviderCredentialPresenceAsync(draft.ProviderName, null, cancellationToken)
            .ConfigureAwait(false);

        if (presence.Present)
        {

            choices.Insert(
                0,
                new SetupChoice(
                    "keep",
                    "Keep the existing credential",
                    presence.FromEnvironment
                        ? "An environment reference already resolves for this provider."
                        : "A credential is already stored for this provider."));

        }

        string? choice = await prompt.SelectAsync(
                "Step 3/8 — Provider credential",
                choices,
                presence.Present ? "keep" : "value",
                cancellationToken)
            .ConfigureAwait(false);

        switch (choice)
        {

            case null:

                return SetupStepResult.Abort;

            case "keep":

                return SetupStepResult.Next(
                    draft with { ProviderCredential = SetupCredentialAction.Unchanged });

            case "none":

                return SetupStepResult.Next(
                    draft with { ProviderCredential = SetupCredentialAction.Clear });

            case "environment":

                string? variable = await prompt.AskAsync(
                        "Environment variable name",
                        EnvironmentCredentialResolver.GetProviderApiKeyEnvironmentVariableName(
                            new ProviderSettings { Name = draft.ProviderName }),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (variable is null)
                {

                    return SetupStepResult.Abort;

                }

                if (!EnvironmentCredentialResolver.IsValidEnvironmentVariableName(variable.Trim()))
                {

                    prompt.Write($"'{variable.Trim()}' is not a valid environment variable name.");

                    return SetupStepResult.Retry;

                }

                return SetupStepResult.Next(
                    draft with
                    {

                        ProviderCredential = SetupCredentialAction.Reference,

                        ProviderCredentialEnvironmentVariable = variable.Trim(),

                    });

            default:

                string? secret = await prompt
                    .AskSecretAsync("Provider API key (input is hidden)", cancellationToken)
                    .ConfigureAwait(false);

                if (secret is null)
                {

                    return SetupStepResult.Abort;

                }

                if (string.IsNullOrWhiteSpace(secret))
                {

                    prompt.Write("The API key must not be empty.");

                    return SetupStepResult.Retry;

                }

                return SetupStepResult.Next(
                    draft with
                    {

                        ProviderCredential = SetupCredentialAction.Store,

                        ProviderCredentialValue = secret,

                    });

        }

    }

    private async Task<SetupStepResult> RunWebResearchStepAsync(
        SetupPlanContext context,
        SetupDraft draft,
        CancellationToken cancellationToken)
    {

        bool? wanted = await prompt.ConfirmAsync(
                "Step 4/8 — Would you like to enable advanced web research using Perplexity?",
                draft.WebResearchRequested,
                cancellationToken)
            .ConfigureAwait(false);

        if (wanted is null)
        {

            return SetupStepResult.Abort;

        }

        if (!wanted.Value)
        {

            return SetupStepResult.Next(
                draft with
                {

                    WebResearchRequested = false,

                    WebResearchCredential = SetupCredentialAction.Unchanged,

                    WebResearchCredentialValue = null,

                });

        }

        if (context.WebResearchCredential.Present)
        {

            bool? keep = await prompt.ConfirmAsync(
                    "A web-research credential is already available. Keep it?",
                    true,
                    cancellationToken)
                .ConfigureAwait(false);

            if (keep is null)
            {

                return SetupStepResult.Abort;

            }

            if (keep.Value)
            {

                return SetupStepResult.Next(
                    draft with
                    {

                        WebResearchRequested = true,

                        WebResearchCredential = SetupCredentialAction.Unchanged,

                    });

            }

        }

        string? secret = await prompt
            .AskSecretAsync("Perplexity API key (input is hidden)", cancellationToken)
            .ConfigureAwait(false);

        if (secret is null)
        {

            return SetupStepResult.Abort;

        }

        if (string.IsNullOrWhiteSpace(secret))
        {

            prompt.Write("The API key must not be empty.");

            return SetupStepResult.Retry;

        }

        return SetupStepResult.Next(
            draft with
            {

                WebResearchRequested = true,

                WebResearchCredential = SetupCredentialAction.Store,

                WebResearchCredentialValue = secret,

            });

    }

    private async Task<SetupConnectivityStepResult> RunConnectivityStepAsync(
        SetupDraft draft,
        CancellationToken cancellationToken)
    {

        prompt.Write("Step 5/8 — Validating the provider (no inference tokens are spent)…");

        SetupConnectivityResult connectivity = await ProbeAsync(draft, cancellationToken)
            .ConfigureAwait(false);

        prompt.Write($"  {connectivity.Status}: {connectivity.Detail}");

        if (connectivity.IsUsable)
        {

            return new SetupConnectivityStepResult(SetupNavigation.Advance, connectivity, null);

        }

        bool? proceed = await prompt.ConfirmAsync(
                "Continue with this provider anyway?",
                false,
                cancellationToken)
            .ConfigureAwait(false);

        return proceed switch
        {
            null => new SetupConnectivityStepResult(SetupNavigation.Abort, connectivity, null),
            true => new SetupConnectivityStepResult(
                SetupNavigation.Advance,
                connectivity,
                draft with { AllowUnreachableProvider = true }),
            _ => new SetupConnectivityStepResult(SetupNavigation.Back, connectivity, null),
        };

    }

    private async Task<SetupStepResult> RunWorkspaceStepAsync(
        SetupDraft draft,
        CancellationToken cancellationToken)
    {

        string? workspace = await prompt.AskAsync(
                "Step 6/8 — Default workspace root",
                string.IsNullOrWhiteSpace(draft.WorkspaceRoot)
                    ? Environment.CurrentDirectory
                    : draft.WorkspaceRoot,
                cancellationToken)
            .ConfigureAwait(false);

        if (workspace is null)
        {

            return SetupStepResult.Abort;

        }

        string? campaign = await prompt.AskAsync(
                "Campaign name (optional; press Enter to skip)",
                draft.CampaignName ?? string.Empty,
                cancellationToken)
            .ConfigureAwait(false);

        return campaign is null
            ? SetupStepResult.Abort
            : SetupStepResult.Next(
                draft with
                {

                    WorkspaceRoot = string.IsNullOrWhiteSpace(workspace) ? null : workspace.Trim(),

                    CampaignName = string.IsNullOrWhiteSpace(campaign) ? null : campaign.Trim(),

                });

    }

    private async Task<SetupStepResult> RunPresetStepAsync(
        SetupDraft draft,
        CancellationToken cancellationToken)
    {

        SetupChoice[] choices = [.. ConfigurationPresetCatalog.All
            .Select(static preset =>
                new SetupChoice(preset.Id, preset.DisplayName, preset.Purpose))];

        string? preset = await prompt.SelectAsync(
                "Step 7/8 — Onboarding preset",
                choices,
                draft.WebResearchRequested ? "research" : draft.PresetId,
                cancellationToken)
            .ConfigureAwait(false);

        if (preset is null)
        {

            return SetupStepResult.Abort;

        }

        if (ConfigurationPresetCatalog.Find(preset) is null)
        {

            prompt.Write(
                $"Unknown preset '{preset}'. Available presets: "
                + string.Join(", ", ConfigurationPresetCatalog.All.Select(static p => p.Id))
                + ".");

            return SetupStepResult.Retry;

        }

        return SetupStepResult.Next(draft with { PresetId = preset });

    }

    private async Task<SetupNavigation> RunReviewStepAsync(
        SetupPlanContext context,
        SetupDraft draft,
        SetupConnectivityResult connectivity,
        Action<SetupPlan> capture,
        CancellationToken cancellationToken)
    {

        SetupPlan plan = await planner
            .PlanAsync(context, draft, connectivity, SetupStep.Review, cancellationToken)
            .ConfigureAwait(false);

        capture(plan);

        prompt.Write("Step 8/8 — Review");

        WritePlanDiagnostics(plan.Payload);

        if (!plan.IsApplicable)
        {

            bool? retry = await prompt.ConfirmAsync(
                    "The plan cannot be applied. Go back and change an answer?",
                    true,
                    cancellationToken)
                .ConfigureAwait(false);

            return retry is true ? SetupNavigation.Back : SetupNavigation.Abort;

        }

        if (plan.Payload.IsIdempotent)
        {

            prompt.Write("Nothing would change; the current installation already matches this plan.");

        }

        bool? accept = invocationContext.Options.Yes
            ? true
            : await prompt.ConfirmAsync("Apply this plan?", true, cancellationToken)
                .ConfigureAwait(false);

        return accept switch
        {
            null => SetupNavigation.Abort,
            true => SetupNavigation.Advance,
            _ => SetupNavigation.Abort,
        };

    }

    private async Task<SetupNavigation> RunCommitStepAsync(
        SetupPlanContext context,
        SetupDraft draft,
        SetupPlan plan,
        Action<SetupCommitOutcome> capture,
        CancellationToken cancellationToken)
    {

        SetupCommitOutcome outcome = await CommitUnderExclusiveAsync(
                context,
                draft,
                plan,
                cancellationToken)
            .ConfigureAwait(false);

        capture(outcome);

        return SetupNavigation.Advance;

    }

    private Task<SetupCommitOutcome> CommitUnderExclusiveAsync(
        SetupPlanContext context,
        SetupDraft draft,
        SetupPlan plan,
        CancellationToken cancellationToken) =>
        initialization.RunExclusiveAsync(
            (_, token) => committer.CommitAsync(context, draft, plan, token),
            cancellationToken);

    /// <summary>
    /// Validates with the credential the provider will actually receive: the value being entered
    /// when one is supplied, the referenced variable when a reference is chosen, nothing when the
    /// operator selected a keyless provider, and otherwise whatever already resolves for this
    /// provider name. Without that last case, re-running setup against an installation whose
    /// credential is already in the secure store would report a spurious authentication failure.
    /// </summary>
    private async Task<SetupConnectivityResult> ProbeAsync(
        SetupDraft draft,
        CancellationToken cancellationToken)
    {

        string? apiKey = draft.ProviderCredential switch
        {
            SetupCredentialAction.Store => draft.ProviderCredentialValue,
            SetupCredentialAction.Clear => null,
            _ => await apiKeyResolver
                .PeekAsync(
                    new ProviderSettings
                    {

                        Name = draft.ProviderName,

                        CredentialEnvironmentVariable =
                            draft.ProviderCredential == SetupCredentialAction.Reference
                                ? draft.ProviderCredentialEnvironmentVariable
                                : null,

                    },
                    cancellationToken)
                .ConfigureAwait(false),
        };

        return await probe
            .ProbeAsync(draft.ProviderEndpoint, draft.Model, apiKey, cancellationToken)
            .ConfigureAwait(false);

    }

    private async Task<SetupDraft> ApplyOptionsAsync(
        SetupDraft seed,
        SetupCommandOptions options,
        CancellationToken cancellationToken)
    {

        SetupDraft draft = seed;

        if (options.Edition is not null)
        {

            draft = Enum.TryParse(options.Edition, ignoreCase: true, out ArcanumEdition edition)
                ? draft with { Edition = edition }
                : throw new SetupInputException(
                    $"Unknown edition '{options.Edition}'. Supported editions: local, development.");

        }

        if (options.ListenAny is { } listenAny)
        {

            draft = draft with { ListenAny = listenAny };

        }

        if (options.ProviderName is not null)
        {

            draft = draft with { ProviderName = options.ProviderName.Trim() };

        }

        if (options.ProviderEndpoint is not null)
        {

            draft = draft with { ProviderEndpoint = options.ProviderEndpoint.Trim() };

        }

        if (options.Model is not null)
        {

            draft = draft with { Model = options.Model.Trim() };

        }

        if (options.Workspace is not null)
        {

            draft = draft with { WorkspaceRoot = options.Workspace.Trim() };

        }

        if (options.Campaign is not null)
        {

            draft = draft with { CampaignName = options.Campaign.Trim() };

        }

        if (options.Preset is not null)
        {

            draft = draft with { PresetId = options.Preset.Trim() };

        }

        if (options.AllowUnreachableProvider)
        {

            draft = draft with { AllowUnreachableProvider = true };

        }

        if (options.ProviderKeyEnvironmentVariable is not null)
        {

            string variable = options.ProviderKeyEnvironmentVariable.Trim();

            if (!EnvironmentCredentialResolver.IsValidEnvironmentVariableName(variable))
            {

                throw new SetupInputException(
                    $"'{variable}' is not a valid environment variable name.");

            }

            draft = draft with
            {

                ProviderCredential = SetupCredentialAction.Reference,

                ProviderCredentialEnvironmentVariable = variable,

            };

        }
        else if (options.ClearProviderKey)
        {

            draft = draft with { ProviderCredential = SetupCredentialAction.Clear };

        }

        if (options.Research is { } research)
        {

            draft = draft with { WebResearchRequested = research };

        }

        if (options.ResearchKeyEnvironmentVariable is not null)
        {

            string variable = options.ResearchKeyEnvironmentVariable.Trim();

            if (!EnvironmentCredentialResolver.IsValidEnvironmentVariableName(variable))
            {

                throw new SetupInputException(
                    $"'{variable}' is not a valid environment variable name.");

            }

            draft = draft with
            {

                WebResearchRequested = true,

                WebResearchCredential = SetupCredentialAction.Reference,

                WebResearchCredentialEnvironmentVariable = variable,

            };

        }

        // Secrets arrive only on redirected stdin, in the documented order: the provider credential
        // first, then the web-research credential. Never argv.
        if (options.ProviderKeyStdin)
        {

            draft = draft with
            {

                ProviderCredential = SetupCredentialAction.Store,

                ProviderCredentialValue = await ReadStdinSecretAsync(
                        "provider",
                        cancellationToken)
                    .ConfigureAwait(false),

            };

        }

        if (options.ResearchKeyStdin)
        {

            draft = draft with
            {

                WebResearchRequested = true,

                WebResearchCredential = SetupCredentialAction.Store,

                WebResearchCredentialValue = await ReadStdinSecretAsync(
                        "web-research",
                        cancellationToken)
                    .ConfigureAwait(false),

            };

        }

        return draft;

    }

    private static async Task<string> ReadStdinSecretAsync(
        string kind,
        CancellationToken cancellationToken)
    {

        if (!Console.IsInputRedirected)
        {

            throw new SetupInputException(
                $"Reading the {kind} credential from stdin requires redirected input.");

        }

        string? line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(line)
            ? throw new SetupInputException($"The {kind} credential read from stdin was empty.")
            : line.Trim();

    }

    private static string DefaultTemplate(SetupDraft draft) =>
        SetupProviderProbe.ClassifyEndpoint(draft.ProviderEndpoint) == SetupEndpointClass.Loopback
            ? LocalTemplate
            : string.IsNullOrWhiteSpace(draft.ProviderEndpoint)
                ? OpenAiTemplate
                : CustomTemplate;

    private void Report(SetupResultPayload payload)
    {

        if (invocationContext.Options.Json)
        {

            console.WriteJson(payload, CliJsonContext.Default.SetupResultPayload);

            return;

        }

        WritePlan(payload.Plan);

        foreach (string entry in payload.Applied)
        {

            console.WritePayload($"  applied: {entry}");

        }

        foreach (string entry in payload.RolledBack)
        {

            console.WritePayload($"  rolled back: {entry}");

        }

        if (payload.Failure is { } failure)
        {

            console.WriteDiagnostic($"Setup failed at {payload.Step}: {failure}");

        }

        if (payload.Recovery is { } recovery)
        {

            console.WriteDiagnostic(recovery);

        }

    }

    private void WritePlan(SetupPlanPayload plan)
    {

        console.WritePayload("Arcanum setup plan");

        console.WritePayload($"  Applicable: {(plan.IsApplicable ? "yes" : "no")}");

        console.WritePayload($"  Idempotent: {(plan.IsIdempotent ? "yes" : "no")}");

        console.WritePayload(
            $"  Provider validation: {plan.Connectivity.Status} — {plan.Connectivity.Detail}");

        console.WritePayload(
            plan.ConfigurationChanges.Length == 0
                ? "  Configuration changes: none"
                : "  Configuration changes:");

        foreach (SetupChangePayload change in plan.ConfigurationChanges)
        {

            console.WritePayload($"    {change.Path}: {change.Current} -> {change.Proposed}");

        }

        console.WritePayload("  Credentials:");

        foreach (SetupCredentialPlanPayload credential in plan.Credentials)
        {

            console.WritePayload(
                $"    {credential.Kind} '{credential.Target}': {credential.Action} "
                + $"({credential.Storage}"
                + (credential.EnvironmentVariable is null
                    ? ")"
                    : $"; {credential.EnvironmentVariable})"));

        }

        foreach (string blocker in plan.Blockers)
        {

            console.WritePayload($"  blocker: {blocker}");

        }

        foreach (string note in plan.Notes)
        {

            console.WritePayload($"  note: {note}");

        }

        SetupCompletionSummaryPayload summary = plan.Summary;

        console.WritePayload("  Completion summary");

        console.WritePayload($"    Active preset: {summary.ActivePreset}");

        console.WritePayload($"    Provider/model: {summary.ProviderAndModel}");

        console.WritePayload($"    Endpoint class: {summary.EndpointClass}");

        console.WritePayload($"    Workspace/campaign: {summary.WorkspaceAndCampaign}");

        console.WritePayload(
            $"    Network capabilities: {string.Join("; ", summary.NetworkCapabilities)}");

        console.WritePayload(
            $"    Memory capabilities: {string.Join("; ", summary.MemoryCapabilities)}");

        console.WritePayload($"    Tool security posture: {summary.ToolSecurityPosture}");

        console.WritePayload($"    Privacy state: {summary.PrivacyState}");

        console.WritePayload($"    Next command: {summary.NextCommand}");

    }

    private void WritePlanDiagnostics(SetupPlanPayload plan)
    {

        prompt.Write(
            plan.ConfigurationChanges.Length == 0
                ? "  Configuration changes: none"
                : "  Configuration changes:");

        foreach (SetupChangePayload change in plan.ConfigurationChanges)
        {

            prompt.Write($"    {change.Path}: {change.Current} -> {change.Proposed}");

        }

        foreach (SetupCredentialPlanPayload credential in plan.Credentials)
        {

            prompt.Write(
                $"    credential {credential.Kind} '{credential.Target}': {credential.Action}");

        }

        foreach (string blocker in plan.Blockers)
        {

            prompt.Write($"    blocker: {blocker}");

        }

        foreach (string note in plan.Notes)
        {

            prompt.Write($"    note: {note}");

        }

    }

    private enum SetupNavigation
    {

        Advance,

        Back,

        /// <summary>Stay on this step so the operator can answer again after a validation message.</summary>
        Retry,

        Abort,

    }

    private readonly record struct SetupStepResult(
        SetupNavigation Navigation,
        SetupDraft? Draft)
    {

        public static SetupStepResult Abort { get; } = new(SetupNavigation.Abort, null);

        public static SetupStepResult Retry { get; } = new(SetupNavigation.Retry, null);

        public static SetupStepResult Next(SetupDraft draft) =>
            new(SetupNavigation.Advance, draft);

    }

    private readonly record struct SetupConnectivityStepResult(
        SetupNavigation Navigation,
        SetupConnectivityResult Connectivity,
        SetupDraft? Draft);

    private sealed class SetupInputException(string message) : Exception(message);

}
