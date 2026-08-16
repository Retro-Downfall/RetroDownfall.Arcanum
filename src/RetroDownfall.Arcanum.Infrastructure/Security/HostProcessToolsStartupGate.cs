using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The first phase permitted to classify this installation's host-tools state at startup.
/// </summary>
/// <remarks>
/// It runs before any shared pool, envelope key, prompt builder, MCP server, or worker exists,
/// because those are exactly the things that would put decrypted Covenant bytes into a process that
/// may be about to advertise arbitrary host execution. Publishing its decision is what lets every
/// later service ask one question — <see cref="IHostProcessToolsRuntimePolicy.CovenantPermitted"/> —
/// instead of re-deriving the answer from configuration it can be lied to about (§10.12).
///
/// <para>The operating-system marker is read <i>first</i>, and an unreadable or malformed one blocks
/// before the database is opened at all. That ordering is deliberate: the database is the marker a
/// restore can replace, so a process must never form an opinion from it while the independent marker
/// is in an unknown state.</para>
///
/// <para>Only four dispositions exist and only one of them starts normally with Covenant. A tainted
/// pair starts in permanent no-Covenant mode and may advertise the escape hatch; everything else
/// keeps startup closed with <c>Covenant.HostToolsTransitionRequired</c> and the exact offline
/// command, because a host that guessed here would either lose the evidence of an escape or run
/// Covenant beside one.</para>
/// </remarks>
internal sealed class HostProcessToolsStartupGate(
    IHostProcessToolsMarkerStore markers,
    IHostProcessToolsAuthorityStore authority,
    IHostProcessToolsEnvironmentProbe environment,
    IHostProcessToolsMarkerPairJoiner joiner,
    HostProcessToolsRuntimePolicy policy)
{

    /// <summary>The one remediation instruction every blocked disposition prints.</summary>
    internal const string OfflineCommand = "arcanum security host-process-tools enable --yes";

    public async Task<Result<HostProcessToolsStartupDecision>> ClassifyAndPublishAsync(
        CancellationToken cancellationToken)
    {

        HostProcessToolsMarkerReadResult marker = markers.Read();

        if (marker.Status is HostProcessToolsMarkerReadStatus.Unavailable
            or HostProcessToolsMarkerReadStatus.Malformed)
        {

            return Block(
                HostProcessToolsMarkerPairDisposition.MismatchBlocked,
                HostProcessToolsStartupBlocker.MarkerMismatch,
                "The host-process-tools marker could not be read or is malformed.");

        }

        Result<HostProcessToolsAuthorityRow?> read = await authority.TryReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {

            return Block(
                HostProcessToolsMarkerPairDisposition.MismatchBlocked,
                HostProcessToolsStartupBlocker.AuthorityUnreadable,
                "The durable authority row could not be read.");

        }

        if (read.Value is not { } row)
        {

            // A database that has never been installed has no authority row. That is ordinary
            // absence only while the independent marker is absent too; a marker beside it means an
            // earlier tainted installation was replaced, which is exactly the evidence-loss case.
            return marker.Marker is null
                ? PublishClean()
                : Block(
                    HostProcessToolsMarkerPairDisposition.MismatchBlocked,
                    HostProcessToolsStartupBlocker.MarkerMismatch,
                    "An operating-system taint marker exists for an installation with no authority row.");

        }

        HostProcessToolsDatabaseMarkerEvidence database;

        try
        {

            database = row.ToEvidence();

        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {

            return Block(
                HostProcessToolsMarkerPairDisposition.MismatchBlocked,
                HostProcessToolsStartupBlocker.AuthorityUnreadable,
                "The durable authority row does not describe a valid host-tools state.");

        }

        HostProcessToolsMarkerPairJoinResult join = joiner.Join(database, marker.Marker);

        return join.Disposition switch
        {
            HostProcessToolsMarkerPairDisposition.Clean => PublishClean(),

            HostProcessToolsMarkerPairDisposition.TaintedMatched => PublishTainted(),

            HostProcessToolsMarkerPairDisposition.PendingBlocked => Block(
                join.Disposition,
                HostProcessToolsStartupBlocker.PendingTransition,
                "A host-process-tools transition is pending and was never proven complete."),

            _ => Block(
                join.Disposition,
                HostProcessToolsStartupBlocker.MarkerMismatch,
                "The durable authority row and the operating-system marker do not describe the same installation."),
        };

    }

    /// <summary>
    /// A clean installation. It starts normally, and it refuses to run with the escape hatch armed.
    /// </summary>
    /// <remarks>
    /// The environment opt-in on a clean installation is the case the offline transition exists for.
    /// Honouring it here would enable arbitrary host execution without ever recording that it
    /// happened, so it blocks and names the command that records it.
    /// </remarks>
    private Result<HostProcessToolsStartupDecision> PublishClean()
    {

        HostProcessToolsTransitionEnvironment probe = environment.Read();

        if (probe.Edition is ArcanumEdition.Development && probe.EscapeHatchOptIn)
        {

            return Block(
                HostProcessToolsMarkerPairDisposition.MismatchBlocked,
                HostProcessToolsStartupBlocker.EscapeHatchWithoutTransition,
                "This host was started with the host-process-tools escape hatch but has no completed transition.");

        }

        return Publish(new HostProcessToolsStartupDecision(
            HostProcessToolsMarkerPairDisposition.Clean,
            CovenantPermitted: true,
            HostProcessToolsPermitted: false));

    }

    /// <summary>
    /// A proven tainted pair. Covenant is gone for good; the escape hatch is merely configurable.
    /// </summary>
    /// <remarks>
    /// Removing the opt-in suppresses the tool and restores nothing, which is why the two flags are
    /// computed from different sources: one from durable evidence, one from live configuration.
    /// </remarks>
    private Result<HostProcessToolsStartupDecision> PublishTainted()
    {

        HostProcessToolsTransitionEnvironment probe = environment.Read();

        Log.Warning(
            "This installation is host-process-tools tainted; Covenant stays closed for the life of this process.");

        return Publish(new HostProcessToolsStartupDecision(
            HostProcessToolsMarkerPairDisposition.TaintedMatched,
            CovenantPermitted: false,
            HostProcessToolsPermitted: probe.Edition is ArcanumEdition.Development && probe.EscapeHatchOptIn));

    }

    private Result<HostProcessToolsStartupDecision> Publish(HostProcessToolsStartupDecision decision)
    {

        Result published = policy.Publish(decision);

        return published.IsSuccess
            ? Result<HostProcessToolsStartupDecision>.Success(decision)
            : published.Error;

    }

    private Result<HostProcessToolsStartupDecision> Block(
        HostProcessToolsMarkerPairDisposition disposition,
        HostProcessToolsStartupBlocker blocker,
        string reason)
    {

        // Publishing the block is what keeps a service that starts anyway from finding an
        // unpublished policy and deciding for itself.
        _ = policy.Publish(new HostProcessToolsStartupDecision(
            disposition,
            CovenantPermitted: false,
            HostProcessToolsPermitted: false,
            blocker));

        Log.Error("Arcanum cannot start: {Reason} Run `{Command}` while the host is stopped.", reason, OfflineCommand);

        return new Error(
            ErrorCodes.Covenant.HostToolsTransitionRequired,
            $"{reason} Run `{OfflineCommand}` while the host is stopped.");

    }

}
