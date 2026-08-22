using System.Text.Json;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal static class InstallationResetActiveStoreTestExtensions
{

    internal static async Task<Result<InstallationResetActivePublication>>
        MigrateLegacyV1ForTestsAsync(
            this InstallationResetActiveStore store,
            RetroDownfall.Arcanum.Infrastructure.Backup.ArcanumMaintenanceLock
                heldInstallationLock,
            Guid installationId,
            CancellationToken cancellationToken = default)
    {

        Result<InstallationResetActiveRecoveryState> inspected = await store
            .InspectAsync(cancellationToken).ConfigureAwait(false);

        if (inspected.IsFailure)
        {

            return Result<InstallationResetActivePublication>.Failure(
                inspected.Error);

        }

        return inspected.Value is
            {
                Outcome: InstallationResetActiveRecoveryOutcome.LegacyV1,
                LegacyRecord: { } record,
                LegacyFileIdentity: { } identity,
            }
            ? await store.MigrateLegacyV1Async(
                heldInstallationLock,
                installationId,
                record,
                identity,
                cancellationToken).ConfigureAwait(false)
            : Result<InstallationResetActivePublication>.Failure(new Error(
                ErrorCodes.Data.RecoveryRequired,
                "The legacy test record is not an eligible migration candidate."));

    }

    internal static async Task<Result> WriteLegacyV1ForTestsAsync(
        this InstallationResetActiveStore store,
        InstallationResetActiveRecord record,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(store);

        ArgumentNullException.ThrowIfNull(record);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            record,
            InstallationResetActiveLegacyJsonContext.Default
                .InstallationResetActiveRecord);

        if (payload.Length > InstallationResetActiveStore.MaxBytes)
        {

            return Result.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The legacy test record exceeds the active-store bound."));

        }

        string parent = Path.GetDirectoryName(store.ActivePath)!;

        if (!SecureFilePermissions.TryEnsureOwnerOnlyDirectoryExistsStrict(parent))
        {

            return Result.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The legacy test control directory is unavailable."));

        }

        await File.WriteAllBytesAsync(
            store.ActivePath,
            payload,
            cancellationToken).ConfigureAwait(false);

        return SecureFilePermissions.TryApplyOwnerOnlyFileStrict(store.ActivePath)
            ? Result.Success()
            : Result.Failure(new Error(
                ErrorCodes.Data.ControlPathUnavailable,
                "The legacy test record could not be hardened."));

    }

}
