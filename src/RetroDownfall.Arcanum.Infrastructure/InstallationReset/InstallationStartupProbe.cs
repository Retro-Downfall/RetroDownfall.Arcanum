using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal sealed class InstallationStartupProbe(
    string guardedRoot,
    string configurationPath,
    string databasePath,
    string protectedMasterStatePath,
    IOsCredentialStore credentialStore) : IInstallationStartupProbe
{

    private readonly string _guardedRoot = guardedRoot;

    private readonly InstallationResetActiveStore _activeStore = new(
        guardedRoot,
        credentialStore);

    private readonly string _activeEvidenceParent = Path.GetDirectoryName(
        ArcanumMaintenanceLock.LockPathFor(guardedRoot))!;

    internal IOsCredentialStore CredentialStore => credentialStore;

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

        Result<bool> parentAbsent = ActiveEvidenceParentIsAbsent();

        if (parentAbsent.IsFailure)
        {

            return Result<ActiveInstallationReset?>.Failure(parentAbsent.Error);

        }

        if (parentAbsent.Value)
        {

            return Result<ActiveInstallationReset?>.Success(null);

        }

        Result<AuthoritativePathState> root = ClassifyAuthoritativePath(
            _guardedRoot,
            exactMustBeDirectory: true);

        if (root.IsFailure)
        {

            return Result<ActiveInstallationReset?>.Failure(root.Error);

        }

        Result<InstallationResetActiveRecoveryState> inspected = await _activeStore
            .InspectAsync(cancellationToken)
            .ConfigureAwait(false);

        if (inspected.IsFailure)
        {

            return Result<ActiveInstallationReset?>.Failure(inspected.Error);

        }

        Result<InstallationResetStartupRecoveryState> projected =
            InstallationResetStartupRecovery.Project(inspected.Value);

        if (projected.IsFailure)
        {

            return Result<ActiveInstallationReset?>.Failure(projected.Error);

        }

        return Result<ActiveInstallationReset?>.Success(
            projected.Value.ActiveReset);

    }

    public Result<bool> IsFreshInstallation()
    {

        Result<ActiveInstallationReset?> active;

        try
        {

            active = ReadActiveResetAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or NotSupportedException)
        {

            return ActiveProbeFailure();

        }

        if (active.IsFailure)
        {

            return Result<bool>.Failure(active.Error);

        }

        if (active.Value is not null)
        {

            return Result<bool>.Success(false);

        }

        string[] authoritativePaths =
        [
            configurationPath,
            databasePath,
            databasePath + ".kdf",
            databasePath + ".kdf.pending",
            protectedMasterStatePath,
        ];

        foreach (string path in authoritativePaths)
        {

            Result<AuthoritativePathState> state = ClassifyAuthoritativePath(
                path,
                exactMustBeDirectory: false);

            if (state.IsFailure)
            {

                return Result<bool>.Failure(state.Error);

            }

            if (state.Value is AuthoritativePathState.Present)
            {

                return Result<bool>.Success(false);

            }

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

    private static Result<bool> CredentialProbeFailure() =>
        Result<bool>.Failure(new Error(
            ErrorCodes.Data.CredentialInventoryUnavailable,
            "The fixed master credential could not be probed safely."));

    private static Result<bool> ActiveProbeFailure() =>
        Result<bool>.Failure(new Error(
            ErrorCodes.Data.ControlPathUnavailable,
            "The installation-reset active evidence could not be probed safely."));

    private Result<bool> ActiveEvidenceParentIsAbsent()
    {

        Result<AuthoritativePathState> state = ClassifyAuthoritativePath(
            _activeEvidenceParent,
            exactMustBeDirectory: true);

        return state.IsSuccess
            ? Result<bool>.Success(state.Value is AuthoritativePathState.Absent)
            : Result<bool>.Failure(state.Error);

    }

    private static Result<AuthoritativePathState> ClassifyAuthoritativePath(
        string path,
        bool exactMustBeDirectory)
    {

        Result<NoFollowPathTopologyKind> classified =
            NoFollowPathTopology.Classify(path);

        if (classified.IsFailure)
        {

            return AuthoritativePathFailure();

        }

        return classified.Value switch
        {
            NoFollowPathTopologyKind.Absent =>
                Result<AuthoritativePathState>.Success(
                    AuthoritativePathState.Absent),
            NoFollowPathTopologyKind.Directory =>
                Result<AuthoritativePathState>.Success(
                    AuthoritativePathState.Present),
            NoFollowPathTopologyKind.RegularFile when !exactMustBeDirectory =>
                Result<AuthoritativePathState>.Success(
                    AuthoritativePathState.Present),
            _ => AuthoritativePathFailure(),
        };

    }

    private static Result<AuthoritativePathState> AuthoritativePathFailure() =>
        Result<AuthoritativePathState>.Failure(
            ActiveProbeFailure().Error);

    private enum AuthoritativePathState : byte
    {

        Absent,

        Present,

    }

}
