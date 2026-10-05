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
/// bootstrap remains the sole publisher of the process-wide and static host-tools decisions. A marker
/// whose credential store cannot be read right now, and an identity or authority-row read that meets
/// an I/O failure or a busy or locked catalog, fail with <c>Covenant.Unavailable</c>; a malformed
/// marker and every other refused classification is the manual-recovery refusal.
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

            // A credential store that cannot be read right now is an outage, not a marker that
            // disagrees: the remedy is to unlock the store and start again.
            if (marker.Status is HostProcessToolsMarkerReadStatus.Unavailable)
            {
                return Result<HostProcessToolsRecoveryMarkerSnapshot>.Failure(MarkerOutage());
            }

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
        catch (Exception exception) when (GrimoireDatabaseBootstrapper.IsCatalogOutage(exception))
        {
            // A catalog that cannot be read right now is an outage, not an identity that disagrees:
            // the start is retryable. The terminal finisher answers the same read on the same
            // recovery connection the same way.
            return Result<IHostProcessToolsRuntimePolicy>.Failure(Outage());
        }
        catch (Exception)
        {
            return Result<IHostProcessToolsRuntimePolicy>.Failure(Refusal().Error);
        }

        return await ClassifyAuthorityAsync(
            new HostProcessToolsAuthorityStore(recoveryConnection),
            marker,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Joins the captured marker with the durable authority row through the startup gate, publishing
    /// only into a fresh provisional policy.
    /// </summary>
    internal async Task<Result<IHostProcessToolsRuntimePolicy>> ClassifyAuthorityAsync(
        IHostProcessToolsAuthorityStore authority,
        HostProcessToolsRecoveryMarkerSnapshot marker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);

        ArgumentNullException.ThrowIfNull(marker);

        HostProcessToolsRuntimePolicy provisional = new();

        HostProcessToolsStartupGate gate = new(
            _markers,
            authority,
            _environment,
            _joiner,
            provisional);

        Result<HostProcessToolsStartupDecision> classified;

        try
        {
            classified = await gate
                .ClassifyAndPublishAsync(marker.Marker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && GrimoireDatabaseBootstrapper.IsCatalogOutage(exception))
        {
            // The authority-row read meets the same busy, locked, or unreadable catalog the identity
            // read can, and answers it the same way. Any other failure still escapes to the caller.
            return Result<IHostProcessToolsRuntimePolicy>.Failure(Outage());
        }

        if (classified.IsFailure
            || !provisional.IsPublished
            || !provisional.CovenantPermitted)
        {
            return Result<IHostProcessToolsRuntimePolicy>.Failure(Refusal().Error);
        }

        return Result<IHostProcessToolsRuntimePolicy>.Success(provisional);
    }

    private static Result Refusal() => CovenantRecoveryAuthorityBootstrapper.Refusal();

    private static Error Outage() =>
        new(
            ErrorCodes.Covenant.Unavailable,
            "The catalog's host-process-tools authority could not be read right now.");

    private static Error MarkerOutage() =>
        new(
            ErrorCodes.Covenant.Unavailable,
            "The host-process-tools marker could not be read right now.");
}
