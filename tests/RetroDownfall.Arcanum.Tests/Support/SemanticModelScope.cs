namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A class fixture that lets the process's semantic models go when the test class that bound them is done.
/// </summary>
/// <remarks>
/// A model keeps everything it has bound, and the Build scans bind every tree of the production compilations. Held to the end of the run,
/// that is memory the fixture lane's one test host cannot spare while it runs the Serve worker discoveries, which analyse compilations of
/// their own and read none of these models. A model is made again when the next reader asks for it, so releasing changes no answer; the
/// collection is not parallel, so no discovery is reading a model while a class ends.
/// </remarks>
public sealed class SemanticModelScope : IDisposable
{
    public void Dispose()
    {
        HostedGrimoireProducerInventory.ReleaseProcessSemanticModels();

        GC.Collect();
    }
}
