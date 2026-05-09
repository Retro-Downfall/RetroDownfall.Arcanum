using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Workspace;

internal static class CodexReader
{
    internal static async Task<string?> ReadCodexAsync(string? workingDirectory, CancellationToken ct)
    {
        string globalPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "CODEX.md");

        string? globalContent = await TryReadAsync(globalPath, ct).ConfigureAwait(false);

        string? localContent = null;

        if (!string.IsNullOrWhiteSpace(workingDirectory)
            && ToolHelpers.TryNormalizeWorkspace(workingDirectory, out string? workspaceRoot, out _)
            && Directory.Exists(workspaceRoot))
        {
            try
            {
                string localCodexFull = Path.GetFullPath(Path.Combine(workspaceRoot, "CODEX.md"));

                if (ToolHelpers.IsPathUnderWorkspace(workspaceRoot, localCodexFull))
                {
                    localContent = await TryReadAsync(localCodexFull, ct).ConfigureAwait(false);
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

    private static async Task<string?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
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
