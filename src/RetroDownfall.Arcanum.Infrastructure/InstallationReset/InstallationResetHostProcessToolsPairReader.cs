using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal interface IInstallationResetHostProcessToolsDatabaseEvidenceReader
{

    Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadMarkerEvidenceAsync(
        CancellationToken cancellationToken = default);

}

internal interface IInstallationResetHostProcessToolsPairReader
{

    Task<Result<HostProcessToolsMarkerPairJoinResult>> ReadAsync(
        CancellationToken cancellationToken = default);

}

internal interface IInstallationResetStoppedHostProcessToolsPairReader
{

    Task<Result<HostProcessToolsMarkerPairJoinResult>>
        ReadUnderStoppedHostAuthorityAsync(
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken = default);

}

internal sealed class InstallationResetHostProcessToolsPairReader(
    IHostProcessToolsMarkerStore markerStore,
    IInstallationResetHostProcessToolsDatabaseEvidenceReader databaseEvidenceReader,
    IHostProcessToolsMarkerPairJoiner joiner)
    : IInstallationResetHostProcessToolsPairReader,
      IInstallationResetStoppedHostProcessToolsPairReader
{

    public async Task<Result<HostProcessToolsMarkerPairJoinResult>> ReadAsync(
        CancellationToken cancellationToken = default)
    {

        cancellationToken.ThrowIfCancellationRequested();

        Result<HostProcessToolsOsMarkerEvidence?> osEvidence = ReadOsEvidence();

        if (osEvidence.IsFailure)
        {

            return Unavailable();

        }

        if (databaseEvidenceReader is IInstallationResetStoppedHostDataService)
        {

            return Unavailable();

        }

        return await ReadAndJoinAsync(
            osEvidence.Value,
            databaseEvidenceReader.ReadMarkerEvidenceAsync,
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<Result<HostProcessToolsMarkerPairJoinResult>>
        ReadUnderStoppedHostAuthorityAsync(
            IStoppedHostGrimoireAuthorityIssuer issuer,
            CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(issuer);

        cancellationToken.ThrowIfCancellationRequested();

        Result<HostProcessToolsOsMarkerEvidence?> osEvidence = ReadOsEvidence();

        if (osEvidence.IsFailure
            || databaseEvidenceReader is not IInstallationResetStoppedHostDataService
                stoppedHostData)
        {

            return Unavailable();

        }

        return await ReadAndJoinAsync(
            osEvidence.Value,
            token => stoppedHostData.ReadHostToolsEvidenceUnderStoppedHostAuthorityAsync(
                issuer,
                token),
            cancellationToken).ConfigureAwait(false);

    }

    private Result<HostProcessToolsOsMarkerEvidence?> ReadOsEvidence()
    {

        HostProcessToolsMarkerReadResult markerRead;

        try
        {

            markerRead = markerStore.Read();

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            return OsUnavailable();

        }

        if (markerRead is null)
        {

            return OsUnavailable();

        }

        HostProcessToolsOsMarkerEvidence? osMarker;

        switch (markerRead.Status)
        {
            case HostProcessToolsMarkerReadStatus.Present
                when markerRead.Marker is not null:

                osMarker = markerRead.Marker;

                break;

            case HostProcessToolsMarkerReadStatus.Absent
                when markerRead.Marker is null:

                osMarker = null;

                break;

            default:

                return OsUnavailable();
        }

        return Result<HostProcessToolsOsMarkerEvidence?>.Success(osMarker);

    }

    private async Task<Result<HostProcessToolsMarkerPairJoinResult>> ReadAndJoinAsync(
        HostProcessToolsOsMarkerEvidence? osMarker,
        Func<
            CancellationToken,
            Task<Result<HostProcessToolsDatabaseMarkerEvidence>>> readDatabase,
        CancellationToken cancellationToken)
    {

        Result<HostProcessToolsDatabaseMarkerEvidence> database;

        try
        {

            database = await readDatabase(cancellationToken).ConfigureAwait(false);

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            return Unavailable();

        }

        if (database.IsFailure)
        {

            return Unavailable();

        }

        try
        {

            HostProcessToolsMarkerPairJoinResult joined =
                joiner.Join(database.Value, osMarker);

            return joined is null
                ? Unavailable()
                : Result<HostProcessToolsMarkerPairJoinResult>.Success(joined);

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            return Unavailable();

        }

    }

    private static Result<HostProcessToolsMarkerPairJoinResult> Unavailable() =>
        Result<HostProcessToolsMarkerPairJoinResult>.Failure(new Error(
            ErrorCodes.Data.ExternalRemediationRequired,
            "The host-process-tools marker pair requires external remediation."));

    private static Result<HostProcessToolsOsMarkerEvidence?> OsUnavailable() =>
        Result<HostProcessToolsOsMarkerEvidence?>.Failure(new Error(
            ErrorCodes.Data.ExternalRemediationRequired,
            "The host-process-tools operating-system marker evidence is unavailable."));

}
