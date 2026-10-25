using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;
using Serilog;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

internal static class CodexReader
{
    /// <summary>Most codex bodies kept at once; the least recently used is evicted beyond this.</summary>
    internal const int CacheCapacity = 64;

    private static readonly CodexReadCache Cache = new(CacheCapacity);

    internal static int CachedEntryCountForTests => Cache.Count;

    internal static async Task<string?> ReadCodexAsync(string? workingDirectory, long maxSizeBytes, CancellationToken ct)
    {
        string globalPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "CODEX.md");

        string? globalContent = await ReadPromptCodexAsync(globalPath, maxSizeBytes, ct).ConfigureAwait(false);

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
                    localContent = await ReadPromptCodexAsync(localCodexFull, maxSizeBytes, ct).ConfigureAwait(false);
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

    internal static async Task<(SecureFileReadStatus Status, string? Text)> ReadCodexFileAsync(string path, long maxSizeBytes, CancellationToken ct) =>
        await TryReadCachedAsync(path, maxSizeBytes, ct).ConfigureAwait(false);

    private static async Task<string?> ReadPromptCodexAsync(string path, long maxSizeBytes, CancellationToken ct)
    {
        (SecureFileReadStatus Status, string? Text) result = await ReadCodexFileAsync(path, maxSizeBytes, ct).ConfigureAwait(false);

        if (result.Status is not SecureFileReadStatus.Success and not SecureFileReadStatus.NotFound)
        {
            Log.Information("CODEX content omitted from prompt assembly because the secure read failed: {Status}", result.Status);
        }

        return result.Text;
    }

    private static async Task<(SecureFileReadStatus Status, string? Text)> TryReadCachedAsync(string path, long maxSizeBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        CodexCacheKey key = new(path, maxSizeBytes);

        CodexFileStamp? stamp = TryCaptureStamp(path);

        if (stamp is { } existingStamp && Cache.TryGet(key, existingStamp) is { } cached)
        {
            return (SecureFileReadStatus.Success, cached);
        }

        SecureUtf8FileReadResult result = await TryReadAsync(path, maxSizeBytes, ct).ConfigureAwait(false);

        if (stamp is { } capturedStamp && result.Status is SecureFileReadStatus.Success && result.Text is { } content)
        {
            Cache.Store(key, capturedStamp, content);
        }

        return (result.Status, result.Text);
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

    /// <summary>
    /// Reads a codex through the shared fail-closed primitive (DESIGN §11.6). A plain
    /// <c>File.ReadAllTextAsync</c> here followed symlinks — a git-tracked <c>CODEX.md -&gt; ~/.ssh/id_ed25519</c>
    /// in a cloned campaign was returned verbatim — and blocked forever on a FIFO, whose
    /// <c>FileInfo.Length</c> of 0 sailed through the size gate while <c>open(2)</c> waited for a writer that
    /// no cancellation token could interrupt. <c>SecureFileReader</c> opens once with link following disabled,
    /// proves the object is an unaliased regular file, and bounds the read.
    /// </summary>
    private static async Task<SecureUtf8FileReadResult> TryReadAsync(string path, long maxSizeBytes, CancellationToken ct)
    {
        if (maxSizeBytes <= 0)
        {
            return new SecureUtf8FileReadResult(SecureFileReadStatus.TooLarge, null, 0, default);
        }

        int maxBytes = (int)Math.Min(maxSizeBytes, int.MaxValue - 1);

        return await SecureFileReader.ReadUtf8TextAsync(path, maxBytes, ct).ConfigureAwait(false);
    }
}
