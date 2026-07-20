using RetroDownfall.TheForge.Ux.Models;

namespace RetroDownfall.TheForge.Ux.Services.Git;

/// <summary>
/// Maps a repository-relative path to a Workbench document when it looks like a Spell, Prompt, or CODEX.md.
/// </summary>
public static class GitArtifactPathMapper
{

    public static bool TryMap(
        string relativePath,
        string? repositoryPath,
        out DocumentKind kind,
        out string id,
        out string? workspace)
    {

        kind = default;

        id = string.Empty;

        workspace = string.IsNullOrWhiteSpace(repositoryPath)
            ? null
            : repositoryPath.Trim();

        if (string.IsNullOrWhiteSpace(relativePath))
        {

            return false;

        }

        string normalized = relativePath.Replace('\\', '/').Trim('/');

        if (normalized.EndsWith("CODEX.md", StringComparison.OrdinalIgnoreCase)
            && (normalized.Equals("CODEX.md", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/CODEX.md", StringComparison.OrdinalIgnoreCase)))
        {

            kind = DocumentKind.Codex;

            id = "global";

            return true;

        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length >= 3
            && segments[0].Equals("spells", StringComparison.OrdinalIgnoreCase)
            && (segments[^1].Equals("SPELL.md", StringComparison.OrdinalIgnoreCase)
                || segments[^1].Equals("SPELL.json", StringComparison.OrdinalIgnoreCase)))
        {

            kind = DocumentKind.Spell;

            id = segments[^2];

            return !string.IsNullOrWhiteSpace(id);

        }

        // Prompt files are not a first-class on-disk layout; accept prompts/{guid}(.json|.md)? when present.
        if (segments.Length >= 2
            && segments[0].Equals("prompts", StringComparison.OrdinalIgnoreCase))
        {

            string leaf = segments[^1];

            string stem = Path.GetFileNameWithoutExtension(leaf);

            if (Guid.TryParse(stem, out Guid promptId) || Guid.TryParse(leaf, out promptId))
            {

                kind = DocumentKind.Prompt;

                id = promptId.ToString("D");

                workspace = null;

                return true;

            }

        }

        return false;

    }

}
