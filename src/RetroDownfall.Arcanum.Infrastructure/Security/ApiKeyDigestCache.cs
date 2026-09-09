using System.Security.Cryptography;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class ApiKeyDigestCache : IApiKeyDigestCache
{
    private enum DigestCopyResult
    {
        Success,
        Expired,
        Retired
    }

    // The digest and expiry remain one atomically published snapshot. A reader owns a
    // short-lived reference while it checks the clock and copies the digest. Retirement
    // prevents new readers from acquiring that snapshot and erases it after the last
    // already-admitted reader releases it.
    private sealed class DigestEntry(
        byte[] digest,
        long storedAtTimestamp,
        TimeSpan authenticationTtl)
    {
        private readonly byte[] _digest = digest;

        private int _activeReaders;

        private int _retired;

        // Only competing retirement paths take this per-entry lock. Ordinary reads and
        // ordinary releases stay entirely on the interlocked fast path.
        private readonly Lock _zeroizationLock = new();

        private bool _zeroed;

        public DigestCopyResult TryCopyForAuthentication(
            TimeProvider timeProvider,
            out byte[]? copy)
        {
            copy = null;

            if (!TryAcquireReader())
            {
                return DigestCopyResult.Retired;
            }

            try
            {
                TimeSpan elapsed = timeProvider.GetElapsedTime(
                    storedAtTimestamp,
                    timeProvider.GetTimestamp());

                if (elapsed >= authenticationTtl)
                {
                    return DigestCopyResult.Expired;
                }

                copy = _digest.ToArray();

                return DigestCopyResult.Success;
            }
            finally
            {
                ReleaseReader();
            }
        }

        public DigestCopyResult TryCopyForPresence(out byte[]? copy)
        {
            copy = null;

            if (!TryAcquireReader())
            {
                return DigestCopyResult.Retired;
            }

            try
            {
                copy = _digest.ToArray();

                return DigestCopyResult.Success;
            }
            finally
            {
                ReleaseReader();
            }
        }

        public void Retire()
        {
            if (Interlocked.Exchange(ref _retired, 1) == 0)
            {
                ZeroIfRetiredAndUnused();
            }
        }

        private bool TryAcquireReader()
        {
            if (Volatile.Read(ref _retired) != 0)
            {
                return false;
            }

            Interlocked.Increment(ref _activeReaders);

            if (Volatile.Read(ref _retired) == 0)
            {
                return true;
            }

            ReleaseReader();

            return false;
        }

        private void ReleaseReader()
        {
            int remainingReaders = Interlocked.Decrement(ref _activeReaders);

            if (remainingReaders == 0 && Volatile.Read(ref _retired) != 0)
            {
                ZeroIfRetiredAndUnused();
            }
        }

        private void ZeroIfRetiredAndUnused()
        {
            if (Volatile.Read(ref _retired) == 0 ||
                Volatile.Read(ref _activeReaders) != 0)
            {
                return;
            }

            lock (_zeroizationLock)
            {
                if (_zeroed || Volatile.Read(ref _activeReaders) != 0)
                {
                    return;
                }

                CryptographicOperations.ZeroMemory(_digest);

                _zeroed = true;
            }
        }
    }

    private DigestEntry? _entry;

    // Even generations identify stable cache states. Mutations briefly publish an odd generation
    // while swapping the entry, which lets queue-free readers reject a torn (generation, entry)
    // observation without taking the mutation lock.
    private long _generation;

    private readonly Lock _mutationLock = new();

    private readonly TimeProvider _timeProvider;

    public ApiKeyDigestCache(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool TryGetDigest(out byte[]? digest)
    {
        return TryGetDigest(out digest, out _);
    }

    public bool TryGetDigest(out byte[]? digest, out long generation)
    {
        SpinWait spinWait = default;

        while (true)
        {
            long generationBefore = Volatile.Read(ref _generation);

            if ((generationBefore & 1L) != 0)
            {
                spinWait.SpinOnce();

                continue;
            }

            DigestEntry? entry = Volatile.Read(ref _entry);

            if (generationBefore != Volatile.Read(ref _generation))
            {
                spinWait.SpinOnce();

                continue;
            }

            generation = generationBefore;

            if (entry is null)
            {
                digest = null;

                return false;
            }

            switch (entry.TryCopyForAuthentication(_timeProvider, out digest))
            {
                case DigestCopyResult.Success:
                    return true;

                case DigestCopyResult.Expired:
                    return false;

                case DigestCopyResult.Retired:
                    spinWait.SpinOnce();

                    continue;

                default:
                    throw new InvalidOperationException("Unknown digest copy result.");
            }
        }
    }

    public bool TryGetPresenceDigest(out byte[]? digest)
    {
        SpinWait spinWait = default;

        while (true)
        {
            long generationBefore = Volatile.Read(ref _generation);

            if ((generationBefore & 1L) != 0)
            {
                spinWait.SpinOnce();

                continue;
            }

            DigestEntry? entry = Volatile.Read(ref _entry);

            if (generationBefore != Volatile.Read(ref _generation))
            {
                spinWait.SpinOnce();

                continue;
            }

            if (entry is null)
            {
                digest = null;

                return false;
            }

            if (entry.TryCopyForPresence(out digest) == DigestCopyResult.Success)
            {
                return true;
            }

            spinWait.SpinOnce();
        }
    }

    public void StoreDigest(byte[] digest, int ttlSeconds)
    {
        ArgumentNullException.ThrowIfNull(digest);

        DigestEntry snapshot = CreateEntry(digest, ttlSeconds);

        Publish(snapshot);
    }

    public bool TryStoreDigest(
        byte[] digest,
        int ttlSeconds,
        long expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(digest);

        if (Volatile.Read(ref _generation) != expectedGeneration)
        {
            return false;
        }

        DigestEntry snapshot = CreateEntry(digest, ttlSeconds);

        lock (_mutationLock)
        {
            long currentGeneration = Volatile.Read(ref _generation);

            if (currentGeneration != expectedGeneration ||
                (currentGeneration & 1L) != 0)
            {
                snapshot.Retire();

                return false;
            }

            PublishLocked(snapshot, currentGeneration);

            return true;
        }
    }

    public void Invalidate()
    {
        lock (_mutationLock)
        {
            long currentGeneration = Volatile.Read(ref _generation);

            PublishLocked(snapshot: null, currentGeneration);
        }
    }

    private DigestEntry CreateEntry(byte[] digest, int ttlSeconds)
    {
        long storedAtTimestamp = _timeProvider.GetTimestamp();

        TimeSpan authenticationTtl = TimeSpan.FromSeconds(ttlSeconds);

        // The cache owns its copy, so caller cleanup cannot mutate the live snapshot.
        return new DigestEntry(
            digest.ToArray(),
            storedAtTimestamp,
            authenticationTtl);
    }

    private void Publish(DigestEntry snapshot)
    {
        lock (_mutationLock)
        {
            long currentGeneration = Volatile.Read(ref _generation);

            PublishLocked(snapshot, currentGeneration);
        }
    }

    private void PublishLocked(
        DigestEntry? snapshot,
        long currentGeneration)
    {
        long mutationGeneration = unchecked(currentGeneration + 1L);

        long publishedGeneration = unchecked(currentGeneration + 2L);

        Volatile.Write(ref _generation, mutationGeneration);

        DigestEntry? retired = Interlocked.Exchange(ref _entry, snapshot);

        retired?.Retire();

        Volatile.Write(ref _generation, publishedGeneration);
    }
}
