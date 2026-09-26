using System.Diagnostics;
using System.Globalization;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class HostedProducerPhaseProgress(string stage, int worker, int total) : IDisposable
{
    private static readonly object ProgressGate = new();

    private readonly string? path = global::System.Environment.GetEnvironmentVariable("ARCANUM_HOSTED_ANALYSIS_PROGRESS");

    private readonly Stopwatch elapsed = Stopwatch.StartNew();

    private int count;

    private bool completed;

    internal static HostedProducerPhaseProgress Start(string stage, int worker, int total = 1)
    {
        HostedProducerPhaseProgress progress = new(stage, worker, total);

        progress.Write("start");

        return progress;
    }

    internal void Report(int value)
    {
        count = value;

        Write("progress");
    }

    internal void Complete()
    {
        count = total;

        Write("complete");

        completed = true;
    }

    public void Dispose()
    {
        if (!completed)
        {
            Write("error");
        }
    }

    private void Write(string phase)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        AppendProgressLine(path, string.Join('\t',
            "HOSTED_PHASE_PROGRESS",
            "stage=" + stage,
            "phase=" + phase,
            "worker=" + worker.ToString(CultureInfo.InvariantCulture),
            "count=" + count.ToString(CultureInfo.InvariantCulture),
            "total=" + total.ToString(CultureInfo.InvariantCulture),
            "elapsed_ms=" + elapsed.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)));
    }

    internal static void AppendProgressLine(string path, string line)
    {
        lock (ProgressGate)
        {
            string? directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, line + global::System.Environment.NewLine);
        }
    }
}
