using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

[ExcludeFromCodeCoverage] // Reason: spell path validation wrapper; SpellWorkspaceResolver and repository tests cover policy outcomes.
internal static partial class SpellPathPolicy
{

    internal static Result<string> ValidateOverrideSpellPath(
        string overrideSpellPath,
        string? spellWorkspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(overrideSpellPath))
        {
            return Result<string>.Failure(new Error("Spell.InvalidPath", "Override spell path is required."));
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(overrideSpellPath.Trim());
        }
        catch (Exception)
        {
            return Result<string>.Failure(new Error("Spell.InvalidPath", "The override spell path is invalid."));
        }

        string fileName = Path.GetFileName(fullPath);

        if (!SpellFileNameRegex().IsMatch(fileName))
        {
            return Result<string>.Failure(
                new Error("Spell.PathNotAllowed", "Override spell path must reference SPELL.md or SPELL.v{N}.md."));
        }

        if (!IsUnderKnownSpellRoot(fullPath, spellWorkspaceRoot))
        {
            return Result<string>.Failure(
                new Error("Spell.PathNotAllowed", "Override spell path is outside allowed spell directories."));
        }

        if (!File.Exists(fullPath))
        {
            return Result<string>.Failure(new Error("Spell.NotFound", "The specified spell file does not exist."));
        }

        return Result<string>.Success(fullPath);
    }

    private static bool IsUnderKnownSpellRoot(string fullPath, string? spellWorkspaceRoot)
    {
        string globalRoot;

        try
        {
            globalRoot = Path.GetFullPath(ArcanumPaths.GlobalSpellsDirectory);
        }
        catch (Exception)
        {
            return false;
        }

        if (ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(globalRoot, fullPath, out _))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(spellWorkspaceRoot)
            && ToolHelpers.TryNormalizeWorkspace(spellWorkspaceRoot, out string? normalizedRoot, out _)
            && ToolHelpers.IsPathUnderWorkspaceWithSymlinkCheck(normalizedRoot, fullPath, out _))
        {
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^SPELL(\.v\d+)?\.md$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SpellFileNameRegex();

}
