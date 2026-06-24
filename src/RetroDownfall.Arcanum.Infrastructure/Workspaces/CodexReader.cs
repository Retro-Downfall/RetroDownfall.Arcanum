using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

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
            && ToolHelpers.TryNormalizeWorkspace(workingDirectory, out string? workspaceRoot, out _)
            && Directory.Exists(workspaceRoot))
        {
            try
            {
                string localCodexFull = Path.GetFullPath(Path.Combine(workspaceRoot, "CODEX.md"));

                if (ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, localCodexFull, out _))
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

    private static async Task<string?> TryReadAsync(string path, long maxSizeBytes, CancellationToken ct)
    {
        try
        {
            FileInfo info = new(path);

            if (!info.Exists || info.Length > maxSizeBytes)
            {
                return null;
            }

            return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

}
