// These tests exercise the real Unseen Servant Task.Run and its bounded production shutdown drain.
// CPU-bound suites running beside them can deliberately consume that drain window, which tests host
// overload rather than the lifetime ownership these cases specify. Run this small lifetime suite alone.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UnseenServantLifetimeCollection : ICollectionFixture<object>
{
    public const string Name = "UnseenServantLifetime";
}
