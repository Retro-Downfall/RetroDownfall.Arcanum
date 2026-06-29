using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

internal static class WorkspacePathResolver
{

    internal static Result<string> ResolveRelativePath(WorkspaceInfo workspace, string? relativePath)
    {

        string workspaceRoot;

        try
        {
            workspaceRoot = Path.GetFullPath(workspace.Path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return new Error("Workspace.PathTraversal", "The relative path escapes the workspace root.");
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return workspaceRoot;
        }

        string trimmed = relativePath.Trim();

        if (Path.IsPathRooted(trimmed))
        {
            return new Error("Workspace.PathTraversal", "The relative path escapes the workspace root.");
        }

        if (ContainsParentDirectorySegment(trimmed))
        {
            return new Error("Workspace.PathTraversal", "The relative path escapes the workspace root.");
        }

        string resolved;

        try
        {
            resolved = Path.GetFullPath(Path.Combine(workspaceRoot, trimmed));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return new Error("Workspace.PathTraversal", "The relative path escapes the workspace root.");
        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspace(workspaceRoot, resolved))
        {
            return new Error("Workspace.PathTraversal", "The relative path escapes the workspace root.");
        }

        resolved = NormalizeResolvedPath(workspaceRoot, resolved);

        if (File.Exists(resolved) || Directory.Exists(resolved))
        {

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, resolved, out _))
            {
                return new Error("Workspace.SymbolicLinkEscape", "The path resolves outside the workspace via a symbolic link.");
            }
        }

        return resolved;
    }

    private static bool ContainsParentDirectorySegment(string relativePath)
    {

        foreach (string segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {

            if (string.Equals(segment, "..", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeResolvedPath(string workspaceRoot, string resolved)
    {

        if (WorkspaceRootPolicy.IsSamePath(workspaceRoot, resolved))
        {
            return workspaceRoot;
        }

        return resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

}
