/// <summary>
/// Serializes tests that observe <see cref="RetroDownfall.Arcanum.Core.Telemetry.ArcanumMetrics"/>.
///
/// <c>ArcanumMetrics.Meter</c> is one process-wide <c>Meter</c> named "Arcanum", and
/// <c>TelemetryService</c>'s <c>MeterListener</c> subscribes by meter <em>name</em> rather than by
/// instance. Every listener in the process therefore observes every Arcanum measurement recorded by
/// every test, so a test asserting an exact snapshot or an <c>Assert.Single</c> over an instrument
/// is only correct while nothing else is emitting metrics concurrently.
///
/// xUnit runs a <c>DisableParallelization</c> collection on its own, never alongside another
/// collection, which is the guarantee these assertions depend on. <c>ApiBootstrapperTelemetryTests</c>
/// and <c>TelemetryServiceWebResearchTests</c> already got that guarantee incidentally by living in
/// the <c>ApiHost</c> and <c>ProcessEnvironment</c> collections; this collection extends it to the
/// metrics tests that had no collection at all and so ran fully parallel.
/// </summary>
[CollectionDefinition(TelemetryCollectionName.Value, DisableParallelization = true)]
public sealed class TelemetryCollection : ICollectionFixture<object>
{
}

internal static class TelemetryCollectionName
{

    internal const string Value = "Telemetry";

}
