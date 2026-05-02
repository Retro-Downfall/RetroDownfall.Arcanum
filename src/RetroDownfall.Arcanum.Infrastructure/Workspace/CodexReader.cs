using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Workspace;

internal static class CodexReader
{
    internal static async Task<string?> ReadCodexAsync(string? workingDirectory, CancellationToken ct)
    {
        string globalPath = Path.Combine(ArcanumPaths.GrimoireDirectory, "CODEX.md");

        string? globalContent = await TryReadAsync(globalPath, ct).ConfigureAwait(false);

        string? localContent = null;

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            string localPath = Path.Combine(workingDirectory, "CODEX.md");

            localContent = await TryReadAsync(localPath, ct).ConfigureAwait(false);
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
