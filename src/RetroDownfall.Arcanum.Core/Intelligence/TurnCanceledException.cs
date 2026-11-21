using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>Cancellation observed by the caller, with the same safe evidence used by a native terminal frame.</summary>
public sealed class TurnCanceledException(CancellationToken cancellationToken)
    : OperationCanceledException("Turn cancelled.", cancellationToken)
{
    public Error Failure => CreateFailure();

    internal static Error CreateFailure() => new(ErrorCodes.Hub.Error, "Turn cancelled.")
    {
        FailureEvidence = new(TurnFailurePhase.CallerCancellation, TurnEffectState.Unknown, TurnRecoveryAction.InspectSession),
    };

    internal static void ThrowIfRequested(CancellationToken token)
    {
        if (token.IsCancellationRequested) throw new TurnCanceledException(token);
    }
}
