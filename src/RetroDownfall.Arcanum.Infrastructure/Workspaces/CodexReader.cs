using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

internal static class CodexReader
{
    /// <summary>Most codex bodies kept at once; the least recently used is evicted beyond this.</summary>
    internal const int CacheCapacity = 64;

    /// <summary>
    /// A cached body is only valid for the exact read that produced it: the same path <b>and</b> the same
    /// size limit, so lowering the limit can never be answered from a body that the lower limit rejects.
    /// </summary>
    private readonly record struct CodexCacheKey(string Path, long MaxSizeBytes);

    /// <summary>
    /// What the file looked like when it was read. Identity and length join the modification time, so a
    /// replacement that restores the old mtime (or a different object swapped in under the same name)
    /// still misses.
    /// </summary>
    private readonly record struct CodexFileStamp(FileHandleIdentity Identity, long Length, long MtimeUtcTicks);

    private sealed record CodexCacheEntry(CodexFileStamp Stamp, string Content);

    private static readonly Lock CacheGate = new();

    private static readonly Dictionary<CodexCacheKey, LinkedListNode<(CodexCacheKey Key, CodexCacheEntry Entry)>> CacheIndex = [];

    // Most recently used first.
    private static readonly LinkedList<(CodexCacheKey Key, CodexCacheEntry Entry)> CacheOrder = new();

    internal static int CachedEntryCountForTests
    {
        get
        {
            lock (CacheGate)
            {
                return CacheIndex.Count;
            }
        }
    }

    internal static async Task<string?> ReadCodexAsync(string? workingDirectory, long maxSizeBytes, CancellationToken ct)
    {
        string globalPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "CODEX.md");

        string? globalContent = await TryReadCachedAsync(globalPath, maxSizeBytes, ct).ConfigureAwait(false);

        string? localContent = null;

        if (!string.IsNullOrWhiteSpace(workingDirectory)
            && WorkspacePathPolicy.TryNormalizeWorkspace(workingDirectory, out string? workspaceRoot, out _)
            && Directory.Exists(workspaceRoot))
        {
            try
            {
                string localCodexFull = Path.GetFullPath(Path.Combine(workspaceRoot, "CODEX.md"));

                if (WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, localCodexFull, out _))
                {
                    localContent = await TryReadCachedAsync(localCodexFull, maxSizeBytes, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                localContent = null;
            }
        }

        if (globalContent is not null && localContent is not null)
        {
            return $"{globalContent}\n\n### Local Workspace Spells\n\n{localContent}";
        }

        return globalContent ?? localContent;
    }

    internal static async Task<string?> ReadCodexFileAsync(string path, long maxSizeBytes, CancellationToken ct) =>
        await TryReadCachedAsync(path, maxSizeBytes, ct).ConfigureAwait(false);

    private static async Task<string?> TryReadCachedAsync(string path, long maxSizeBytes, CancellationToken ct)
    {
        CodexCacheKey key = new(path, maxSizeBytes);

        CodexFileStamp? stamp = TryCaptureStamp(path);

        if (stamp is null)
        {
            return null;
        }

        if (TryGetCached(key, stamp.Value) is { } cached)
        {
            return cached;
        }

        string? content = await TryReadAsync(path, maxSizeBytes, ct).ConfigureAwait(false);

        if (content is not null)
        {
            Store(key, new CodexCacheEntry(stamp.Value, content));
        }

        return content;
    }

    private static CodexFileStamp? TryCaptureStamp(string path)
    {
        try
        {
            FileInfo info = new(path);

            if (!info.Exists
                || !FileHandleIdentityInterop.TryGetPathIdentity(path, out FileHandleIdentity identity))
            {
                return null;
            }

            return new CodexFileStamp(identity, info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryGetCached(CodexCacheKey key, CodexFileStamp stamp)
    {
        lock (CacheGate)
        {
            if (!CacheIndex.TryGetValue(key, out LinkedListNode<(CodexCacheKey Key, CodexCacheEntry Entry)>? node))
            {
                return null;
            }

            if (node.Value.Entry.Stamp != stamp)
            {
                CacheOrder.Remove(node);

                CacheIndex.Remove(key);

                return null;
            }

            CacheOrder.Remove(node);

            CacheOrder.AddFirst(node);

            return node.Value.Entry.Content;
        }
    }

    private static void Store(CodexCacheKey key, CodexCacheEntry entry)
    {
        lock (CacheGate)
        {
            if (CacheIndex.Remove(key, out LinkedListNode<(CodexCacheKey Key, CodexCacheEntry Entry)>? existing))
            {
                CacheOrder.Remove(existing);
            }

            CacheIndex[key] = CacheOrder.AddFirst((key, entry));

            while (CacheIndex.Count > CacheCapacity && CacheOrder.Last is { } oldest)
            {
                CacheOrder.RemoveLast();

                CacheIndex.Remove(oldest.Value.Key);
            }
        }
    }

    /// <summary>
    /// Reads a codex through the shared fail-closed primitive (DESIGN §11.6). A plain
    /// <c>File.ReadAllTextAsync</c> here followed symlinks — a git-tracked <c>CODEX.md -&gt; ~/.ssh/id_ed25519</c>
    /// in a cloned campaign was returned verbatim — and blocked forever on a FIFO, whose
    /// <c>FileInfo.Length</c> of 0 sailed through the size gate while <c>open(2)</c> waited for a writer that
    /// no cancellation token could interrupt. <c>SecureFileReader</c> opens once with link following disabled,
    /// proves the object is an unaliased regular file, and bounds the read.
    /// </summary>
    private static async Task<string?> TryReadAsync(string path, long maxSizeBytes, CancellationToken ct)
    {
        if (maxSizeBytes <= 0)
        {
            return null;
        }

        int maxBytes = (int)Math.Min(maxSizeBytes, int.MaxValue - 1);

        SecureUtf8FileReadResult readResult = await SecureFileReader
            .ReadUtf8TextAsync(path, maxBytes, ct)
            .ConfigureAwait(false);

        if (readResult.Status is not SecureFileReadStatus.Success)
        {
            return null;
        }

        return readResult.Text;
    }
}
