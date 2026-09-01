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
/// keeps startup closed with <c>Covenant.HostToolsTransitionRequired</c> and the remedy that exists
/// today, because a host that guessed here would either lose the evidence of an escape or run
/// Covenant beside one.</para>
/// </remarks>
internal sealed class HostProcessToolsStartupGate(
    IHostProcessToolsMarkerStore markers,
    IHostProcessToolsAuthorityStore authority,
    IHostProcessToolsEnvironmentProbe environment,
    IHostProcessToolsMarkerPairJoiner joiner,
    HostProcessToolsRuntimePolicy policy)
{

    /// <summary>What an operator whose host merely started with the escape hatch armed can do.</summary>
    /// <remarks>
    /// This used to name <c>arcanum security host-process-tools enable --yes</c>, the offline
    /// transition command — which no verb registers, so an operator who ran it got an unknown-command
    /// parse error and no second instruction. Clearing the variable is the remedy that exists today,
    /// and it is an honest one: the process refuses these tools either way, so removing the opt-in
    /// costs nothing and stops the host warning about a state it is not in.
    /// </remarks>
    internal const string EscapeHatchRemediation =
        "Host process tools stay refused for this process. Clear the "
        + "ARCANUM_ALLOW_HOST_PROCESS_TOOLS environment variable and start the host again.";

    /// <summary>What an operator whose operating-system marker could not be read can do.</summary>
    /// <remarks>
    /// This is the most reachable block in the gate and the one least like the others: no transition
    /// has to have been attempted for a locked keychain or an absent secret service to land here, so
    /// the operator is not looking at damaged evidence at all. Telling them the recorded evidence
    /// disagrees with itself would send them hunting for a corruption that is not there.
    /// </remarks>
    internal const string MarkerUnreadableRemediation =
        "Arcanum cannot tell an escaped installation from a clean one while that marker is "
        + "unreadable. Check that the operating-system credential store is unlocked and reachable, "
        + "then start the host again.";

    /// <summary>What an operator whose durable authority row could not be read or validated can do.</summary>
    /// <remarks>
    /// The database half of the same distinction. A row that will not read, or that does not describe
    /// a state the evidence contract accepts, is a damaged or unavailable Grimoire rather than two
    /// markers telling different stories, and it is repaired the way any unreadable Grimoire is.
    /// </remarks>
    internal const string AuthorityUnreadableRemediation =
        "Arcanum cannot tell an escaped installation from a clean one while that row is unreadable. "
        + "Check that the Grimoire database opens — `arcanum doctor` reports it — and restore it from "
        + "a backup if it does not.";

    /// <summary>What an operator whose durable evidence is genuinely unresolved can do: not much.</summary>
    /// <remarks>
    /// The three dispositions that reach this one are the ones the two-marker protocol exists to
    /// catch: a marker beside an installation that has no row, a transition that was started and
    /// never proven complete, and a pair that names two different installations. Naming the offline
    /// command here would be the fiction this replaced, and naming the environment variable would be
    /// a different one, so it says plainly that no repair has shipped.
    /// </remarks>
    internal const string EvidenceMismatchRemediation =
        "The host stays closed while the recorded host-process-tools evidence is not a settled, "
        + "matching pair, and no offline repair command has shipped.";

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
                "The host-process-tools marker could not be read or is malformed.",
                MarkerUnreadableRemediation);

        }

        Result<HostProcessToolsAuthorityRow?> read = await authority.TryReadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {

            return Block(
                HostProcessToolsMarkerPairDisposition.MismatchBlocked,
                HostProcessToolsStartupBlocker.AuthorityUnreadable,
                "The durable authority row could not be read.",
                AuthorityUnreadableRemediation);

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
                    "An operating-system taint marker exists for an installation with no authority row.",
                    EvidenceMismatchRemediation);

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
                "The durable authority row does not describe a valid host-tools state.",
                AuthorityUnreadableRemediation);

        }

        HostProcessToolsMarkerPairJoinResult join = joiner.Join(database, marker.Marker);

        return join.Disposition switch
        {
            HostProcessToolsMarkerPairDisposition.Clean => PublishClean(),

            HostProcessToolsMarkerPairDisposition.TaintedMatched => PublishTainted(),

            HostProcessToolsMarkerPairDisposition.PendingBlocked => Block(
                join.Disposition,
                HostProcessToolsStartupBlocker.PendingTransition,
                "A host-process-tools transition is pending and was never proven complete.",
                EvidenceMismatchRemediation),

            _ => Block(
                join.Disposition,
                HostProcessToolsStartupBlocker.MarkerMismatch,
                "The durable authority row and the operating-system marker do not describe the same installation.",
                EvidenceMismatchRemediation),
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
                "This host was started with the host-process-tools escape hatch but has no completed transition.",
                EscapeHatchRemediation);

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

    /// <summary>
    /// Refuses, publishing the block and pairing this call site's reason with its own remedy.
    /// </summary>
    /// <remarks>
    /// The remedy travels with the reason rather than being derived from the blocker, because the
    /// blocker is coarser than the diagnosis: <see cref="HostProcessToolsStartupBlocker.MarkerMismatch"/>
    /// covers both an unreadable credential store and two markers that name different installations,
    /// and one sentence for both told the first operator to look for damage that was not there.
    /// </remarks>
    private Result<HostProcessToolsStartupDecision> Block(
        HostProcessToolsMarkerPairDisposition disposition,
        HostProcessToolsStartupBlocker blocker,
        string reason,
        string remediation)
    {

        // Publishing the block is what keeps a service that starts anyway from finding an
        // unpublished policy and deciding for itself.
        _ = policy.Publish(new HostProcessToolsStartupDecision(
            disposition,
            CovenantPermitted: false,
            HostProcessToolsPermitted: false,
            blocker));

        Log.Error(
            "The host-process-tools startup check refused this process: {Reason} {Remediation}",
            reason,
            remediation);

        return new Error(
            ErrorCodes.Covenant.HostToolsTransitionRequired,
            $"{reason} {remediation}");

    }

}
