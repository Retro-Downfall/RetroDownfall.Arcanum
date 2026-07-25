// Tests that replace OutboundUrlGuard.DnsResolver mutate one process-global static
// seam. Keep them in one disabled-parallel collection so each class restores the
// resolver before another test can observe or overwrite it.
[CollectionDefinition("OutboundUrlGuardDns", DisableParallelization = true)]
public sealed class OutboundUrlGuardDnsCollection : ICollectionFixture<object>
{
}
