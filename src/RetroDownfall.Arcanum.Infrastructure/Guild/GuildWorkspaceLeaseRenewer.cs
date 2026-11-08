namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Host-owned renewal loop; not a scheduler for recovery or execution.</summary>
internal sealed class GuildWorkspaceLeaseRenewer(GuildWorkspaceLeaseBroker broker, TimeProvider time)
{
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(GuildWorkspaceLeaseBroker.RenewalInterval, time, cancellationToken).ConfigureAwait(false);

                // Rejected holders are forgotten by the broker. This driver never acquires
                // replacement ownership and its duration is not an autonomous-work deadline.
                _ = await broker.RenewHeldAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
