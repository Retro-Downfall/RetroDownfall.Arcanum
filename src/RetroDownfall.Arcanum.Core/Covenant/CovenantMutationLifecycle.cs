using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>The lifecycle refusals shared by operator preparation and mutation publication.</summary>
public static class CovenantMutationLifecycle
{
    public static Error? Refusal(CovenantMutationIntent intent, CovenantLifecycle? current)
    {
        ArgumentNullException.ThrowIfNull(intent);

        if (current is not (null or CovenantLifecycle.Set or CovenantLifecycle.Retired))
        {
            throw new ArgumentOutOfRangeException(nameof(current));
        }

        if (intent.Operation == CovenantOperation.Retire)
        {
            return current is null
                ? new Error(ErrorCodes.Covenant.LifecycleConflict,
                    "There is no Covenant head in this scope and lane to retire.")
                : null;
        }

        if (current != CovenantLifecycle.Retired)
        {
            return null;
        }

        if (intent.Target.Lane == CovenantLane.Proposed)
        {
            return new Error(ErrorCodes.Covenant.LifecycleConflict,
                "An agent cannot reactivate a retired Proposed Covenant lane.");
        }

        return !intent.Reactivate
            ? new Error(ErrorCodes.Covenant.LifecycleConflict,
                "This Covenant head is retired and reactivation was not requested. "
                + "Set reactivate to true (CLI: --reactivate) to restore this key.")
            : null;
    }
}
