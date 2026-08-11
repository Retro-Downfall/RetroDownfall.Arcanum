using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

internal static class CodexReader
{

    private sealed record CodexCacheEntry(long MtimeUtcTicks, string Content);

    private static readonly ConcurrentDictionary<string, CodexCacheEntry> Cache = new(StringComparer.Ordinal);

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
        long mtimeTicks;

        try
        {
            mtimeTicks = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (Cache.TryGetValue(path, out CodexCacheEntry? entry) && entry.MtimeUtcTicks == mtimeTicks)
        {
            return entry.Content;
        }

        string? content = await TryReadAsync(path, maxSizeBytes, ct).ConfigureAwait(false);

        if (content is not null)
        {
            Cache[path] = new CodexCacheEntry(mtimeTicks, content);
        }

        return content;
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
