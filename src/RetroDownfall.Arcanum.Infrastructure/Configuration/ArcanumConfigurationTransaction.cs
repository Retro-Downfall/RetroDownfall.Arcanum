using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Configuration;

/// <summary>
/// Coordinates every in-process and cross-process mutation of the canonical Arcanum
/// configuration. The named mutex is current-user scoped, spans desktop/CLI sessions, and is
/// released by the operating system if a process terminates.
/// </summary>
public static class ArcanumConfigurationTransaction
{

    private static readonly AsyncLocal<int> NestingDepth = new();

    public static Task<T> RunAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(operation);

        if (NestingDepth.Value > 0)
        {

            return operation();

        }

        return Task.Run(
            () => RunOwned(operation, cancellationToken),
            CancellationToken.None);

    }

    private static T RunOwned<T>(
        Func<Task<T>> operation,
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

            acquired = WaitForOwnership(mutex, cancellationToken);

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
        CancellationToken cancellationToken)
    {

        try
        {

            if (!cancellationToken.CanBeCanceled)
            {

                return mutex.WaitOne();

            }

            while (!mutex.WaitOne(TimeSpan.FromMilliseconds(50)))
            {

                cancellationToken.ThrowIfCancellationRequested();

            }

            return true;

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
