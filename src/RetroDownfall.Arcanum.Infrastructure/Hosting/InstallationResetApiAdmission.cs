using RetroDownfall.Arcanum.Core.DataLifecycle;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Process-local API admission state published only by lock-first host startup after the active
/// installation-reset record has been authenticated and admitted.
/// </summary>
internal sealed class InstallationResetApiAdmission
{

    private readonly object _sync = new();

    private InstallationResetRecoveryApiIdentity? _activeRecovery;

    internal InstallationResetRecoveryApiIdentity? ActiveRecovery =>
        Volatile.Read(ref _activeRecovery);

    internal void PublishRecovery(ActiveInstallationReset active)
    {

        ArgumentNullException.ThrowIfNull(active);

        if (!InstallationResetHostStartupAdmission.AllowsRecoveryHost(active))
        {

            throw new ArgumentException(
                "Only an admitted installation-reset host handoff may restrict the API surface.",
                nameof(active));

        }

        InstallationResetRecoveryApiIdentity identity = new(
            active.Scope,
            active.PlanId,
            active.OperationId);

        lock (_sync)
        {

            if (_activeRecovery is null)
            {

                Volatile.Write(ref _activeRecovery, identity);

                return;

            }

            if (_activeRecovery != identity)
            {

                throw new InvalidOperationException(
                    "The recovery API admission identity cannot be replaced in a running host.");

            }

        }

    }

}

internal sealed record InstallationResetRecoveryApiIdentity(
    InstallationResetScope Scope,
    string InstallationPlanId,
    Guid OperationId);
