using System.Diagnostics;

using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Configuration;

/// <summary>
/// Raised when the configuration mutex could not be taken inside the bounded acquisition window,
/// so the caller fails fast instead of parking a thread on another process's write.
/// </summary>
public sealed class ArcanumConfigurationLockException(TimeSpan timeout)
    : TimeoutException(
        $"The Arcanum configuration transaction was not acquired within {timeout.TotalSeconds:0.###} seconds. Another Arcanum process or window is writing the configuration.")
{

    public TimeSpan Timeout { get; } = timeout;

}

/// <summary>
/// Coordinates every in-process and cross-process mutation of the canonical Arcanum
/// configuration. The named mutex is current-user scoped, spans desktop/CLI sessions, and is
/// released by the operating system if a process terminates.
/// </summary>
public static class ArcanumConfigurationTransaction
{

    /// <summary>
    /// Acquisition is always bounded, including for callers whose token cannot be cancelled, so a
    /// configuration write held elsewhere surfaces as a failure instead of an indefinite block.
    /// </summary>
    public static readonly TimeSpan DefaultAcquisitionTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan AcquisitionPollInterval = TimeSpan.FromMilliseconds(50);

    private static readonly AsyncLocal<int> NestingDepth = new();

    public static Task<T> RunAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default,
        TimeSpan? acquisitionTimeout = null)
    {

        ArgumentNullException.ThrowIfNull(operation);

        if (NestingDepth.Value > 0)
        {

            return operation();

        }

        TimeSpan timeout = acquisitionTimeout ?? DefaultAcquisitionTimeout;

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        return Task.Run(
            () => RunOwned(operation, timeout, cancellationToken),
            CancellationToken.None);

    }

    private static T RunOwned<T>(
        Func<Task<T>> operation,
        TimeSpan acquisitionTimeout,
        CancellationToken cancellationToken)
    {

        NamedWaitHandleOptions options = new()
        {

            CurrentUserOnly = true,

            CurrentSessionOnly = false,

        };

        using Mutex mutex = new(MutexName(), options);

        bool acquired = false;

        try
        {

            acquired = WaitForOwnership(mutex, acquisitionTimeout, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            NestingDepth.Value++;

            return operation().GetAwaiter().GetResult();

        }
        finally
        {

            if (NestingDepth.Value > 0)
            {

                NestingDepth.Value--;

            }

            if (acquired)
            {

                mutex.ReleaseMutex();

            }

        }

    }

    private static bool WaitForOwnership(
        Mutex mutex,
        TimeSpan acquisitionTimeout,
        CancellationToken cancellationToken)
    {

        try
        {

            if (!cancellationToken.CanBeCanceled)
            {

                if (mutex.WaitOne(acquisitionTimeout))
                {

                    return true;

                }

                throw new ArcanumConfigurationLockException(acquisitionTimeout);

            }

            long start = Stopwatch.GetTimestamp();

            while (true)
            {

                TimeSpan remaining = acquisitionTimeout - Stopwatch.GetElapsedTime(start);

                if (remaining <= TimeSpan.Zero)
                {

                    throw new ArcanumConfigurationLockException(acquisitionTimeout);

                }

                if (mutex.WaitOne(
                        remaining < AcquisitionPollInterval
                            ? remaining
                            : AcquisitionPollInterval))
                {

                    return true;

                }

                cancellationToken.ThrowIfCancellationRequested();

            }

        }
        catch (AbandonedMutexException)
        {

            return true;

        }

    }

    private static string MutexName()
    {

        byte[] pathHash = SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(ArcanumPaths.ConfigurationFile)));

        return $"RetroDownfall.Arcanum.Config.{Convert.ToHexStringLower(pathHash.AsSpan(0, 16))}";

    }

}
