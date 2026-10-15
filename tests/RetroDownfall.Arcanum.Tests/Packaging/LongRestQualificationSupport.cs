using System.Diagnostics;
using System.Globalization;
using System.Text;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// Timestamped progress for a test that runs for tens of minutes. A line goes to the test output (shown when the run ends) and,
/// when a path is given, is appended to a file the wrapper streams to the terminal while the run is still going.
/// </summary>
internal sealed class LongRestQualificationLog(ITestOutputHelper? output, string? progressPath)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly object _sync = new();

    private readonly StringBuilder _transcript = new();

    public TimeSpan Elapsed => _clock.Elapsed;

    public string Transcript
    {
        get
        {
            lock (_sync)
            {
                return _transcript.ToString();
            }
        }
    }

    public void Info(string message)
    {
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"[{_clock.Elapsed:hh\\:mm\\:ss}] {message}");

        lock (_sync)
        {
            _ = _transcript.AppendLine(line);

            if (progressPath is not null)
            {
                try
                {
                    File.AppendAllText(progressPath, line + global::System.Environment.NewLine);
                }
                catch (IOException)
                {
                    // Progress is a courtesy; the transcript still carries the line.
                }
            }
        }

        try
        {
            output?.WriteLine(line);
        }
        catch (InvalidOperationException)
        {
            // The test output is closed once the test method returns.
        }
    }
}

internal static class LongRestQualificationPolling
{
    /// <summary>
    /// Reads until <paramref name="done"/> holds or <paramref name="timeout"/> passes. Every distinct description is logged once, so a
    /// slow phase reads as a timeline rather than silence, and a timeout names the last thing seen.
    /// </summary>
    public static async Task<T> UntilAsync<T>(
        string what,
        Func<CancellationToken, Task<T>> read,
        Func<T, bool> done,
        Func<T, string> describe,
        TimeSpan timeout,
        TimeSpan interval,
        LongRestQualificationLog log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(done);
        ArgumentNullException.ThrowIfNull(describe);
        ArgumentNullException.ThrowIfNull(log);

        Stopwatch clock = Stopwatch.StartNew();

        string? lastDescription = null;

        T value = await read(cancellationToken);

        while (true)
        {
            string description = describe(value);

            if (!string.Equals(description, lastDescription, StringComparison.Ordinal))
            {
                log.Info($"{what}: {description}");

                lastDescription = description;
            }

            if (done(value))
            {
                return value;
            }

            if (clock.Elapsed >= timeout)
            {
                throw new TimeoutException($"{what} did not finish within {timeout}; last observed: {description}");
            }

            await Task.Delay(interval, cancellationToken);

            value = await read(cancellationToken);
        }
    }
}
