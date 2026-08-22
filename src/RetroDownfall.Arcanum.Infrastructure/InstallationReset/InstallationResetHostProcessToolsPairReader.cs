using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.InstallationReset;

internal interface IInstallationResetHostProcessToolsDatabaseEvidenceReader
{

    Task<Result<HostProcessToolsDatabaseMarkerEvidence>> ReadAsync(
        CancellationToken cancellationToken = default);

}

internal interface IInstallationResetHostProcessToolsPairReader
{

    Task<Result<HostProcessToolsMarkerPairJoinResult>> ReadAsync(
        CancellationToken cancellationToken = default);

}

internal sealed class InstallationResetHostProcessToolsPairReader(
    IHostProcessToolsMarkerStore markerStore,
    IInstallationResetHostProcessToolsDatabaseEvidenceReader databaseEvidenceReader,
    IHostProcessToolsMarkerPairJoiner joiner)
    : IInstallationResetHostProcessToolsPairReader
{

    public async Task<Result<HostProcessToolsMarkerPairJoinResult>> ReadAsync(
        CancellationToken cancellationToken = default)
    {

        cancellationToken.ThrowIfCancellationRequested();

        HostProcessToolsMarkerReadResult markerRead;

        try
        {

            markerRead = markerStore.Read();

        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {

            return Unavailable();

        }

        if (markerRead is null)
        {

            return Unavailable();

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

                return Unavailable();
        }

        Result<HostProcessToolsDatabaseMarkerEvidence> database;

        try
        {

            database = await databaseEvidenceReader
                .ReadAsync(cancellationToken).ConfigureAwait(false);

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

}
