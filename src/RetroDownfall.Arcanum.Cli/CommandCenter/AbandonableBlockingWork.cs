namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Runs work that may block inside a system call off the caller's thread, and lets the caller walk away
/// from it when cancelled.
/// </summary>
/// <remarks>
/// <see cref="Task.Run(Func{Task}, CancellationToken)"/> honours its token only until the delegate
/// starts. A delegate that is parked in <c>open(2)</c> on a FIFO, or reading from a stalled mount, never
/// observes the token, so awaiting that task directly strands the awaiting turn for as long as the
/// system call lasts. This helper awaits the started task with <see cref="Task.WaitAsync(CancellationToken)"/>
/// instead: the caller is released the moment the token is cancelled and the delegate is left to finish on
/// its own, with its eventual outcome observed so a late fault is not reported as unobserved.
/// </remarks>
internal static class AbandonableBlockingWork
{
    public static async Task<T> RunAsync<T>(
        Func<Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        cancellationToken.ThrowIfCancellationRequested();

        // The delegate is not handed the token: a cancellation that lands before it starts would
        // otherwise surface as a task that never ran, and the caller's await below already reports it.
        Task<T> started = Task.Run(work, CancellationToken.None);

        _ = started.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await started.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
