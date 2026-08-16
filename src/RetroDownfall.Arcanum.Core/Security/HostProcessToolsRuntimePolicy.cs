using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>Why the startup gate refused to let this process open Covenant.</summary>
/// <remarks>
/// Separate from the marker disposition because two very different situations both classify as a
/// mismatch. An installation whose durable evidence disagrees with itself has been through something
/// — a restore, a partial transition, a replaced marker — and must not start. An installation that
/// is provably clean and merely started with the escape hatch armed has done nothing yet, and the
/// distinction is what lets the host give one of them a hard stop and the other an instruction.
/// </remarks>
public enum HostProcessToolsStartupBlocker : byte
{

    None = 0,

    /// <summary>Clean durable state, but this host was started with the escape hatch armed.</summary>
    EscapeHatchWithoutTransition = 1,

    /// <summary>A transition was started and never proven complete.</summary>
    PendingTransition = 2,

    /// <summary>The database row and the operating-system marker do not describe the same thing.</summary>
    MarkerMismatch = 3,

    /// <summary>The durable authority row could not be read or validated.</summary>
    AuthorityUnreadable = 4,

}

/// <summary>What the startup marker join decided this process may do.</summary>
public sealed record HostProcessToolsStartupDecision(
    HostProcessToolsMarkerPairDisposition Disposition,
    bool CovenantPermitted,
    bool HostProcessToolsPermitted,
    HostProcessToolsStartupBlocker Blocker = HostProcessToolsStartupBlocker.None);

/// <summary>
/// The process-wide answer to "may anything here open Covenant?".
/// </summary>
/// <remarks>
/// Read by every service that could otherwise derive an envelope key, open a canonical pool, inject
/// Covenant into a prompt, or advertise a Covenant tool. It answers <see langword="false"/> until the
/// startup gate has published, so a service that starts before the gate — or in a process where the
/// gate never ran at all — fails closed rather than inheriting a permissive default (§10.12).
/// </remarks>
public interface IHostProcessToolsRuntimePolicy
{

    bool IsPublished { get; }

    bool CovenantPermitted { get; }

    bool HostProcessToolsPermitted { get; }

    HostProcessToolsMarkerPairDisposition? Disposition { get; }

    /// <summary>Why Covenant was refused, when it was.</summary>
    HostProcessToolsStartupBlocker Blocker { get; }

}

/// <inheritdoc cref="IHostProcessToolsRuntimePolicy"/>
/// <remarks>
/// Publication is one-way in the direction that matters. A second publication may repeat the same
/// decision or take Covenant away, but nothing may give it back inside a process that has already
/// been told this installation is tainted: that is the difference between a policy and a setting.
/// </remarks>
public sealed class HostProcessToolsRuntimePolicy : IHostProcessToolsRuntimePolicy
{

    private readonly Lock _gate = new();

    private HostProcessToolsStartupDecision? _decision;

    public bool IsPublished
    {

        get
        {

            lock (_gate)
            {

                return _decision is not null;

            }

        }

    }

    public bool CovenantPermitted
    {

        get
        {

            lock (_gate)
            {

                return _decision?.CovenantPermitted ?? false;

            }

        }

    }

    public bool HostProcessToolsPermitted
    {

        get
        {

            lock (_gate)
            {

                return _decision?.HostProcessToolsPermitted ?? false;

            }

        }

    }

    public HostProcessToolsMarkerPairDisposition? Disposition
    {

        get
        {

            lock (_gate)
            {

                return _decision?.Disposition;

            }

        }

    }

    public HostProcessToolsStartupBlocker Blocker
    {

        get
        {

            lock (_gate)
            {

                return _decision?.Blocker ?? HostProcessToolsStartupBlocker.None;

            }

        }

    }

    /// <summary>Publishes the gate's decision, refusing any attempt to restore Covenant.</summary>
    public Result Publish(HostProcessToolsStartupDecision decision)
    {

        ArgumentNullException.ThrowIfNull(decision);

        lock (_gate)
        {

            if (_decision is { CovenantPermitted: false } && decision.CovenantPermitted)
            {

                return new Error(
                    ErrorCodes.Covenant.OperatorAuthorityUnavailable,
                    "A process told that this installation is host-process-tools tainted cannot re-open Covenant.");

            }

            _decision = decision;

            return Result.Success();

        }

    }

}
