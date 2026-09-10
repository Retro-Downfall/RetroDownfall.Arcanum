// These tests exercise real hosted-service Task.Run paths and bounded production shutdown drains.
// CPU-bound suites running beside them can deliberately consume those drain windows, which tests host
// overload rather than the lifetime ownership these cases specify. Run these small lifetime suites alone.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostedServiceLifetimeCollection : ICollectionFixture<object>
{
    public const string Name = "HostedServiceLifetime";
}
