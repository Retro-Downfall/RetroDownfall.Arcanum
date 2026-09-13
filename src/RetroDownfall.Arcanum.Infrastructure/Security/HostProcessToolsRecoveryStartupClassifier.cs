using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>The one validated operating-system marker sample taken before recovery opens SQLite.</summary>
internal sealed record HostProcessToolsRecoveryMarkerSnapshot(
    HostProcessToolsMarkerReadResult Marker);

/// <summary>
/// Classifies host-process-tools authority for one active-journal recovery without publishing the
/// process-wide startup decision.
/// </summary>
internal interface IHostProcessToolsRecoveryStartupClassifier
{
    Result<HostProcessToolsRecoveryMarkerSnapshot> CaptureMarker();

    Task<Result<IHostProcessToolsRuntimePolicy>> ClassifyAsync(
        SqliteConnection recoveryConnection,
        Guid expectedInstallationId,
        HostProcessToolsRecoveryMarkerSnapshot marker,
        CancellationToken cancellationToken);
}

/// <summary>The production recovery-only classifier.</summary>
/// <remarks>
/// The marker is captured before the database opens. After recovery-only unlock, the authenticated
/// journal installation identity is matched to the durable authority singleton before the captured
/// marker and row are joined. The decision is published only into a fresh provisional policy; normal
/// bootstrap remains the sole publisher of the process-wide and static host-tools decisions.
/// </remarks>
internal sealed class HostProcessToolsRecoveryStartupClassifier(
    IHostProcessToolsMarkerStore markers,
    IHostProcessToolsEnvironmentProbe environment,
    IHostProcessToolsMarkerPairJoiner joiner) : IHostProcessToolsRecoveryStartupClassifier
{
    private readonly IHostProcessToolsMarkerStore _markers =
        markers ?? throw new ArgumentNullException(nameof(markers));

    private readonly IHostProcessToolsEnvironmentProbe _environment =
        environment ?? throw new ArgumentNullException(nameof(environment));

    private readonly IHostProcessToolsMarkerPairJoiner _joiner =
        joiner ?? throw new ArgumentNullException(nameof(joiner));

    public Result<HostProcessToolsRecoveryMarkerSnapshot> CaptureMarker()
    {
        try
        {
            HostProcessToolsMarkerReadResult marker = _markers.Read();

            bool valid = marker.Status switch
            {
                HostProcessToolsMarkerReadStatus.Present => marker.Marker is not null,

                HostProcessToolsMarkerReadStatus.Absent => marker.Marker is null,

                _ => false,
            };

            return valid
                ? new HostProcessToolsRecoveryMarkerSnapshot(marker)
                : Result<HostProcessToolsRecoveryMarkerSnapshot>.Failure(Refusal().Error);
        }
        catch (Exception)
        {
            return Result<HostProcessToolsRecoveryMarkerSnapshot>.Failure(Refusal().Error);
        }
    }

    public async Task<Result<IHostProcessToolsRuntimePolicy>> ClassifyAsync(
        SqliteConnection recoveryConnection,
        Guid expectedInstallationId,
        HostProcessToolsRecoveryMarkerSnapshot marker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recoveryConnection);

        ArgumentNullException.ThrowIfNull(marker);

        if (expectedInstallationId == Guid.Empty)
        {
            return Result<IHostProcessToolsRuntimePolicy>.Failure(Refusal().Error);
        }

        try
        {
            await GrimoireDatabaseBootstrapper.VerifyExpectedInstallationIdentityAsync(
                recoveryConnection, expectedInstallationId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result<IHostProcessToolsRuntimePolicy>.Failure(Refusal().Error);
        }

        HostProcessToolsRuntimePolicy provisional = new();

        HostProcessToolsStartupGate gate = new(
            _markers,
            new HostProcessToolsAuthorityStore(recoveryConnection),
            _environment,
            _joiner,
            provisional);

        Result<HostProcessToolsStartupDecision> classified = await gate
            .ClassifyAndPublishAsync(marker.Marker, cancellationToken).ConfigureAwait(false);

        if (classified.IsFailure
            || !provisional.IsPublished
            || !provisional.CovenantPermitted)
        {
            return Result<IHostProcessToolsRuntimePolicy>.Failure(Refusal().Error);
        }

        return Result<IHostProcessToolsRuntimePolicy>.Success(provisional);
    }

    private static Result Refusal() => CovenantRecoveryAuthorityBootstrapper.Refusal();
}
