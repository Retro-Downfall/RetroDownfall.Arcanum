// A few low-level egress tests still replace the private pinned-address rewrite seam, and Comm Link
// tests coordinate a process environment variable. Keep those remaining global test boundaries serial.
[CollectionDefinition("OutboundUrlGuardDns", DisableParallelization = true)]
public sealed class OutboundUrlGuardDnsCollection : ICollectionFixture<object>
{
}
