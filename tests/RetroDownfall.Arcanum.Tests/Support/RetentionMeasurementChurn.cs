namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>Finite-lifetime real-file churn; no fake metadata or storage implementation.</summary>
internal sealed class RetentionMeasurementChurn : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _writer;

    private long _cycles;

    private RetentionMeasurementChurn(string root, bool dated)
    {
        Directory.CreateDirectory(root);

        _writer = Task.Run(() =>
        {
            string[] paths = Enumerable.Range(0, 32).Select(index => Path.Combine(root,
                dated ? $"audit-{29000000 + index:D8}.jsonl" : $".staged-{index}.tmp")).ToArray();

            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    foreach (string path in paths)
                    {
                        File.WriteAllBytes(path, [1, 2, 3]);

                        if (dated) File.SetLastWriteTimeUtc(path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                    }

                    _ready.TrySetResult();

                    Thread.Yield();

                    foreach (string path in paths) File.Delete(path);

                    Interlocked.Increment(ref _cycles);
                }
            }
            catch (Exception failure)
            {
                _ready.TrySetException(failure);

                throw;
            }
        });
    }

    internal long Cycles => Interlocked.Read(ref _cycles);

    internal static async Task<RetentionMeasurementChurn> StartAsync(string root, bool dated = false)
    {
        RetentionMeasurementChurn churn = new(root, dated);

        try
        {
            await churn._ready.Task.WaitAsync(TimeSpan.FromSeconds(10));

            return churn;
        }
        catch (Exception startupFailure)
        {
            try
            {
                await churn.DisposeAsync();
            }
            catch (Exception cleanupFailure) when (ReferenceEquals(cleanupFailure, startupFailure))
            {
                // The ready signal and writer expose the same fault; preserve its original throw below.
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Retention churn startup and cleanup both failed.", startupFailure, cleanupFailure);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();

        try { await _writer; }
        finally { _stop.Dispose(); }
    }
}
