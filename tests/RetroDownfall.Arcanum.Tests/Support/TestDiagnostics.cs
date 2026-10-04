using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework(
    "RetroDownfall.Arcanum.Tests.Support.DiagnosticSinkTestFramework",
    "RetroDownfall.Arcanum.Tests")]

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Reports test-infrastructure problems that have no <see cref="ITestOutputHelper"/> to write to,
/// such as a fixture that fails to delete its temp directory while it is being disposed. The
/// messages go to the xUnit diagnostic sink the runner hands the test framework, which the runner
/// shows because <c>xunit.runner.json</c> enables <c>diagnosticMessages</c>.
/// </summary>
internal static class TestDiagnostics
{
    private static IMessageSink? _sink;

    internal static void Attach(IMessageSink sink) => Volatile.Write(ref _sink, sink);

    internal static void Report(string message)
    {
        if (Volatile.Read(ref _sink) is { } sink)
        {
            _ = sink.OnMessage(new DiagnosticMessage(message));

            return;
        }

        Console.Error.WriteLine(message);
    }
}

/// <summary>
/// The default xUnit framework with one addition: it keeps the diagnostic sink it is given so
/// <see cref="TestDiagnostics"/> can reach it from fixtures that xUnit does not construct.
/// </summary>
public sealed class DiagnosticSinkTestFramework : XunitTestFramework
{
    public DiagnosticSinkTestFramework(IMessageSink messageSink)
        : base(messageSink)
    {
        TestDiagnostics.Attach(messageSink);
    }
}
