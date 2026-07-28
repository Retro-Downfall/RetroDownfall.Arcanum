using System.Text;
using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// UI-agnostic turn attachment builder: inline <c>@path</c> tokens + pre-staged <c>/attach</c> paths.
/// Text files → <see cref="AttachedFileDto"/>; images → <see cref="ScryingFocusDto"/> (ephemeral).
/// </summary>
internal static class CommandCenterTurnAttachmentBuilder
{
    private static readonly Regex AtTokenRegex = new(
        @"(?<=^|\s)@([^\s]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static TurnAttachmentBuildResult Build(
        string prompt,
        string workingDirectory,
        IReadOnlyCollection<string> preStagedPaths,
        ArcanumSettings settings)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(preStagedPaths);
        ArgumentNullException.ThrowIfNull(settings);

        long maxAttach = ArcanumSettingClamps.MaxAttachFileSizeBytes(
            ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);
        ScryingSettings scrying = settings.ResolveScrying();
        long maxImage = ArcanumSettingClamps.ScryingMaxImageBytes(scrying.MaxImageBytes);
        string[] allowedMime = scrying.AllowedMimeTypes is { Length: > 0 }
            ? scrying.AllowedMimeTypes
            : ["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"];

        HashSet<string> stagedText = new(StringComparer.Ordinal);
        HashSet<string> stagedImages = new(StringComparer.Ordinal);
        List<string> status = [];

        string workingPrompt = prompt;
        MatchCollection atMatches = AtTokenRegex.Matches(workingPrompt);

