namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed class LongRestMaintenanceDeferredException(CancellationToken token) : OperationCanceledException(token)
{
}
