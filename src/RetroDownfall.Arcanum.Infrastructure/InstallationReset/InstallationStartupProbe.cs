using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed class InstallationStartupProbe(
    string guardedRoot,
    string configurationPath,
    string databasePath,
    string protectedMasterStatePath,
    IOsCredentialStore credentialStore) : IInstallationStartupProbe
{

    private readonly InstallationResetActiveStore _activeStore = new(guardedRoot);

    public static InstallationStartupProbe CreateDefault() =>
        new(
            ArcanumPaths.GrimoireDirectory,
            ArcanumPaths.ConfigurationFile,
            ArcanumPaths.GrimoireDatabaseFile,
            ArcanumPaths.ApiKeyStoreFile,
            new OsCredentialStore());

    public async Task<Result<ActiveInstallationReset?>> ReadActiveResetAsync(
        CancellationToken cancellationToken = default)
    {

        Result<InstallationResetActiveRecord?> read = await _activeStore
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {

            return Result<ActiveInstallationReset?>.Failure(read.Error);

        }

        InstallationResetActiveRecord? record = read.Value;

        return Result<ActiveInstallationReset?>.Success(
            record is null
                ? null
                : new ActiveInstallationReset(
                    record.Scope,
                    record.Workspace?.WorkspaceRoot,
                    record.PlanId));

    }

    public Result<bool> IsFreshInstallation()
    {

        if (PathExists(configurationPath)
            || PathExists(databasePath)
            || PathExists(databasePath + ".kdf")
            || PathExists(databasePath + ".kdf.pending")
            || PathExists(protectedMasterStatePath))
        {

            return Result<bool>.Success(false);

        }

        OsCredentialStoreResult credential;

        try
        {

            credential = credentialStore.TryGet(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.MasterApiKeyAccount);

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return CredentialProbeFailure();

        }

        return credential.Status switch
        {
            OsCredentialStoreStatus.NotFound => Result<bool>.Success(true),
            OsCredentialStoreStatus.Ok => Result<bool>.Success(false),
            _ => CredentialProbeFailure(),
        };

    }

    private static bool PathExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static Result<bool> CredentialProbeFailure() =>
        Result<bool>.Failure(new Error(
            ErrorCodes.Data.CredentialInventoryUnavailable,
            "The fixed master credential could not be probed safely."));

}
