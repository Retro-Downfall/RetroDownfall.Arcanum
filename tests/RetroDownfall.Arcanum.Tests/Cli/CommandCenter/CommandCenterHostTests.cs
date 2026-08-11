using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterHostTests
{
    /// <summary>
    /// Ctrl+C during the pre-TUI startup (auto-serve readiness polling, MCP refresh, session
    /// restore) cancels the invocation token. The CLI contract fixes cancellation at 130, so the
    /// host must not fold that into its generic failure arm.
    /// </summary>
    [Fact]
    public async Task Cancelling_startup_exits_130_not_the_generic_failure_code()
    {
        ServiceCollection services = new();
        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());
        services.RemoveAll<IArcanumServeLauncher>();
        services.AddSingleton<IArcanumServeLauncher>(new CancellingServeLauncher());

        await using ServiceProvider provider = services.BuildServiceProvider();
        ICommandCenterHost host = provider.GetRequiredService<ICommandCenterHost>();

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        int exitCode = await host.RunAsync(cts.Token);

        Assert.Equal((int)CliExitCode.Cancelled, exitCode);
    }

    private sealed class CancellingServeLauncher : IArcanumServeLauncher
    {
        public Task<ServeLaunchResult> EnsureRunningAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                new ServeLaunchResult(
                    ServeLaunchStatus.AlreadyRunning,
                    HealthProbeState.Healthy,
                    TimeSpan.Zero,
                    null,
                    null));
        }
    }
}
