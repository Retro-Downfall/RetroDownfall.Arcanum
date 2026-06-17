using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Containment and size checks for prompt-test codex file reads.
/// </summary>
public static class CodexPathPolicy
{

    public static Result<string> ValidateContainedFile(
        string codexPath,
        string containmentRootFullPath,
        long maxFileReadSizeBytes)
    {
        if (string.IsNullOrWhiteSpace(codexPath))
        {
            return Result<string>.Failure(new Error("Prompt.CodexPathRequired", "Codex path is required."));
        }

        if (string.IsNullOrWhiteSpace(containmentRootFullPath))
        {
            return Result<string>.Failure(
                new Error(
                    "Prompt.CodexPathNotContained",
                    "A workspace or campaign root is required to read a codex file."));
        }

        string normalizedCodex;

        try
        {
            normalizedCodex = Path.GetFullPath(codexPath.Trim());
        }
        catch (Exception)
        {
            return Result<string>.Failure(new Error("Prompt.CodexPathInvalid", "The codex path is invalid."));
        }

        string normalizedRoot;

        try
        {
            normalizedRoot = Path.GetFullPath(containmentRootFullPath.Trim());
        }
        catch (Exception)
        {
            return Result<string>.Failure(
                new Error(
                    "Prompt.CodexPathNotContained",
                    "A workspace or campaign root is required to read a codex file."));
        }

        if (!WorkspaceRootPolicy.IsUnderAnyAllowedRoot(normalizedCodex, [normalizedRoot]))
        {
            return Result<string>.Failure(
                new Error(
                    "Prompt.CodexPathNotContained",
                    "The codex path must be under the prompt campaign or working directory."));
        }

        if (!File.Exists(normalizedCodex))
        {
            return Result<string>.Failure(new Error("Prompt.CodexPathNotFound", "The codex file was not found."));
        }

        try
        {
            FileInfo info = new(normalizedCodex);

            if (info.Length > maxFileReadSizeBytes)
            {
                return Result<string>.Failure(
                    new Error("Prompt.CodexPathTooLarge", "The codex file exceeds the maximum read size limit."));
            }
        }
        catch (Exception)
        {
            return Result<string>.Failure(new Error("Prompt.CodexPathNotFound", "The codex file was not found."));
        }

        return Result<string>.Success(normalizedCodex);
    }

}
