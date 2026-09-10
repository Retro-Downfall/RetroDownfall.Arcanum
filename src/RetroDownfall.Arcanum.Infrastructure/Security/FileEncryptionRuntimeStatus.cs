using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class FileEncryptionRuntimeStatus : IFileEncryptionRuntimeStatus
{
    private static readonly FileEncryptionRuntimeSnapshot Pending = new(
        FileEncryptionRuntimeState.Pending,
        "File-encryption startup validation is pending.");

    private static readonly FileEncryptionRuntimeSnapshot Deferred = new(
        FileEncryptionRuntimeState.Deferred,
        "No encrypted blob currently requires the file-encryption key.");

    private static readonly FileEncryptionRuntimeSnapshot Ready = new(
        FileEncryptionRuntimeState.Ready,
        "The file-encryption key is ready.");

    private static readonly FileEncryptionRuntimeSnapshot Unavailable = new(
        FileEncryptionRuntimeState.Unavailable,
        "The file-encryption key is unavailable or its blob inventory is uncertain.");

    private FileEncryptionRuntimeSnapshot _current = Pending;

    public FileEncryptionRuntimeSnapshot Current => Volatile.Read(ref _current);

    internal void PublishDeferred()
    {
        FileEncryptionRuntimeSnapshot observed;

        do
        {
            observed = Volatile.Read(ref _current);
            if (observed.State is FileEncryptionRuntimeState.Ready)
            {
                return;
            }
        }

        while (!ReferenceEquals(
            Interlocked.CompareExchange(ref _current, Deferred, observed),
            observed));
    }

    internal void PublishReady() => Volatile.Write(ref _current, Ready);

    internal void PublishUnavailable() => Volatile.Write(ref _current, Unavailable);
}
