using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

/// <summary>
/// Tracks every <c>llama-server</c> process Arcanum spawns via a small pid sidecar file under
/// <c>ArcanumPaths.ModelCacheDirectory/.pids/</c>, so a subsequent Arcanum run (after a crash or
/// <c>SIGKILL</c> that skipped <see cref="LlamaServerLifecycleHostedService.StopAsync"/>) can sweep and
/// terminate orphaned servers before starting new ones — reclaiming the VRAM/RAM they hold.
///
/// Deliberately conservative: a recorded pid is only killed at sweep time when (1) a process with that
/// pid is still alive, (2) its live <see cref="Process.StartTime"/> matches what was recorded within a
/// tight tolerance (guards against the OS having reused the pid for an unrelated process since the
/// sidecar was written), and (3) its process image name still contains <c>llama-server</c>. Every other
/// process on the machine — including a <c>llama-server</c> the user launched manually outside Arcanum —
/// is left untouched.
/// </summary>
internal static class LlamaProcessRegistry
{

    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    private static string PidDirectory => Path.Combine(ArcanumPaths.ModelCacheDirectory, ".pids");

    /// <summary>Called once a spawned llama-server process is attached and tracked.</summary>
    public static void Record(Process process, string cacheKey, ILogger? logger = null)
    {

        try
        {

            Directory.CreateDirectory(PidDirectory);

            string path = Path.Combine(PidDirectory, process.Id.ToString(CultureInfo.InvariantCulture));

            DateTime startTimeUtc = process.StartTime.ToUniversalTime();

            File.WriteAllLines(path, [cacheKey, startTimeUtc.ToString("o", CultureInfo.InvariantCulture)]);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {

            // Best-effort bookkeeping — a failure here must never block starting the server itself.
            logger?.LogDebug(ex, "Failed to record llama-server pid sidecar for {CacheKey}.", cacheKey);

        }

    }

    /// <summary>Called once a tracked llama-server process is fully detached (stopped or crashed).</summary>
    public static void Remove(int pid, ILogger? logger = null)
    {

        try
        {

            string path = Path.Combine(PidDirectory, pid.ToString(CultureInfo.InvariantCulture));

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            logger?.LogDebug(ex, "Failed to remove llama-server pid sidecar {Pid}.", pid);

        }

    }

    /// <summary>
    /// Scans the pid registry for servers recorded by a previous (crashed/killed) Arcanum run and
    /// terminates any that are still alive and still identifiably llama-server. Every sidecar is
    /// removed as it is processed, whether or not the process it named is swept — a stale record must
    /// never accumulate or be re-checked on the next startup.
    /// </summary>
    public static void SweepOrphans(ILogger logger)
    {

        string directory = PidDirectory;

        if (!Directory.Exists(directory))
        {
            return;

        }

        string[] files;

        try
        {

            files = Directory.GetFiles(directory);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            logger.LogDebug(ex, "Failed to enumerate the llama-server pid registry at {Directory}.", directory);

            return;

        }

        foreach (string file in files)
        {

            SweepOneSidecar(file, logger);

        }

    }

    private static void SweepOneSidecar(string file, ILogger logger)
    {

        string fileName = Path.GetFileName(file);

        if (!int.TryParse(fileName, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid))
        {

            TryDeleteSidecar(file, logger);

            return;

        }

        (string CacheKey, DateTime? StartTimeUtc) recorded = ReadSidecar(file, pid, logger);

        // Remove the sidecar unconditionally before acting on it — if this Arcanum instance ends up
        // re-attaching to (or re-spawning) the same cache key, LlamaServerManager records a fresh entry.
        TryDeleteSidecar(file, logger);

        Process process;

        try
        {

            process = Process.GetProcessById(pid);

        }
        catch (ArgumentException)
        {

            // No process with this pid is running — nothing to sweep.
            return;

        }

        try
        {

            if (!IsSameLlamaServerProcess(process, recorded.StartTimeUtc))
            {
                return;

            }

            logger.LogWarning(
                "Found an orphaned llama-server process (pid {Pid}, cache key {CacheKey}) left running by a "
                    + "previous Arcanum run; terminating it to reclaim VRAM/RAM before starting new servers.",
                pid,
                recorded.CacheKey);

            ProcessTreeKiller.TryKillEntireTree(process, logger, $"orphaned llama-server pid {pid}");

        }
        finally
        {

            process.Dispose();

        }

    }

    private static (string CacheKey, DateTime? StartTimeUtc) ReadSidecar(string file, int pid, ILogger logger)
    {

        try
        {

            string[] lines = File.ReadAllLines(file);

            string cacheKey = lines.Length > 0 ? lines[0] : "(unknown)";

            DateTime? startTimeUtc = lines.Length > 1
                && DateTime.TryParse(
                    lines[1],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTime parsed)
                ? parsed
                : null;

            return (cacheKey, startTimeUtc);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            logger.LogDebug(ex, "Failed to read llama-server pid sidecar {Pid}.", pid);

            return ("(unknown)", null);

        }

    }

    /// <summary>
    /// Guards against pid reuse: the OS may have recycled <paramref name="process"/>'s pid for an
    /// unrelated process since the sidecar was written. Requires both a start-time match (within
    /// <see cref="StartTimeTolerance"/>) and a llama-server-shaped process image name before treating it
    /// as the same process Arcanum spawned.
    /// </summary>
    private static bool IsSameLlamaServerProcess(Process process, DateTime? recordedStartTimeUtc)
    {

        if (recordedStartTimeUtc is not { } expected)
        {
            return false;

        }

        try
        {

            bool startTimeMatches = (process.StartTime.ToUniversalTime() - expected).Duration() <= StartTimeTolerance;

            return startTimeMatches
                && process.ProcessName.Contains("llama-server", StringComparison.OrdinalIgnoreCase);

        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {

            // Cannot confirm identity (process exited mid-check, or access denied) — do not touch it.
            return false;

        }

    }

    private static void TryDeleteSidecar(string file, ILogger logger)
    {

        try
        {

            File.Delete(file);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            logger.LogDebug(ex, "Failed to delete stale llama-server pid sidecar {File}.", file);

        }

    }

}
