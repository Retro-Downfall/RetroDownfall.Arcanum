namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// What a cheap, read-lease-only look at a Campaign path apply request found.
/// </summary>
/// <remarks>
/// Deliberately not a wire shape and not serializable. It exists so a replay can be answered without
/// closing and draining a Campaign's admission: a client retrying an operation that already completed
/// is the most common request this surface sees, and making the cheapest request the most disruptive
/// one is how an operator's retry loop takes a Campaign offline (§10.18).
/// </remarks>
public abstract class CampaignPathApplyProbe
{

    private CampaignPathApplyProbe()
    {
    }

    /// <summary>The operation already completed; the recorded result is the whole answer.</summary>
    public sealed class Terminal : CampaignPathApplyProbe
    {

        internal Terminal(CampaignPathIdentityResultDto result) => Result = result;

        public CampaignPathIdentityResultDto Result { get; }

    }

    /// <summary>
    /// There is work to do: either nothing has started, or this process left an intent behind.
    /// </summary>
    public sealed class Continue : CampaignPathApplyProbe
    {

        internal Continue(CampaignPathApplyContinuation continuation) => Continuation = continuation;

        public CampaignPathApplyContinuation Continuation { get; }

    }

    public static CampaignPathApplyProbe ForTerminal(CampaignPathIdentityResultDto result)
    {

        ArgumentNullException.ThrowIfNull(result);

        return new Terminal(result);

    }

    public static CampaignPathApplyProbe ForContinuation(CampaignPathApplyContinuation continuation)
    {

        ArgumentNullException.ThrowIfNull(continuation);

        return new Continue(continuation);

    }

}

/// <summary>
/// Whether the caller must acquire a fresh exclusive lease or resume the one an interrupted attempt
/// registered.
/// </summary>
/// <remarks>
/// The distinction is not cosmetic. An initial acquisition for an intent that already exists would
/// give one closed scope two owners, each believing it may reopen; resuming adopts the durable owner
/// the interrupted attempt recorded, so exactly one reopening decision survives the crash.
/// </remarks>
public enum CampaignPathApplyContinuationKind : byte
{

    New = 1,

    ActiveIntent = 2,

}

/// <summary>
/// The identity and authenticated digests a continuation carries, and nothing else.
/// </summary>
/// <remarks>
/// It carries no path, no marker bytes, no filesystem capability, and no token authority. A
/// continuation crosses from a released read lease to a not-yet-acquired exclusive one, and anything
/// capability-shaped in that gap would be a capability held while nothing covers it.
/// </remarks>
public sealed record CampaignPathApplyContinuation(
    CampaignPathApplyContinuationKind Kind,
    Guid OperationId,
    CovenantDigest ApplyRequestDigest,
    CovenantDigest EffectDigest);
