using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// UI-agnostic turn attachment builder: inline <c>@path</c> tokens + pre-staged <c>/attach</c> paths.
/// Text files → <see cref="AttachedFileDto"/>; images → <see cref="ScryingFocusDto"/> (ephemeral).
/// </summary>
internal static partial class CommandCenterTurnAttachmentBuilder
{
    /// <summary>
    /// An <c>@path</c> token at the start of the prompt or after whitespace. The prompt is operator-typed
    /// but can be a paste of anything, so the match carries a timeout instead of the engine's infinite default.
    /// </summary>
    [GeneratedRegex(
        @"(?<=^|\s)@([^\s]+)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 250)]
    private static partial Regex AtTokenRegex();

    private const int FileReadBufferBytes = 81920;

    /// <summary>
    /// Resolves <c>@path</c> tokens and pre-staged <c>/attach</c> paths into the turn's attachments. Only
    /// regular files are staged; the reads are bounded and observe <paramref name="cancellationToken"/>
    /// between chunks. A caller that must stay responsive when a read stalls inside the operating system
    /// awaits this through <see cref="AbandonableBlockingWork"/>.
    /// </summary>
    public static async Task<TurnAttachmentBuildResult> BuildAsync(
        string prompt,
        string workingDirectory,
        IReadOnlyCollection<string> preStagedPaths,
        ArcanumSettings settings,
        CancellationToken cancellationToken)
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
        MatchCollection atMatches = AtTokenRegex().Matches(workingPrompt);

        for (int mi = atMatches.Count - 1; mi >= 0; mi--)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            if (!AttachableFile.TryConfirmRegularFile(fullPath, out string? notRegularReason))
            {
                // A FIFO reports a length of 0 and opens only when a writer appears, and a device has no
                // end: neither is an attachment, and reading either would hold the turn indefinitely.
                status.Add(
                    $"Cannot stage {Path.GetFileName(fullPath)}: {notRegularReason}; literal token kept in the prompt.");
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
                // Staging failed, so the token stays: rewriting the prompt would send the model a
                // question the operator never asked and persist that edit as the user turn.
                status.Add(
                    $"Cannot stage {Path.GetFileName(fullPath)}: File exceeds the configured limit ({maxAttach} bytes); literal token kept in the prompt.");
                continue;
            }

            stagedText.Add(fullPath);
            status.Add($"Staged: {Path.GetFileName(fullPath)}");
            workingPrompt = workingPrompt.Remove(match.Index, match.Length);
        }

        foreach (string pre in preStagedPaths)
        {
            if (string.IsNullOrWhiteSpace(pre))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!TryResolvePath(workingDirectory, pre, out string full, out string? preResolveError))
            {
                status.Add($"/attach: {preResolveError}");
                continue;
            }

            if (!File.Exists(full))
            {
                status.Add($"/attach: not found at {full}");
                continue;
            }

            if (!AttachableFile.TryConfirmRegularFile(full, out string? preNotRegularReason))
            {
                status.Add($"/attach: {Path.GetFileName(full)} is {preNotRegularReason}; skipped.");
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
                cancellationToken.ThrowIfCancellationRequested();

                string fileName = Path.GetFileName(file);
                try
                {
                    string? contents = await ReadBoundedTextAsync(file, maxAttach, cancellationToken)
                        .ConfigureAwait(false);
                    if (contents is null)
                    {
                        status.Add(
                            $"Cannot stage {fileName}: File exceeds the configured limit ({maxAttach} bytes).");
                        continue;
                    }

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
                ScryingFocusStager.StagingResult staged = ScryingFocusStager.Stage(
                    imagePath,
                    maxImage,
                    allowedMime,
                    cancellationToken);
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

        if (!AttachableFile.TryConfirmRegularFile(fullPath, out string? notRegularReason))
        {
            statusLine = $"/attach: {Path.GetFileName(fullPath)} is {notRegularReason}.";
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

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> of UTF-8 text from <paramref name="path"/>, or returns
    /// <see langword="null"/> when the file holds more than that. The file is opened without ever waiting
    /// on the path and judged by its own handle, so a path that became a FIFO or a device after the stat
    /// is refused instead of blocking the read or feeding it an unbounded stream.
    /// </summary>
    internal static async Task<string?> ReadBoundedTextAsync(
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = AttachableFile.OpenForRead(path, FileReadBufferBytes);

        using MemoryStream content = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(FileReadBufferBytes);
        try
        {
            long total = 0;
            while (true)
            {
                int want = (int)Math.Min(buffer.Length, maxBytes - total + 1);
                int read = await stream
                    .ReadAsync(buffer.AsMemory(0, want), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maxBytes)
                {
                    return null;
                }

                content.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        content.Position = 0;
        using StreamReader reader = new(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
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

/// <summary>
/// Builds a turn's attachments. The chat runner holds one as a seam, so a test can make the build block
/// the way a read stalled inside the operating system does.
/// </summary>
internal delegate Task<TurnAttachmentBuildResult> AttachmentBuildDelegate(
    string prompt,
    string workingDirectory,
    IReadOnlyCollection<string> preStagedPaths,
    ArcanumSettings settings,
    CancellationToken cancellationToken);

internal sealed record TurnAttachmentBuildResult(
    string Prompt,
    IReadOnlyList<AttachedFileDto>? AttachedFiles,
    IReadOnlyList<ScryingFocusDto>? ScryingFoci,
    IReadOnlyList<string> StatusLines,
    bool ClearPreStagedAfterTurn);
