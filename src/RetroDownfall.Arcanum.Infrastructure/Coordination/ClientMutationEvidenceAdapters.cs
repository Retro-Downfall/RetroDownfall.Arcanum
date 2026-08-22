using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Coordination;

internal sealed class InstallationResetClientMutationEvidenceProbe(
    IInstallationStartupProbe startupProbe)
    : IClientMutationResetEvidenceProbe
{

    private readonly IInstallationStartupProbe _startupProbe =
        startupProbe ?? throw new ArgumentNullException(nameof(startupProbe));

    public Task<Result<ActiveInstallationReset?>> InspectAsync(
        CancellationToken cancellationToken)
        => _startupProbe.ReadActiveResetAsync(cancellationToken);

}

internal sealed class BackupRestoreClientMutationEvidenceProbe(
    string guardedRoot,
    IOsCredentialStore credentials)
    : IClientMutationRestoreEvidenceProbe
{

    private readonly IOsCredentialStore _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    private readonly BackupRestoreActiveEvidenceInspector _inspector = new(
        guardedRoot,
        credentials);

    internal IOsCredentialStore CredentialStore => _credentials;

    public Task<Result<ActiveReplacementRestore?>> InspectAsync(
        CancellationToken cancellationToken) =>
        _inspector.InspectAsync(cancellationToken);

}
