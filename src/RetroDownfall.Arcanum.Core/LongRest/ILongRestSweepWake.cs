namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// Wakes the hosted Long Rest worker so that it scans for a sweep it may drive now instead of waiting out its rescan timer. A wake is only a
/// hint: the worker proves everything again from the durable rows, and a hint that arrives while it is already scanning is kept for the next pass.
/// </summary>
public interface ILongRestSweepWake
{
    void Wake(Guid operationId);
}
