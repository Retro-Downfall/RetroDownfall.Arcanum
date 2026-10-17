using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// <c>arcanum data repair-installation-identity</c>: puts a profile's damaged pre-database installation
/// identity right, and changes nothing else, when no unfinished work is bound to it.
/// </summary>
/// <remarks>
/// A local-only verb. It works on the installation's credential store and Grimoire directly through
/// Infrastructure, takes the installation maintenance lock itself, and needs no running host: a running
/// host is one of the things it refuses for. Every precondition and the change live in
/// <see cref="InstallationIdentityRepair"/>; this handler plans, asks, and applies the plan it asked
/// about, so the apply refuses unless it finds the same choice the operator confirmed.
///
/// <para>Exit codes: <c>0</c> for a repair, for a dry run, and for a profile with nothing to repair;
/// <c>1</c> for a refused precondition, including a profile that changed after the confirmation
/// (<c>Data.PlanChanged</c>), with the refusal's code and its own message; <c>2</c> for a
/// confirmation that was declined or could not be asked, with <c>Data.ConfirmationRequired</c> and its
/// own message; <c>130</c> for cancellation.</para>
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
                changed: false,
                "This profile carries no installation identity credential item. There is nothing to repair.");
        }

        string item = $"(service {plan.CredentialService}, account {plan.CredentialAccount})";

        if (dryRun)
        {
            return WriteOutcome(
                "damaged",
                plan,
                dryRun,
                changed: false,
                "Dry run. This profile's installation identity credential item "
                + $"{item} is present and is "
                + "not one canonical identity, and no unfinished work is bound to it. "
                + plan.Action switch
                {
                    InstallationIdentityRepairAction.Reseed =>
                        "The Grimoire holds its authority row, so applying the repair writes that row's "
                        + "installation identity into the one item and changes nothing else.",
                    InstallationIdentityRepairAction.Restore =>
                        "A closed restore or offline-transition anchor of this profile names the installation, "
                        + "so applying the repair writes that identity back into the one item and changes "
                        + "nothing else.",
                    _ =>
                        "No anchor names an installation, so applying the repair removes that one item and "
                        + "nothing else.",
                }
                + " Nothing was changed.");
        }

        bool confirmed;

        try
        {
            confirmed = await confirmationPrompt
                .PromptForConfirmationAsync(
                    plan.Action switch
                    {
                        InstallationIdentityRepairAction.Reseed =>
                            "Seed this profile's damaged installation identity credential item "
                            + $"{item} again from the installation the Grimoire's authority row carries, "
                            + "and change nothing else?",
                        InstallationIdentityRepairAction.Restore =>
                            "Write back into this profile's damaged installation identity credential item "
                            + $"{item} the identity a closed anchor names, and change nothing else?",
                        _ =>
                            "Remove this profile's damaged installation identity credential item "
                            + $"{item}, and nothing else?",
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NonInteractiveConfirmationException)
        {
            return Fail(
                new Error(
                    ErrorCodes.Data.ConfirmationRequired,
                    "Confirmation is required to repair the damaged installation identity. Re-run "
                    + "interactively, pass --yes, or pass --dry-run to preview. Nothing was changed."),
                CliExitCode.ConfigurationError);
        }

        if (!confirmed)
        {
            return Fail(
                new Error(
                    ErrorCodes.Data.ConfirmationRequired,
                    "The repair was not confirmed. Nothing was changed."),
                CliExitCode.ConfigurationError);
        }

        Result<InstallationIdentityRepairReport> applied = await repair
            .ApplyAsync(plan, cancellationToken)
            .ConfigureAwait(false);

        if (applied.IsFailure)
        {
            return Fail(applied.Error);
        }

        InstallationIdentityRepairReport result = applied.Value;

        return result.State switch
        {
            InstallationIdentityRepairState.Removed => WriteOutcome(
                "removed",
                result,
                dryRun,
                changed: true,
                "Removed this profile's damaged installation identity credential item "
                + $"(service {result.CredentialService}, account {result.CredentialAccount}). The next "
                + "start creates its Grimoire under a new identity."),
            InstallationIdentityRepairState.Restored => WriteOutcome(
                "restored",
                result,
                dryRun,
                changed: true,
                "Wrote the identity a closed anchor names back into this profile's installation identity "
                + $"credential item (service {result.CredentialService}, account {result.CredentialAccount}). "
                + "The next start creates its Grimoire under that identity."),
            InstallationIdentityRepairState.Reseeded => WriteOutcome(
                "reseeded",
                result,
                dryRun,
                changed: true,
                "Seeded this profile's installation identity credential item "
                + $"(service {result.CredentialService}, account {result.CredentialAccount}) again from the "
                + "installation the Grimoire's authority row carries. The next start runs under that identity."),
            _ => WriteOutcome(
                "absent",
                result,
                dryRun,
                changed: false,
                "The damaged installation identity credential item was already gone. Nothing was changed."),
        };
    }

    private int WriteOutcome(
        string outcome,
        InstallationIdentityRepairReport report,
        bool dryRun,
        bool changed,
        string text)
    {
        if (invocationContext.Options.Json)
        {
            dispatcher.WriteJson(
                new InstallationIdentityRepairPayload(
                    outcome,
                    dryRun,
                    Removed: changed && report.State is InstallationIdentityRepairState.Removed,
                    Restored: changed && report.State is InstallationIdentityRepairState.Restored,
                    Reseeded: changed && report.State is InstallationIdentityRepairState.Reseeded,
                    Action: report.Action switch
                    {
                        InstallationIdentityRepairAction.Remove => "remove",
                        InstallationIdentityRepairAction.Restore => "restore",
                        InstallationIdentityRepairAction.Reseed => "reseed",
                        _ => "none",
                    },
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

    private int Fail(Error error) => Fail(error, CliExitCode.GenericError);

    private int Fail(Error error, CliExitCode exitCode)
    {
        string message = $"{error.Code}: {error.Message}";

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
