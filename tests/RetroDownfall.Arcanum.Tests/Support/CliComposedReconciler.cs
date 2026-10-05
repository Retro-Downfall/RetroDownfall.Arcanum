using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Operations;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The reconciler registration the CLI container composes, taken from the production composition
/// rather than rebuilt by hand.
/// </summary>
/// <remarks>
/// A test that constructs its own <see cref="LongRunningOperationReconciler"/> pins that construction,
/// not the CLI's: the two can drift, and the CLI's is the one stopped-host bootstrap resolves. Handing
/// out the real descriptor lets a test run the CLI's reconciler exactly as the CLI container builds it,
/// over whatever store, clock, handlers and ownership the test supplies.
/// </remarks>
internal static class CliComposedReconciler
{
    internal static ServiceDescriptor Descriptor()
    {
        ServiceCollection cli = [];

        cli.AddArcanumGrimoireForCli();

        return Assert.Single(
            cli,
            static descriptor => descriptor.ServiceType == typeof(LongRunningOperationReconciler));
    }
}
