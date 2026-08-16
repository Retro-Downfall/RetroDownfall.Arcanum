using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// The read-only source a selective import reads its Session graph and attachment bytes through.
/// </summary>
/// <remarks>
/// Nonserializable and async-disposable by design. The lease pins one read-only source SQLCipher
/// snapshot and the source attachment root for the whole transfer, including the destination commit,
/// so the graph the store validated and the graph it copied cannot be two different things.
///
/// <para>It is never persisted. A durable capability would let recovery resume reading a source the
/// process no longer holds, and a source that can be reopened after the fact is a source whose bytes
/// nobody re-proved (§10.12).</para>
/// </remarks>
internal sealed class ImportedSessionSourceLease : IAsyncDisposable
{

    private SqliteConnection? _source;

    private ImportedSessionSourceLease(SqliteConnection source, string attachmentsRoot)
    {

        _source = source;

        AttachmentsRoot = attachmentsRoot;

    }

    /// <summary>
    /// The source attachment root, resolved once and never re-resolved.
    /// </summary>
    internal string AttachmentsRoot { get; }

    /// <summary>
    /// Takes ownership of an already-open read-only source snapshot.
    /// </summary>
    /// <remarks>
    /// Ownership transfers here rather than being borrowed, so the lease's disposal is the one place
    /// the snapshot closes. Two owners would mean the store could still be reading a connection the
    /// importer had already released.
    /// </remarks>
    internal static ImportedSessionSourceLease Adopt(SqliteConnection source, string attachmentsRoot)
    {

        ArgumentNullException.ThrowIfNull(source);

        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentsRoot);

        return new ImportedSessionSourceLease(source, Path.GetFullPath(attachmentsRoot));

    }

    /// <summary>
    /// The pinned snapshot, or a disposed-object failure. Never null and never reopened.
    /// </summary>
    internal SqliteConnection Snapshot =>
        Volatile.Read(ref _source) ?? throw new ObjectDisposedException(nameof(ImportedSessionSourceLease));

    /// <summary>
    /// Resolves one source-relative attachment path inside the pinned root, or refuses.
    /// </summary>
    /// <remarks>
    /// Containment is checked against the resolved root rather than the supplied string, so a stored
    /// relative path carrying traversal cannot reach a file outside the archive the operator chose.
    /// </remarks>
    internal string ResolveContained(string relativePath)
    {

        string candidate = Path.GetFullPath(
            Path.Combine(AttachmentsRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        return candidate.StartsWith(AttachmentsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? candidate
            : throw new IOException("An imported attachment path escapes the archive attachment root.");

    }

    public async ValueTask DisposeAsync()
    {

        // Exchange rather than read-then-write: a second disposal that closed the connection again
        // would release a native handle the process may have already handed to something else.
        SqliteConnection? source = Interlocked.Exchange(ref _source, null);

        if (source is not null)
        {

            await source.DisposeAsync().ConfigureAwait(false);

        }

    }

}
