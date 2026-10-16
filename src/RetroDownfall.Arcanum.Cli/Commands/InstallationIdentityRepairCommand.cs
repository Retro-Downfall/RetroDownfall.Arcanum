using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// <c>arcanum data repair-installation-identity</c>: removes a profile's damaged pre-database
/// installation identity, and nothing else, when nothing is bound to it.
/// </summary>
/// <remarks>
/// A local-only verb. It works on the installation's credential store and Grimoire directly through
/// Infrastructure, takes the installation maintenance lock itself, and needs no running host: a running
/// host is one of the things it refuses for. Every precondition and the removal live in
/// <see cref="InstallationIdentityRepair"/>; this handler plans, asks, and applies, so the operator
/// confirms against the state the apply proves again.
///
/// <para>Exit codes: <c>0</c> for a removal, for a dry run, and for a profile with nothing to repair;
/// <c>1</c> for a refused precondition, with the refusal's code and its own message; <c>2</c> for a
/// confirmation that was declined or could not be asked; <c>130</c> for cancellation.</para>
/// </remarks>
internal sealed class InstallationIdentityRepairCommand(
    InstallationIdentityRepair repair,
    IConsoleDispatcher dispatcher,
    ICliInvocationContext invocationContext,
    IConfirmationPrompt confirmationPrompt)
{
    public async Task<int> Execute(bool dryRun, CancellationToken cancellationToken)
    {
        Result<InstallationIdentityRepairReport> planned = await repair
            .PlanAsync(cancellationToken)
            .ConfigureAwait(false);

        if (planned.IsFailure)
        {
            return Fail(planned.Error);
        }

        InstallationIdentityRepairReport plan = planned.Value;

        if (plan.State is InstallationIdentityRepairState.Absent)
        {
            return WriteOutcome(
                "absent",
                plan,
                dryRun,
                removed: false,
                "This profile carries no installation identity credential item. There is nothing to repair.");
        }

        if (dryRun)
        {
            return WriteOutcome(
                "damaged",
                plan,
                dryRun,
                removed: false,
                "Dry run. This profile's installation identity credential item "
                + $"(service {plan.CredentialService}, account {plan.CredentialAccount}) is present and is "
                + "not one canonical identity, and nothing is bound to it. Applying the repair removes that "
                + "one item and nothing else. Nothing was changed.");
        }

        bool confirmed;

        try
        {
            confirmed = await confirmationPrompt
                .PromptForConfirmationAsync(
                    "Remove this profile's damaged installation identity credential item "
                    + $"(service {plan.CredentialService}, account {plan.CredentialAccount}), and nothing else?",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NonInteractiveConfirmationException)
        {
            return Fail(
                "Confirmation is required to remove the damaged installation identity. Re-run "
                + "interactively, pass --yes, or pass --dry-run to preview. Nothing was changed.",
                CliExitCode.ConfigurationError);
        }

        if (!confirmed)
        {
            return Fail(
                "The repair was not confirmed. Nothing was changed.",
                CliExitCode.ConfigurationError);
        }

        Result<InstallationIdentityRepairReport> applied = await repair
            .ApplyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (applied.IsFailure)
        {
            return Fail(applied.Error);
        }

        InstallationIdentityRepairReport result = applied.Value;

        return result.State is InstallationIdentityRepairState.Removed
            ? WriteOutcome(
                "removed",
                result,
                dryRun,
                removed: true,
                "Removed this profile's damaged installation identity credential item "
                + $"(service {result.CredentialService}, account {result.CredentialAccount}). The next "
                + "start creates its Grimoire under a new identity.")
            : WriteOutcome(
                "absent",
                result,
                dryRun,
                removed: false,
                "The damaged installation identity credential item was already gone. Nothing was changed.");
    }

    private int WriteOutcome(
        string outcome,
        InstallationIdentityRepairReport report,
        bool dryRun,
        bool removed,
        string text)
    {
        if (invocationContext.Options.Json)
        {
            dispatcher.WriteJson(
                new InstallationIdentityRepairPayload(
                    outcome,
                    dryRun,
                    removed,
                    report.CredentialService,
                    report.CredentialAccount),
                CliJsonContext.Default.InstallationIdentityRepairPayload);
        }
        else
        {
            dispatcher.WritePayload(text);
        }

        return (int)CliExitCode.Success;
    }

    private int Fail(Error error) =>
        Fail($"{error.Code}: {error.Message}", CliExitCode.GenericError);

    private int Fail(string message, CliExitCode exitCode)
    {
        dispatcher.WriteDiagnostic(message);

        if (invocationContext.Options.Json)
        {
            dispatcher.WriteJson(
                new CliErrorPayload(message, (int)exitCode),
                CliJsonContext.Default.CliErrorPayload);
        }

        return (int)exitCode;
    }
}
