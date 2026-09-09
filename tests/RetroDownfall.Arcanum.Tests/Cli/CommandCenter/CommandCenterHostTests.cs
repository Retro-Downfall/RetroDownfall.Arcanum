using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class CommandCenterHostTests
{
    [Theory]
    [InlineData(ServeLaunchStatus.Failed, CliExitCode.NetworkError)]
    [InlineData(ServeLaunchStatus.AuthFailed, CliExitCode.ConfigurationError)]
    public async Task Launch_failure_exits_before_command_center_refresh(
        ServeLaunchStatus status,
        CliExitCode expectedExitCode)
    {
        ServiceCollection services = new();
        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<IArcanumServeLauncher>();
        services.AddSingleton<IArcanumServeLauncher>(
            new FixedServeLauncher(status));

        RecordingConsole console = new();
        services.RemoveAll<IConsoleDispatcher>();
        services.AddSingleton<IConsoleDispatcher>(console);

        await using ServiceProvider provider = services.BuildServiceProvider();
        ICommandCenterHost host = provider.GetRequiredService<ICommandCenterHost>();

        int exitCode = await host.RunAsync(CancellationToken.None);

        Assert.Equal((int)expectedExitCode, exitCode);
        Assert.Contains("command center launch stopped", console.Diagnostics);
    }

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

    private sealed class FixedServeLauncher(ServeLaunchStatus status)
        : IArcanumServeLauncher
    {
        public Task<ServeLaunchResult> EnsureRunningAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                new ServeLaunchResult(
                    status,
                    status == ServeLaunchStatus.AuthFailed
                        ? HealthProbeState.Unauthorized
                        : HealthProbeState.ConnectionRefused,
                    TimeSpan.Zero,
                    null,
                    "command center launch stopped"));
        }
    }

    private sealed class RecordingConsole : IConsoleDispatcher
    {
        internal List<string> Diagnostics { get; } = [];

        public void WritePayload(string value)
        {
        }

        public void WriteDiagnostic(string value) => Diagnostics.Add(value);

        public void WriteVerbose(string value)
        {
        }

        public void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo)
        {
        }

        public void WriteJson(JsonElement value)
        {
        }

        public void BeginJsonStream()
        {
        }
    }
}
