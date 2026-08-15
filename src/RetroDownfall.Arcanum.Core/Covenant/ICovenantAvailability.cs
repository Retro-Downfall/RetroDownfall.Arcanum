namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// Publishes the current Covenant availability snapshot to every caller that has to decide whether a
/// Covenant path may run.
/// </summary>
/// <remarks>
/// Reading <see cref="Current"/> performs no database or configuration I/O. It sits on the turn hot
/// path, and a gate that had to query storage to learn whether storage was healthy would be both
/// slow and circular.
/// </remarks>
public interface ICovenantAvailability
{

    CovenantAvailabilitySnapshot Current { get; }

}
