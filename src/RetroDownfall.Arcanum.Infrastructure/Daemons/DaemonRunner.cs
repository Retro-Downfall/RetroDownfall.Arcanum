using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.Daemons;

public sealed class DaemonRunner(
    IDaemonRegistry registry,
    IDaemonExecutionRepository repository,
    IEventBus eventBus,
    IDaemonLogAttacher logAttacher) : IDaemonRunner
{

    public Task<Result<DaemonExecutionSummary>> RunAsync(string daemonId, bool force, CancellationToken ct) =>
        RunCoreAsync(daemonId, force, enforceSingleRunning: true, ct);

    public Task<Result<DaemonExecutionSummary>> RunScheduledAsync(string daemonId, CancellationToken ct) =>
        RunCoreAsync(daemonId, force: false, enforceSingleRunning: false, ct);

    private async Task<Result<DaemonExecutionSummary>> RunCoreAsync(
        string daemonId,
        bool force,
        bool enforceSingleRunning,
        CancellationToken ct)
    {
        IDaemonJob? job = registry.TryGetJob(daemonId);

        if (job is null)
        {
            return Result<DaemonExecutionSummary>.Failure(
                new Error("Daemon.NotFound", $"Daemon job '{daemonId}' was not found."));
        }

        if (!force && !job.CanRunOnDemand)
        {
            return Result<DaemonExecutionSummary>.Failure(
                new Error("Daemon.Disabled", $"Daemon job '{daemonId}' is not enabled for on-demand execution."));
        }

        // W3.3 Fix 4: atomic single-running enforcement for the on-demand path.
        // The previous HasRunningExecution + StartAsync pair was a TOCTOU — two
        // concurrent on-demand starts could both pass the check and both start.
        // TryStartAsync reserves the in-flight slot via ConcurrentDictionary.TryAdd
        // so the check and the reservation are one atomic step. The scheduled path
        // (enforceSingleRunning: false) keeps the existing StartAsync behavior.
        string executionId;

        if (enforceSingleRunning)
        {

            executionId = Guid.NewGuid().ToString("N");

            bool started = await repository
                .TryStartAsync(daemonId, job.Name, executionId, ct)
                .ConfigureAwait(false);

            if (!started)
            {

                return Result<DaemonExecutionSummary>.Failure(
                    new Error("Daemon.AlreadyRunning", $"Daemon job '{daemonId}' already has a running execution."));

            }

        }
        else
        {

            executionId = await repository
                .StartAsync(daemonId, job.Name, ct)
                .ConfigureAwait(false);

        }

        Guid runId = Guid.Parse(executionId);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        PublishEvent(job, runId, DaemonEventType.Started, startedAt, executionId);

        CancellationTokenSource? executionCts = repository.GetCancellationTokenSource(executionId);

        if (executionCts is null)
        {
            return Result<DaemonExecutionSummary>.Failure(
                new Error("Daemon.NotFound", $"Execution '{executionId}' was not found after start."));
        }

        try
        {
            using (logAttacher.BeginExecutionScope(executionId))
            {
                await job.RunAsync(executionCts.Token).ConfigureAwait(false);
            }

            DaemonExecutionSummary summary = await repository
                .CompleteAsync(executionId, ct)
                .ConfigureAwait(false);

            PublishEvent(
                job,
                runId,
                DaemonEventType.Completed,
                DateTimeOffset.UtcNow,
                executionId,
                durationMilliseconds: ElapsedMilliseconds(startedAt));

            return Result<DaemonExecutionSummary>.Success(summary);
        }
        catch (OperationCanceledException) when (executionCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            DaemonExecutionSummary summary = await repository
                .CancelAsync(executionId, CancellationToken.None)
                .ConfigureAwait(false);

            PublishEvent(
                job,
                runId,
                DaemonEventType.Failed,
                DateTimeOffset.UtcNow,
                "Job was cancelled.",
                durationMilliseconds: ElapsedMilliseconds(startedAt));

            return Result<DaemonExecutionSummary>.Success(summary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try
            {
                _ = await repository
                    .FailAsync(executionId, "Job was cancelled.", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            return Result<DaemonExecutionSummary>.Failure(
                new Error("Daemon.Cancelled", "Daemon run was cancelled by the caller."));
        }
        catch (Exception ex)
        {
            DaemonExecutionSummary summary = await repository
                .FailAsync(executionId, ex.Message, ct)
                .ConfigureAwait(false);

            PublishEvent(
                job,
                runId,
                DaemonEventType.Failed,
                DateTimeOffset.UtcNow,
                ex.Message,
                durationMilliseconds: ElapsedMilliseconds(startedAt));

            return Result<DaemonExecutionSummary>.Success(summary);
        }
    }

    private void PublishEvent(
        IDaemonJob job,
        Guid runId,
        DaemonEventType eventType,
        DateTimeOffset timestamp,
        string? message = null,
        long? durationMilliseconds = null)
    {
        eventBus.Publish(new DaemonEvent(
            timestamp,
            runId,
            job.Name,
            job.TargetSpell,
            eventType,
            message,
            durationMilliseconds));
    }

    private static long ElapsedMilliseconds(DateTimeOffset startedAt) =>
        (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

}
