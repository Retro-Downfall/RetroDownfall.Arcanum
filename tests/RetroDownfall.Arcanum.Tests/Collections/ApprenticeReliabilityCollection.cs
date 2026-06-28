// ApprenticeServiceReliabilityTests drive ApprenticeService via reflection (private method
// invocation + real Task.Run execution) and use Barrier-based concurrent races. Parallel
// instances interfere via thread-pool scheduling and the shared ApprenticeConcurrencyGate
// timing, making the races non-deterministic under load. Serialize them.
[CollectionDefinition("ApprenticeReliability", DisableParallelization = true)]
public sealed class ApprenticeReliabilityCollection : ICollectionFixture<object>
{
}