        for (int mi = atMatches.Count - 1; mi >= 0; mi--)
        {
            Match match = atMatches[mi];
            if (!match.Success)
            {
                continue;
            }

            string tokenPath = match.Groups[1].Value;
            if (!TryResolvePath(workingDirectory, tokenPath, out string fullPath, out string? resolveError))
            {
                status.Add($"@{tokenPath}: {resolveError}");
                continue;
            }

            if (!File.Exists(fullPath))
            {
                status.Add($"@{tokenPath}: not found at {fullPath}; literal token kept in the prompt.");
                continue;
            }

            if (ScryingFocusStager.IsImagePath(fullPath))
            {
                ScryingFocusStager.StagingResult sizeCheck = ScryingFocusStager.CheckSize(fullPath, maxImage);
                if (sizeCheck.Error is not null)
                {
                    status.Add($"Cannot stage Scrying focus {Path.GetFileName(fullPath)}: {sizeCheck.Error}");
                    continue;
                }

                stagedImages.Add(fullPath);
                string sizeLabel = ScryingFocusStager.FormatByteCount(sizeCheck.FileSizeBytes ?? 0);
                status.Add($"Scrying focus: {Path.GetFileName(fullPath)} ({sizeLabel})");
                workingPrompt = workingPrompt.Remove(match.Index, match.Length);
                continue;
            }

            long len;
            try
            {
                len = new FileInfo(fullPath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Add($"Cannot stage {Path.GetFileName(fullPath)}: {ex.Message}");
                continue;
            }

            if (len > maxAttach)
            {
                status.Add(
                    $"Cannot stage {Path.GetFileName(fullPath)}: File exceeds the configured limit ({maxAttach} bytes).");
            }
            else
            {
                stagedText.Add(fullPath);
                status.Add($"Staged: {Path.GetFileName(fullPath)}");
            }

            workingPrompt = workingPrompt.Remove(match.Index, match.Length);
        }

        foreach (string pre in preStagedPaths)
        {
            if (string.IsNullOrWhiteSpace(pre))
            {
                continue;
            }

            string full = Path.GetFullPath(pre);
            if (!File.Exists(full))
            {
                status.Add($"/attach: not found at {full}");
                continue;
            }

            if (ScryingFocusStager.IsImagePath(full))
            {
                stagedImages.Add(full);
            }
            else
            {
                stagedText.Add(full);
            }
        }

        List<AttachedFileDto>? attached = null;
        List<string> relativeFooter = [];

        if (stagedText.Count > 0)
        {
            attached = [];
            foreach (string file in stagedText.OrderBy(static f => f, StringComparer.Ordinal))
            {
                string fileName = Path.GetFileName(file);
                try
                {
                    long len = new FileInfo(file).Length;
                    if (len > maxAttach)
                    {
                        status.Add(
                            $"Cannot stage {fileName}: File exceeds the configured limit ({maxAttach} bytes).");
                        continue;
                    }

                    string contents = File.ReadAllText(file, Encoding.UTF8);
                    string relativePath = Path.GetRelativePath(workingDirectory, file);
                    attached.Add(new AttachedFileDto(relativePath, contents));
                    relativeFooter.Add(relativePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    status.Add($"Cannot stage {fileName}: {ex.Message}");
                }
            }

            if (attached.Count == 0)
            {
                attached = null;
            }
            else if (relativeFooter.Count > 0)
            {
                workingPrompt += $"\n\n[Attached Files: {string.Join(", ", relativeFooter)}]";
            }
        }

        List<ScryingFocusDto>? foci = null;
        if (stagedImages.Count > 0)
        {
            foci = [];
            foreach (string imagePath in stagedImages.OrderBy(static f => f, StringComparer.Ordinal))
            {
                ScryingFocusStager.StagingResult staged = ScryingFocusStager.Stage(imagePath, maxImage, allowedMime);
                if (!staged.IsSuccess || staged.Focus is null)
                {
                    status.Add(
                        $"Cannot stage Scrying focus {Path.GetFileName(imagePath)}: {staged.Error ?? "unknown error"}");
                    continue;
                }

                foci.Add(staged.Focus);
            }

            if (foci.Count == 0)
            {
                foci = null;
            }
        }

        bool clearPre = preStagedPaths.Count > 0;
        return new TurnAttachmentBuildResult(
            workingPrompt,
            attached,
            foci,
            status,
            clearPre);
    }

    /// <summary>Resolves a path for <c>/attach</c> and reports a staging status line.</summary>
    public static bool TryStagePathForNextTurn(
        string workingDirectory,
        string argument,
        ArcanumSettings settings,
        out string fullPath,
        out string statusLine)
    {
        fullPath = string.Empty;
        statusLine = string.Empty;

        if (string.IsNullOrWhiteSpace(argument))
        {
            statusLine = "Usage: /attach <path>  (or use @path in the next message)";
            return false;
        }

        if (!TryResolvePath(workingDirectory, argument.Trim(), out fullPath, out string? resolveError))
        {
            statusLine = $"/attach: {resolveError}";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            statusLine = $"/attach: not found at {fullPath}";
            return false;
        }

        long maxAttach = ArcanumSettingClamps.MaxAttachFileSizeBytes(
            ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes);
        ScryingSettings scrying = settings.ResolveScrying();
        long maxImage = ArcanumSettingClamps.ScryingMaxImageBytes(scrying.MaxImageBytes);

        if (ScryingFocusStager.IsImagePath(fullPath))
        {
            ScryingFocusStager.StagingResult sizeCheck = ScryingFocusStager.CheckSize(fullPath, maxImage);
            if (sizeCheck.Error is not null)
            {
                statusLine = $"Cannot stage Scrying focus {Path.GetFileName(fullPath)}: {sizeCheck.Error}";
                return false;
            }

            string sizeLabel = ScryingFocusStager.FormatByteCount(sizeCheck.FileSizeBytes ?? 0);
            statusLine = $"Scrying focus staged for next turn: {Path.GetFileName(fullPath)} ({sizeLabel})";
            return true;
        }

        try
        {
            long len = new FileInfo(fullPath).Length;
            if (len > maxAttach)
            {
                statusLine =
                    $"Cannot stage {Path.GetFileName(fullPath)}: File exceeds the configured limit ({maxAttach} bytes).";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            statusLine = $"Cannot stage {Path.GetFileName(fullPath)}: {ex.Message}";
            return false;
        }

        statusLine = $"Staged for next turn: {Path.GetFileName(fullPath)}";
        return true;
    }

    private static bool TryResolvePath(
        string workingDirectory,
        string tokenPath,
        out string fullPath,
        out string? error)
    {
        fullPath = string.Empty;
        error = null;
        try
        {
            fullPath = Path.IsPathRooted(tokenPath)
                ? Path.GetFullPath(tokenPath)
                : Path.GetFullPath(Path.Combine(workingDirectory, tokenPath));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            error = $"could not be resolved as a path ({ex.GetType().Name}).";
            return false;
        }
    }
}

internal sealed record TurnAttachmentBuildResult(
    string Prompt,
    IReadOnlyList<AttachedFileDto>? AttachedFiles,
    IReadOnlyList<ScryingFocusDto>? ScryingFoci,
    IReadOnlyList<string> StatusLines,
    bool ClearPreStagedAfterTurn);
