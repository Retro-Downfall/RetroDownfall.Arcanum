using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Hosting;

using Microsoft.Extensions.Logging;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Boots managed MCP servers on host start and stops them on shutdown.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class McpServerBootstrapHostedService : IHostedService
{
    private readonly IMcpGlobalInitializationCoordinator _coordinator;

    private readonly IOptionsMonitor<ArcanumSettings> _options;

    private readonly IHostApplicationLifetime _lifetime;

    private readonly ILogger<McpServerBootstrapHostedService> _logger;

    private readonly object _gate = new();

    private Task? _bootstrapWaiter;

    private Task? _stopWaiter;

    public McpServerBootstrapHostedService(
        IMcpGlobalInitializationCoordinator coordinator,
        IOptionsMonitor<ArcanumSettings> options,
        IHostApplicationLifetime lifetime,
        ILogger<McpServerBootstrapHostedService> logger)
    {
        _coordinator = coordinator;

        _options = options;

        _lifetime = lifetime;

        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        bool blocksStartup = _options.CurrentValue.ResolveMcp().BootstrapBlocksStartup;

        lock (_gate)
        {
            if (_bootstrapWaiter is not null)
            {
                return blocksStartup
                    ? _bootstrapWaiter.WaitAsync(cancellationToken)
                    : Task.CompletedTask;
            }

            if (blocksStartup)
            {
                _bootstrapWaiter = _coordinator.InitializeGlobalAsync(
                    McpGlobalInitializationAuthority.PreReadinessStartup,
                    CancellationToken.None);

                return _bootstrapWaiter.WaitAsync(cancellationToken);
            }

            _bootstrapWaiter = Task.Factory.StartNew(
                    RunOrdinaryBootstrapAsync,
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .Unwrap();

            return Task.CompletedTask;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Task stop = GetOrStartStopOperation();

        return stop.WaitAsync(cancellationToken);
    }

    private Task GetOrStartStopOperation()
    {
        TaskCompletionSource? owner = null;

        Task stop;

        lock (_gate)
        {
            if (_stopWaiter is null)
            {
                owner = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                _stopWaiter = owner.Task;
            }

            stop = _stopWaiter;
        }

        if (owner is not null)
        {
            _ = CompleteStopOperationAsync(owner);
        }

        return stop;
    }

    private async Task CompleteStopOperationAsync(TaskCompletionSource owner)
    {
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            owner.TrySetResult();
        }
        catch (Exception ex)
        {
            owner.TrySetException(ex);
        }
    }

    private async Task StopCoreAsync()
    {
        Task? bootstrap;

        lock (_gate)
        {
            bootstrap = _bootstrapWaiter;
        }

        Exception? stopFailure = null;

        try
        {
            await _coordinator.StopAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopFailure = ex;
        }
        finally
        {
            if (bootstrap is not null)
            {
                try
                {
                    await bootstrap.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Background MCP server bootstrap failed.");
                }
            }
        }

        if (stopFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(stopFailure)
                .Throw();
        }
    }

    private async Task RunOrdinaryBootstrapAsync()
    {
        try
        {
            await _coordinator.InitializeGlobalAsync(
                    McpGlobalInitializationAuthority.OrdinaryHostedWork,
                    _lifetime.ApplicationStopping)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Background MCP server bootstrap failed. Tools will be unavailable until initialization is retried.");

            throw;
        }
    }
}
