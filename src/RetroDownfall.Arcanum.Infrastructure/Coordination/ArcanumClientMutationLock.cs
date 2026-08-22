namespace RetroDownfall.Arcanum.Infrastructure.Coordination;

internal enum ArcanumClientMutationLockAcquisitionDisposition : byte
{

    Unsafe,

    Contended,

    Acquired,

}

internal readonly record struct ArcanumClientMutationLockAcquisitionResult
{

    private ArcanumClientMutationLockAcquisitionResult(
        ArcanumClientMutationLockAcquisitionDisposition disposition,
        ArcanumClientMutationLock? mutationLock)
    {

        Disposition = disposition;

        Lock = mutationLock;

    }

    internal ArcanumClientMutationLockAcquisitionDisposition Disposition { get; }

    internal ArcanumClientMutationLock? Lock { get; }

    internal static ArcanumClientMutationLockAcquisitionResult Acquired(
        ArcanumClientMutationLock mutationLock) =>
        new(ArcanumClientMutationLockAcquisitionDisposition.Acquired, mutationLock);

    internal static ArcanumClientMutationLockAcquisitionResult Contended() =>
        new(ArcanumClientMutationLockAcquisitionDisposition.Contended, mutationLock: null);

    internal static ArcanumClientMutationLockAcquisitionResult Unsafe() =>
        new(ArcanumClientMutationLockAcquisitionDisposition.Unsafe, mutationLock: null);

    internal ArcanumClientMutationLock BorrowAcquiredLock() =>
        Disposition is ArcanumClientMutationLockAcquisitionDisposition.Acquired
        && Lock is { } acquired
            ? acquired
            : throw new InvalidOperationException(
                "This client-mutation lock outcome does not carry an acquired handle.");

}

internal sealed class ArcanumClientMutationLock : IDisposable
{

    private RetainedExclusiveFileLock? _lock;

    private ArcanumClientMutationLock(RetainedExclusiveFileLock mutationLock)
    {

        _lock = mutationLock;

    }

    internal string Path => _lock?.Path
        ?? throw new ObjectDisposedException(nameof(ArcanumClientMutationLock));

    internal static string LockPathFor(string guardedDirectory)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(guardedDirectory);

        string full = System.IO.Path.TrimEndingDirectorySeparator(
            System.IO.Path.GetFullPath(guardedDirectory));

        string? parent = System.IO.Path.GetDirectoryName(full);

        string name = System.IO.Path.GetFileName(full);

        return string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)
            ? System.IO.Path.Combine(full, ".arcanum-client-mutation.lock")
            : System.IO.Path.Combine(
                parent,
                $".arcanum-client-mutation-{name}.lock");

    }

    internal static ArcanumClientMutationLockAcquisitionResult AcquireDetailed(
        string guardedDirectory)
    {

        RetainedExclusiveFileLockAcquisitionResult acquired =
            RetainedExclusiveFileLock.Acquire(LockPathFor(guardedDirectory));

        return acquired.Disposition switch
        {
            RetainedExclusiveFileLockAcquisitionDisposition.Acquired
                when acquired.Lock is { } held =>
                ArcanumClientMutationLockAcquisitionResult.Acquired(
                    new ArcanumClientMutationLock(held)),
            RetainedExclusiveFileLockAcquisitionDisposition.Contended =>
                ArcanumClientMutationLockAcquisitionResult.Contended(),
            _ => ArcanumClientMutationLockAcquisitionResult.Unsafe(),
        };

    }

    internal void AssertHeldFor(string guardedDirectory)
    {

        RetainedExclusiveFileLock? held = _lock;

        ObjectDisposedException.ThrowIf(held is null, this);

        held.AssertHeldAt(LockPathFor(guardedDirectory), this);

    }

    public void Dispose()
    {

        RetainedExclusiveFileLock? held = _lock;

        _lock = null;

        held?.Dispose();

    }

}
