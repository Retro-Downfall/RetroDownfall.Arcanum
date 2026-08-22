using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Configuration.Presets;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Cli.Services.Setup;

internal sealed record SetupCommitOutcome(
    bool Committed,
    SetupStep FailedStep,
    string[] Applied,
    string[] RolledBack,
    string? Failure,
    string? Recovery);

internal interface ISetupCommitter
{

    Task<SetupCommitOutcome> CommitAsync(
        SetupPlanContext context,
        SetupDraft draft,
        SetupPlan plan,
        CancellationToken cancellationToken);

}

/// <summary>
/// Applies an accepted plan in dependency order and unwinds what it can when a later step fails.
/// Credentials are written first because the preset engine's prerequisites read them, and each
/// credential the wizard <em>created</em> is deleted on failure. A credential that <em>replaced</em>
/// or <em>cleared</em> an existing one cannot be restored — the wizard never reads the prior value,
/// and the store deletes both the OS entry and its encrypted mirror — so those cases are reported as
/// an actionable partial-commit state instead of being silently swallowed.
///
/// <para>Cancellation is a failure mode like any other once the first irreversible write has landed.
/// Letting an <see cref="OperationCanceledException"/> escape would skip the unwind and let the
/// caller print its "no change was made" abort message over a half-applied installation, so the
/// commit catches it and rolls back — on <see cref="CancellationToken.None"/>, because compensating
/// work driven by an already-cancelled token cancels itself. Cancellation that arrives before
/// anything has been applied still propagates: there the abort message is true.</para>
/// </summary>
internal sealed class SetupCommitter(
    IConfigurationCommandExclusiveWriter configurationWriter,
    IConfigurationPresetService presetService,
    IProviderCredentialStore providerCredentialStore,
    IWebResearchCredentialStore webResearchCredentialStore,
    ICliContextStore contextStore,
    ICliContextExclusiveWriter contextWriter,
    ISetupPlanner planner) : ISetupCommitter
{

    public async Task<SetupCommitOutcome> CommitAsync(
        SetupPlanContext context,
        SetupDraft draft,
        SetupPlan plan,
        CancellationToken cancellationToken)
    {

        List<string> applied = [];

        List<string> rolledBack = [];

        bool providerCredentialCreated = false;

        bool providerCredentialReplaced = false;

        bool providerCredentialCleared = false;

        bool webResearchCredentialCreated = false;

        bool webResearchCredentialReplaced = false;

        bool webResearchCredentialCleared = false;

        bool configurationWritten = false;

        Result revalidated = await planner
            .RevalidateAsync(context, draft, plan, cancellationToken)
            .ConfigureAwait(false);

        if (revalidated.IsFailure)
        {

            return new SetupCommitOutcome(
                Committed: false,
                SetupStep.Commit,
                [],
                [],
                revalidated.Error.Message,
                Recovery: null);

        }

        try
        {

            if (draft.ProviderCredential == SetupCredentialAction.Store
                && !string.IsNullOrWhiteSpace(draft.ProviderCredentialValue))
            {

                await providerCredentialStore
                    .SaveApiKeyAsync(
                        draft.ProviderName.Trim(),
                        draft.ProviderCredentialValue,
                        cancellationToken)
                    .ConfigureAwait(false);

                providerCredentialCreated = !plan.ProviderCredentialWillReplaceExisting;

                providerCredentialReplaced = plan.ProviderCredentialWillReplaceExisting;

                applied.Add($"Stored the provider credential for '{draft.ProviderName.Trim()}'.");

            }
            else if (draft.ProviderCredential == SetupCredentialAction.Clear
                && plan.ProviderCredential.Present
                && !plan.ProviderCredential.FromEnvironment)
            {

                await providerCredentialStore
                    .DeleteApiKeyAsync(draft.ProviderName.Trim(), cancellationToken)
                    .ConfigureAwait(false);

                providerCredentialCleared = true;

                applied.Add($"Deleted the provider credential for '{draft.ProviderName.Trim()}'.");

            }

            if (draft.WebResearchCredential == SetupCredentialAction.Store
                && !string.IsNullOrWhiteSpace(draft.WebResearchCredentialValue))
            {

                await webResearchCredentialStore
                    .SavePerplexityApiKeyAsync(
                        draft.WebResearchCredentialValue,
                        cancellationToken)
                    .ConfigureAwait(false);

                webResearchCredentialCreated = !plan.WebResearchCredentialWillReplaceExisting;

                webResearchCredentialReplaced = plan.WebResearchCredentialWillReplaceExisting;

                applied.Add("Stored the web-research credential.");

            }
            else if (draft.WebResearchCredential == SetupCredentialAction.Clear
                && plan.WebResearchCredential.Present
                && !plan.WebResearchCredential.FromEnvironment)
            {

                await webResearchCredentialStore
                    .DeletePerplexityApiKeyAsync(cancellationToken)
                    .ConfigureAwait(false);

                webResearchCredentialCleared = true;

                applied.Add("Deleted the web-research credential.");

            }

            Result write = await configurationWriter
                .WriteUnderExclusiveAsync(
                    context.Snapshot,
                    plan.Candidate,
                    cancellationToken)
                .ConfigureAwait(false);

            if (write.IsFailure)
            {

                return await RollbackAsync(
                        SetupStep.Commit,
                        write.Error.Message,
                        draft,
                        plan,
                        applied,
                        rolledBack,
                        providerCredentialCreated,
                        providerCredentialReplaced,
                        providerCredentialCleared,
                        webResearchCredentialCreated,
                        webResearchCredentialReplaced,
                        webResearchCredentialCleared,
                        restoreConfiguration: false,
                        context)
                    .ConfigureAwait(false);

            }

            configurationWritten = true;

            applied.Add("Wrote the validated configuration atomically.");

            Result<ConfigurationPresetApplyResult> preset = await presetService
                .ApplyAsync(draft.PresetId, cancellationToken)
                .ConfigureAwait(false);

            if (preset.IsFailure)
            {

                return await RollbackAsync(
                        SetupStep.Preset,
                        preset.Error.Message,
                        draft,
                        plan,
                        applied,
                        rolledBack,
                        providerCredentialCreated,
                        providerCredentialReplaced,
                        providerCredentialCleared,
                        webResearchCredentialCreated,
                        webResearchCredentialReplaced,
                        webResearchCredentialCleared,
                        configurationWritten,
                        context)
                    .ConfigureAwait(false);

            }

            applied.Add(
                preset.Value.AlreadyApplied
                    ? $"Preset '{draft.PresetId}' was already applied at this version."
                    : $"Applied the '{draft.PresetId}' preset.");

            string? contextWarning = SaveContext(draft);

            if (contextWarning is null)
            {

                applied.Add("Selected the workspace and model for this CLI.");

            }

            return new SetupCommitOutcome(
                Committed: true,
                SetupStep.Done,
                [.. applied],
                [.. rolledBack],
                Failure: null,
                contextWarning);

        }
        // Cancellation that lands after the first irreversible write is unwound and reported like any
        // other mid-commit failure. Before that, nothing has been applied, so it propagates and the
        // caller's "no change was made" abort message stays true.
        catch (OperationCanceledException) when (applied.Count > 0)
        {

            return await RollbackAsync(
                    configurationWritten ? SetupStep.Preset : SetupStep.Commit,
                    "Setup was cancelled while the commit was in flight, after part of it had "
                    + "already been applied.",
                    draft,
                    plan,
                    applied,
                    rolledBack,
                    providerCredentialCreated,
                    providerCredentialReplaced,
                    providerCredentialCleared,
                    webResearchCredentialCreated,
                    webResearchCredentialReplaced,
                    webResearchCredentialCleared,
                    configurationWritten,
                    context)
                .ConfigureAwait(false);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException)
        {

            return await RollbackAsync(
                    SetupStep.Commit,
                    exception.Message,
                    draft,
                    plan,
                    applied,
                    rolledBack,
                    providerCredentialCreated,
                    providerCredentialReplaced,
                    providerCredentialCleared,
                    webResearchCredentialCreated,
                    webResearchCredentialReplaced,
                    webResearchCredentialCleared,
                    configurationWritten,
                    context)
                .ConfigureAwait(false);

        }

    }

    /// <summary>
    /// Every compensating write runs on <see cref="CancellationToken.None"/>. The token that reached
    /// the commit may be the very thing that failed it, and a rollback that cancels itself leaves
    /// exactly the half-applied installation this method exists to prevent.
    /// </summary>
    private async Task<SetupCommitOutcome> RollbackAsync(
        SetupStep failedStep,
        string failure,
        SetupDraft draft,
        SetupPlan plan,
        List<string> applied,
        List<string> rolledBack,
        bool providerCredentialCreated,
        bool providerCredentialReplaced,
        bool providerCredentialCleared,
        bool webResearchCredentialCreated,
        bool webResearchCredentialReplaced,
        bool webResearchCredentialCleared,
        bool restoreConfiguration,
        SetupPlanContext context)
    {

        List<string> recovery = [];

        if (restoreConfiguration)
        {

            // The candidate is what is persisted right now, so it — not the pre-wizard snapshot —
            // is the optimistic-concurrency expectation for writing the original values back.
            Result restore = await configurationWriter
                .WriteUnderExclusiveAsync(
                    context.Snapshot with { Settings = plan.Candidate },
                    context.Snapshot.Settings,
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (restore.IsSuccess)
            {

                rolledBack.Add("Restored the previous configuration.");

            }
            else
            {

                recovery.Add(
                    "The configuration could not be restored automatically; inspect it with "
                    + "'arcanum config show' and re-run 'arcanum setup'.");

            }

        }

        if (providerCredentialCreated)
        {

            if (await TryDeleteAsync(
                    () => providerCredentialStore.DeleteApiKeyAsync(
                        draft.ProviderName.Trim(),
                        CancellationToken.None))
                .ConfigureAwait(false))
            {

                rolledBack.Add("Deleted the provider credential this run created.");

            }
            else
            {

                recovery.Add(
                    "A provider credential written by this run could not be deleted; remove it "
                    + $"with 'arcanum key provider delete {draft.ProviderName.Trim()}'.");

            }

        }
        else if (providerCredentialReplaced)
        {

            recovery.Add(
                "The previous provider credential was replaced and cannot be restored; store the "
                + $"credential you want with 'arcanum key provider set {draft.ProviderName.Trim()}'.");

        }
        else if (providerCredentialCleared)
        {

            // Deleting a credential removes both the OS entry and its encrypted mirror, so a restored
            // configuration that still names this provider is not the working installation the
            // "Restored the previous configuration." line above would otherwise imply.
            recovery.Add(
                $"The provider credential for '{draft.ProviderName.Trim()}' was deleted and cannot "
                + "be recovered from Arcanum; store the credential you want with "
                + $"'arcanum key provider set {draft.ProviderName.Trim()}'.");

        }

        if (webResearchCredentialCreated)
        {

            if (await TryDeleteAsync(
                    () => webResearchCredentialStore.DeletePerplexityApiKeyAsync(
                        CancellationToken.None))
                .ConfigureAwait(false))
            {

                rolledBack.Add("Deleted the web-research credential this run created.");

            }
            else
            {

                recovery.Add(
                    "A web-research credential written by this run could not be deleted; remove it "
                    + "with 'arcanum key provider delete perplexity'.");

            }

        }
        else if (webResearchCredentialReplaced)
        {

            recovery.Add(
                "The previous web-research credential was replaced and cannot be restored; store "
                + "the credential you want with 'arcanum key provider set perplexity'.");

        }
        else if (webResearchCredentialCleared)
        {

            recovery.Add(
                "The web-research credential was deleted and cannot be recovered from Arcanum; "
                + "store the credential you want with 'arcanum key provider set perplexity'.");

        }

        return new SetupCommitOutcome(
            Committed: false,
            failedStep,
            [.. applied],
            [.. rolledBack],
            failure,
            recovery.Count == 0 ? null : string.Join(" ", recovery));

    }

    private static async Task<bool> TryDeleteAsync(Func<Task> delete)
    {

        try
        {

            await delete().ConfigureAwait(false);

            return true;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or OperationCanceledException)
        {

            return false;

        }

    }

    /// <summary>
    /// The CLI context is a convenience selection, not part of the configuration contract, so a
    /// failure here is reported rather than rolled back — the installation is already usable.
    /// </summary>
    private string? SaveContext(SetupDraft draft)
    {

        try
        {

            CliContextDocument current = contextStore.Load();

            contextWriter.SaveUnderExclusive(
                current with
                {

                    Model = string.IsNullOrWhiteSpace(draft.Model) ? current.Model : draft.Model.Trim(),

                    CampaignName = string.IsNullOrWhiteSpace(draft.CampaignName)
                        ? current.CampaignName
                        : draft.CampaignName.Trim(),

                });

            return null;

        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {

            return "Setup completed, but the CLI context selection could not be saved: "
                + $"{exception.Message}";

        }

    }

}
