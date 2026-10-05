using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

[ExcludeFromCodeCoverage] // Reason: IHostedService PID file lifecycle
public sealed class PidFileService : IHostedService
{
    /// <summary>
    /// A claim loses a few races (a stale file removed by another starter, a competing claim still being
    /// written, a file that vanished between the check and the read) before it is reported rather than
    /// retried forever.
    /// </summary>
    private const int MaxClaimAttempts = 10;

    private static readonly TimeSpan ClaimInProgressDelay = TimeSpan.FromMilliseconds(50);

    private readonly string? _path;

    private readonly ILogger<PidFileService> _logger;

    private readonly Func<int, PidFileOwnerProcess?> _lookUp;

    public PidFileService(ILogger<PidFileService> logger)
        : this(ArcanumRuntimeDefaults.Server.PidFilePath, logger, PidFileOwnership.LookUp)
    {
    }

    internal PidFileService(
        string? path,
        ILogger<PidFileService> logger,
        Func<int, PidFileOwnerProcess?> lookUp)
    {
        _path = path;

        _logger = logger;

        _lookUp = lookUp;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_path))
        {
            _logger.LogDebug("PID file is disabled.");

            return;
        }

        string directory = Path.GetDirectoryName(_path)!;

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        for (int attempt = 1; attempt <= MaxClaimAttempts; attempt++)
        {
            if (TryClaim(_path))
            {
                _logger.LogInformation("Wrote PID {Pid} to {Path}.", Environment.ProcessId, _path);

                return;
            }

            PidFileClaim existing = Inspect(_path);

            if (existing.Kind is PidFileClaimKind.InProgress)
            {
                // Another starter created the file a moment ago and has not finished writing its PID. Deleting
                // it as "malformed" would steal that claim, so wait and read it again.
                await Task.Delay(ClaimInProgressDelay, ct).ConfigureAwait(false);

                continue;
            }

            if (existing.Kind is PidFileClaimKind.Live)
            {
                _logger.LogError(
                    "Another Arcanum process is already running (PID {Pid}); PID file {Path}.",
                    existing.Pid,
                    _path);

                throw new InvalidOperationException(
                    $"Another Arcanum process is already running (PID {existing.Pid}). "
                    + $"If no Arcanum process owns PID {existing.Pid}, remove the PID file at '{_path}' and start again.");
            }

            if (existing.Kind is PidFileClaimKind.Replaceable)
            {
                _logger.LogWarning("Removing stale PID file at {Path}.", _path);

                TryDelete(_path);
            }
        }

        throw new InvalidOperationException(
            $"Could not claim the PID file at '{_path}': another Arcanum process is starting at the same time. Start again.");
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_path))
        {
            return Task.CompletedTask;
        }

        try
        {
            if (File.Exists(_path))
            {
                string currentText = File.ReadAllText(_path).Trim();

                if (currentText != Environment.ProcessId.ToString())
                {
                    _logger.LogWarning(
                        "PID file {Path} contains a different PID ({Current}); leaving it.",
                        _path,
                        currentText);

                    return Task.CompletedTask;
                }

                File.Delete(_path);

                _logger.LogInformation("Removed PID file {Path}.", _path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove PID file {Path}.", _path);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the PID file only if it does not exist (an atomic exclusive create), holding it closed to
    /// readers until the PID is written so a competing starter reads the whole claim or none of it.
    /// </summary>
    private static bool TryClaim(string path)
    {
        FileStream stream;

        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }

        using (stream)
        {
            stream.Write(
                Encoding.ASCII.GetBytes(
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));

            stream.Flush(flushToDisk: true);
        }

        return true;
    }

    /// <summary>
    /// Classifies the file that blocked a claim: a live Arcanum owner, a claim another starter is still
    /// writing, or something that no longer names a live owner (malformed, a dead process, a recycled id).
    /// </summary>
    private PidFileClaim Inspect(string path)
    {
        string text;

        DateTimeOffset writtenAt;

        try
        {
            text = File.ReadAllText(path).Trim();

            writtenAt = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new PidFileClaim(PidFileClaimKind.Vanished, 0);
        }
        catch (IOException)
        {
            // A sharing violation: the competing starter still holds the file open while it writes.
            return new PidFileClaim(PidFileClaimKind.InProgress, 0);
        }

        if (text.Length == 0)
        {
            // An empty file is a claim between create and write when it is fresh, and crash residue when it is not.
            return DateTimeOffset.UtcNow - writtenAt <= PidFileOwnership.ClockTolerance
                ? new PidFileClaim(PidFileClaimKind.InProgress, 0)
                : new PidFileClaim(PidFileClaimKind.Replaceable, 0);
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)
            && PidFileOwnership.IsLiveOwner(pid, writtenAt, _lookUp)
                ? new PidFileClaim(PidFileClaimKind.Live, pid)
                : new PidFileClaim(PidFileClaimKind.Replaceable, 0);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A competing starter may have removed or replaced it already; the next attempt re-reads.
        }
    }

    private enum PidFileClaimKind
    {
        Vanished,

        InProgress,

        Live,

        Replaceable,
    }

    private readonly record struct PidFileClaim(PidFileClaimKind Kind, int Pid);
}
