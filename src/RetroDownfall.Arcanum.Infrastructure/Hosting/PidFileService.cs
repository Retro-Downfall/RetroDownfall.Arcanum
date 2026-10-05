using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Coordination;

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

    /// <summary>
    /// Appended to the PID file path to name the lock that serialises replacing a stale file. It is retained and
    /// never deleted, so a starter waiting on it can never be holding a handle to a file another starter unlinked.
    /// </summary>
    internal const string ReplacementGateSuffix = ".replace.lock";

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

        PidFileClaimKind lastObserved = PidFileClaimKind.Vanished;

        for (int attempt = 1; attempt <= MaxClaimAttempts; attempt++)
        {
            ClaimAttempt claim = TryClaim(_path);

            if (claim is ClaimAttempt.Claimed)
            {
                _logger.LogInformation("Wrote PID {Pid} to {Path}.", Environment.ProcessId, _path);

                return;
            }

            if (claim is ClaimAttempt.Denied)
            {
                // Windows refuses a create over a file whose delete is still pending with the same error as a real
                // permission problem. The first clears within moments, so wait and look again; the second is
                // reported, with its own message, once the attempts are used up.
                lastObserved = PidFileClaimKind.AccessDenied;

                await Task.Delay(ClaimInProgressDelay, ct).ConfigureAwait(false);

                continue;
            }

            PidFileClaim existing = Inspect(_path);

            lastObserved = existing.Kind;

            if (existing.Kind is PidFileClaimKind.InProgress or PidFileClaimKind.AccessDenied)
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

            if (existing.Kind is PidFileClaimKind.Replaceable
                && await TryReplaceStaleAsync(_path, ct).ConfigureAwait(false))
            {
                _logger.LogInformation("Wrote PID {Pid} to {Path}.", Environment.ProcessId, _path);

                return;
            }
        }

        throw new InvalidOperationException(
            lastObserved is PidFileClaimKind.AccessDenied
                ? $"Could not claim the PID file at '{_path}': access is denied. Check its permissions and those of its directory, or remove it if no Arcanum process owns it, and start again."
                : $"Could not claim the PID file at '{_path}': another Arcanum process is starting at the same time. Start again.");
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
    private static ClaimAttempt TryClaim(string path)
    {
        FileStream stream;

        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException) when (File.Exists(path))
        {
            return ClaimAttempt.Exists;
        }
        catch (UnauthorizedAccessException)
        {
            return ClaimAttempt.Denied;
        }

        using (stream)
        {
            stream.Write(
                Encoding.ASCII.GetBytes(
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));

            stream.Flush(flushToDisk: true);
        }

        return ClaimAttempt.Claimed;
    }

    /// <summary>
    /// Replaces a file that named no live owner, as one step that no other starter can interleave with. Two starters
    /// can both have read the same stale file as replaceable; if each then deleted it and claimed, the slower one
    /// would delete the faster one's fresh claim and both would run. So the delete and the claim happen only while
    /// holding the replacement gate, and only after the file is read again under the gate: a starter that waited for
    /// the gate finds the winner's claim, no longer stale, and leaves it alone. Returns <see langword="true"/> when
    /// this process claimed the file; <see langword="false"/> when the caller should look again.
    /// </summary>
    private async Task<bool> TryReplaceStaleAsync(string path, CancellationToken ct)
    {
        FileStream? gate = TryTakeReplacementGate(path);

        if (gate is null)
        {
            // Another starter is replacing the stale file right now; its result is what the next look finds.
            await Task.Delay(ClaimInProgressDelay, ct).ConfigureAwait(false);

            return false;
        }

        using (gate)
        {
            if (Inspect(path).Kind is not PidFileClaimKind.Replaceable)
            {
                return false;
            }

            _logger.LogWarning("Removing stale PID file at {Path}.", path);

            if (!TryDelete(path, out string? reason))
            {
                throw new InvalidOperationException(
                    $"Could not remove the stale PID file at '{path}': {reason} Remove it by hand and start again.");
            }

            return TryClaim(path) is ClaimAttempt.Claimed;
        }
    }

    /// <summary>
    /// Takes the exclusive replacement gate beside the PID file, or returns <see langword="null"/> when another
    /// starter holds it. Any other failure to open it is reported with the remedy rather than ignored, because
    /// replacing a stale file without the gate would reopen the race it exists to close.
    /// </summary>
    private static FileStream? TryTakeReplacementGate(string path)
    {
        string gatePath = path + ReplacementGateSuffix;

        FileStreamOptions options = new()
        {
            Mode = FileMode.OpenOrCreate,

            Access = FileAccess.ReadWrite,

            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            return new FileStream(gatePath, options);
        }
        catch (IOException exception) when (RetainedExclusiveFileLock.IsVerifiedSharingViolation(exception))
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Could not take the PID file replacement lock at '{gatePath}': {exception.Message} "
                + $"Remove the stale PID file at '{path}' by hand and start again.",
                exception);
        }
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
        catch (UnauthorizedAccessException)
        {
            // Either a delete that is still pending (Windows) or a file this account may not read.
            return new PidFileClaim(PidFileClaimKind.AccessDenied, 0);
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

    /// <summary>
    /// Deletes <paramref name="path"/>. A failure that leaves the file in place is returned with its reason: the
    /// caller must not go on to claim a path it could not clear, and must not report that as contention.
    /// </summary>
    private static bool TryDelete(string path, [NotNullWhen(false)] out string? reason)
    {
        reason = null;

        try
        {
            File.Delete(path);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(path))
            {
                // Already removed by the starter that beat us to it.
                return true;
            }

            reason = exception.Message;

            return false;
        }
    }

    private enum ClaimAttempt
    {
        Claimed,

        Exists,

        Denied,
    }

    private enum PidFileClaimKind
    {
        Vanished,

        InProgress,

        AccessDenied,

        Live,

        Replaceable,
    }

    private readonly record struct PidFileClaim(PidFileClaimKind Kind, int Pid);
}
