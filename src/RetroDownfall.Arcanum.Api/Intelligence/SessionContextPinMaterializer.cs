using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed record SessionContextPinMaterialization(
    IReadOnlyList<AIContent> Contents,
    int IncludedBytes,
    int OmittedCount,
    IReadOnlyList<ContextPinMaterializedItem>? Items = null);

public sealed record ContextPinMaterializedItem(
    Guid PinId,
    SessionContextPinKind Kind,
    string SourceId,
    string SourceLabel,
    string ContentHash,
    int? VersionOrdinal,
    int MaterializedBytes,
    AIContent Content);

/// <summary>Revalidates and materializes durable context pins as explicitly untrusted model data.</summary>
public sealed class SessionContextPinMaterializer(
    ISessionContextPinStore pins,
    ISessionAttachmentStore attachments,
    ISessionRepository sessions)
{
    private const string StartMarker = "[UNTRUSTED SESSION CONTEXT DATA]";

    private const string EndMarker = "[END UNTRUSTED SESSION CONTEXT DATA]";

    private const string PerTurnTruncationNotice = "[TRUNCATED BY PER-TURN CONTEXT BUDGET]";

    /// <summary>Longest source label, id or diagnostic echoed into a block header.</summary>
    private const int MaxHeaderValueChars = 256;

    /// <summary>
    /// Largest file whose whole content is hashed to decide freshness. A larger file is previewed without
    /// reading past the preview, and its size and last-write time stand in for the hash.
    /// </summary>
    internal const long FileHashCapBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Directories one directory-snapshot pin visits before it stops and reports truncation. The byte
    /// budget bounds what a snapshot emits; this bounds the walk itself when most directories contribute
    /// no rows.
    /// </summary>
    internal const int MaxDirectoriesPerSnapshot = 2_048;

    /// <summary>
    /// Directory names a snapshot does not descend into: version-control metadata, installed
    /// dependencies and build output. They are matched by name at any depth below the pinned directory.
    /// </summary>
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        "node_modules",
        "bin",
        "obj",
    };

    private const string FreshnessTokenPrefix = "size=";

    private const string DirectoryTruncationSuffix =
        "[TRUNCATED BY CONTEXT MATERIALIZATION BUDGET]";

    public const int MaxBytesPerPin = 64 * 1024;

    public const int MaxBytesPerTurn = 256 * 1024;

    public async Task<SessionContextPinMaterialization> MaterializeAsync(
        Guid sessionId,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionContextPinRecord> rows =
            await pins.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        List<AIContent> contents = [];
        List<ContextPinMaterializedItem> items = [];
        int bytes = 0;
        int omitted = 0;

        foreach (SessionContextPinRecord pin in rows)
        {
            int remaining = MaxBytesPerTurn - bytes;
            if (remaining <= 0)
            {
                omitted++;
                continue;
            }

            MaterializedPin materialized;
            try
            {
                materialized = await MaterializeOneAsync(
                    pin, sessionId, workingDirectory, Math.Min(MaxBytesPerPin, remaining), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                materialized = new(SessionContextPinStatus.Error, null, DescribeFailure(ex));
            }

            // A diagnostic pin's target is its whole body, so its stable id is the pin id.
            string sourceId = materialized.SourceId
                ?? (pin.Kind == SessionContextPinKind.Diagnostic
                    ? pin.Id.ToString("N")
                    : pin.TargetIdentifier);

            string? framed = FormatAsUntrustedData(pin, materialized, sourceId, remaining);

            if (framed is null)
            {
                // Not even an empty frame fits in what is left, so defer the pin rather than emit an
                // unclosed envelope.
                omitted++;

                continue;
            }

            string block = framed;

            int blockBytes = Encoding.UTF8.GetByteCount(block);

            TextContent content = new(block);

            contents.Add(content);

            items.Add(
                new ContextPinMaterializedItem(
                    pin.Id,
                    pin.Kind,
                    sourceId,
                    pin.DisplayLabel,
                    materialized.SourceHash
                        ?? Convert.ToHexString(
                            SHA256.HashData(
                                Encoding.UTF8.GetBytes(materialized.Content ?? block)))
                            .ToLowerInvariant(),
                    materialized.SourceVersion,
                    blockBytes,
                    content));

            bytes += blockBytes;
        }

        if (omitted > 0)
        {
            contents.Add(new TextContent(
                $"[SESSION CONTEXT PINS: {omitted} pin(s) deferred by the {MaxBytesPerTurn}-byte context materialization budget.]"));
        }

        return new(contents, bytes, omitted, items);
    }

    private async Task<MaterializedPin> MaterializeOneAsync(
        SessionContextPinRecord pin,
        Guid sessionId,
        string? workingDirectory,
        int byteLimit,
        CancellationToken cancellationToken) =>
        pin.Kind switch
        {
            SessionContextPinKind.DirectorySnapshot => MaterializeDirectory(
                pin,
                workingDirectory,
                byteLimit,
                cancellationToken),
            SessionContextPinKind.File => await MaterializeFileAsync(
                pin,
                workingDirectory,
                byteLimit,
                cancellationToken).ConfigureAwait(false),
            SessionContextPinKind.SymbolRange => await MaterializeSymbolRangeAsync(
                pin,
                workingDirectory,
                byteLimit,
                cancellationToken).ConfigureAwait(false),
            SessionContextPinKind.SessionEntry => await MaterializeEntryAsync(
                pin, sessionId, byteLimit, cancellationToken).ConfigureAwait(false),
            SessionContextPinKind.Attachment => await MaterializeAttachmentAsync(
                pin, sessionId, byteLimit, cancellationToken).ConfigureAwait(false),
            SessionContextPinKind.Diagnostic => FromText(pin.TargetIdentifier, byteLimit),
            SessionContextPinKind.Url => new(
                SessionContextPinStatus.Unsupported,
                null,
                "URL pins require the guarded browsing pipeline and are not fetched during implicit materialization."),
            _ => new(SessionContextPinStatus.Unsupported, null, "Unsupported context pin kind."),
        };

    private static async Task<MaterializedPin> MaterializeFileAsync(
        SessionContextPinRecord pin,
        string? workingDirectory,
        int byteLimit,
        CancellationToken cancellationToken)
    {
        if (!TryResolveWorkspacePath(workingDirectory, pin.TargetIdentifier, out string path, out string error))
        {
            return new(SessionContextPinStatus.Unsafe, null, error);
        }

        SecureFileOpenStatus openStatus = SecureFileReader.TryOpenRegularFile(
            path,
            expectedIdentity: null,
            out FileStream? stream,
            out _);

        if (openStatus == SecureFileOpenStatus.NotFound)
        {
            return new(SessionContextPinStatus.Missing, null, "File no longer exists.");
        }

        if (openStatus != SecureFileOpenStatus.Success || stream is null)
        {
            return new(
                SessionContextPinStatus.Unsafe,
                null,
                "File could not be opened as an unaliased regular file.");
        }

        await using (stream)
        {
            long length = stream.Length;

            BoundedFileRead source = await ReadBoundedFileAsync(
                stream,
                byteLimit,
                FileHashCapBytes,
                cancellationToken).ConfigureAwait(false);

            // Above the hash cap the file is previewed without reading the rest, so the freshness
            // token is its size and last-write time rather than a content hash.
            string? hash = source.Sha256;

            string freshnessToken = hash
                ?? $"{FreshnessTokenPrefix}{length};mtime={File.GetLastWriteTimeUtc(path).Ticks}";

            // A pinned content hash cannot be checked against a size/mtime token, so it is not
            // reported as a change.
            bool versionComparable = hash is not null
                || pin.ContentVersion?.StartsWith(FreshnessTokenPrefix, StringComparison.OrdinalIgnoreCase) == true;

            SessionContextPinStatus freshness =
                pin.ContentVersion is not null
                && versionComparable
                && !string.Equals(pin.ContentVersion, freshnessToken, StringComparison.OrdinalIgnoreCase)
                    ? SessionContextPinStatus.Modified
                    : SessionContextPinStatus.Current;

            SessionContextPinStatus status = source.Truncated
                ? SessionContextPinStatus.Truncated
                : freshness;

            string diagnostic = hash is not null
                ? freshness == SessionContextPinStatus.Modified
                    ? $"Content changed; current sha256={hash}."
                    : $"sha256={hash}."
                : freshness == SessionContextPinStatus.Modified
                    ? $"Content changed; current freshness token {freshnessToken}."
                    : $"Content hash skipped above the {FileHashCapBytes}-byte cap; freshness token {freshnessToken}.";

            return new(status, source.Content, diagnostic);
        }
    }

    private static MaterializedPin MaterializeDirectory(
        SessionContextPinRecord pin,
        string? workingDirectory,
        int byteLimit,
        CancellationToken cancellationToken)
    {
        if (!TryResolveWorkspacePath(workingDirectory, pin.TargetIdentifier, out string path, out string error))
        {
            return new(SessionContextPinStatus.Unsafe, null, error);
        }

        if (!Directory.Exists(path))
        {
            return new(SessionContextPinStatus.Missing, null, "Directory no longer exists.");
        }

        string workspaceRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(workingDirectory!));

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                workspaceRoot,
                path,
                out string? resolvedSnapshotRoot))
        {
            return new(
                SessionContextPinStatus.Unsafe,
                null,
                "Directory failed canonical workspace containment revalidation.");
        }

        string snapshotRoot = Path.GetFullPath(
            resolvedSnapshotRoot ?? path);

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        HashSet<string> visitedCanonicalDirectories = new(
            pathComparer);

        _ = visitedCanonicalDirectories.Add(snapshotRoot);

        Queue<(string TraversalPath, string CanonicalIdentity)> directories = new();

        directories.Enqueue((snapshotRoot, snapshotRoot));

        StringBuilder snapshot = new();

        int snapshotBytes = 0;

        bool truncated = false;

        bool stoppedByDirectoryCap = false;

        int directoriesVisited = 0;

        while (directories.Count > 0
            && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (string directory, string queuedIdentity) =
                directories.Dequeue();

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                    workspaceRoot,
                    directory,
                    out string? resolvedDirectory))
            {
                continue;
            }

            string currentIdentity = Path.GetFullPath(
                resolvedDirectory ?? directory);

            if (!pathComparer.Equals(
                    currentIdentity,
                    queuedIdentity)
                && !visitedCanonicalDirectories.Add(
                    currentIdentity))
            {
                continue;
            }

            if (directoriesVisited >= MaxDirectoriesPerSnapshot)
            {
                truncated = true;

                stoppedByDirectoryCap = true;

                break;
            }

            directoriesVisited++;

            string[] entries = Directory
                .EnumerateFileSystemEntries(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(static entry => entry, StringComparer.Ordinal)
                .ToArray();

            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                        workspaceRoot,
                        entry,
                        out string? resolvedEntry))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    // Version-control and dependency trees would otherwise spend the byte budget before
                    // the source does. Only descendants are skipped; a pin rooted at one still lists it.
                    if (IgnoredDirectoryNames.Contains(Path.GetFileName(entry)))
                    {
                        continue;
                    }

                    string canonicalDirectory = Path.GetFullPath(
                        resolvedEntry ?? entry);

                    if (visitedCanonicalDirectories.Add(
                            canonicalDirectory))
                    {
                        directories.Enqueue(
                            (entry, canonicalDirectory));
                    }

                    continue;
                }

                if (!File.Exists(entry))
                {
                    continue;
                }

                string row =
                    $"{Path.GetRelativePath(snapshotRoot, entry)}\t{new FileInfo(entry).Length}{Environment.NewLine}";

                int rowBytes = Encoding.UTF8.GetByteCount(row);

                if (snapshotBytes + rowBytes > byteLimit)
                {
                    truncated = true;

                    break;
                }

                _ = snapshot.Append(row);

                snapshotBytes += rowBytes;
            }
        }

        if (truncated)
        {
            string suffix = snapshot.Length == 0
                ? DirectoryTruncationSuffix
                : Environment.NewLine + DirectoryTruncationSuffix;

            string content = AppendSuffixWithinUtf8Budget(
                snapshot.ToString(),
                suffix,
                byteLimit);

            return new(
                SessionContextPinStatus.Truncated,
                content,
                stoppedByDirectoryCap
                    ? $"Stopped after visiting {MaxDirectoriesPerSnapshot} directories."
                    : $"Limited to {byteLimit} bytes.");
        }

        return FromText(snapshot.ToString(), byteLimit);
    }

    private static async Task<MaterializedPin> MaterializeSymbolRangeAsync(
        SessionContextPinRecord pin,
        string? workingDirectory,
        int byteLimit,
        CancellationToken cancellationToken)
    {
        int separator = pin.TargetIdentifier.LastIndexOf(':');
        if (separator <= 0)
        {
            return new(SessionContextPinStatus.Error, null, "Expected path:start-end.");
        }

        string pathPart = pin.TargetIdentifier[..separator];
        string[] range = pin.TargetIdentifier[(separator + 1)..].Split('-', 2);
        if (!int.TryParse(range[0], out int start) || start < 1
            || !int.TryParse(range.Length == 2 ? range[1] : range[0], out int end)
            || end < start)
        {
            return new(SessionContextPinStatus.Error, null, "Invalid line range.");
        }

        if (!TryResolveWorkspacePath(workingDirectory, pathPart, out string path, out string error))
        {
            return new(SessionContextPinStatus.Unsafe, null, error);
        }

        SecureFileOpenStatus openStatus = SecureFileReader.TryOpenRegularFile(
            path,
            expectedIdentity: null,
            out FileStream? stream,
            out _);

        if (openStatus == SecureFileOpenStatus.NotFound)
        {
            return new(SessionContextPinStatus.Missing, null, "File no longer exists.");
        }

        if (openStatus != SecureFileOpenStatus.Success || stream is null)
        {
            return new(
                SessionContextPinStatus.Unsafe,
                null,
                "File could not be opened as an unaliased regular file.");
        }

        await using (stream)
        {
            return await ReadBoundedLineRangeAsync(
                stream,
                start,
                end,
                byteLimit,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<MaterializedPin> ReadBoundedLineRangeAsync(
        Stream stream,
        int start,
        int end,
        int byteLimit,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];

        using MemoryStream selected = new(Math.Min(byteLimit, 16 * 1024));

        int lineNumber = 1;

        bool truncated = false;

        int read;

        while (lineNumber <= end
            && (read = await stream
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false)) > 0)
        {
            for (int index = 0; index < read && lineNumber <= end; index++)
            {
                byte value = buffer[index];

                if (lineNumber >= start)
                {
                    if (selected.Length < byteLimit)
                    {
                        selected.WriteByte(value);
                    }
                    else
                    {
                        truncated = true;
                    }
                }

                if (value == (byte)'\n')
                {
                    lineNumber++;
                }
            }

            if (truncated)
            {
                break;
            }
        }

        ReadOnlySpan<byte> bytes = selected.GetBuffer().AsSpan(0, (int)selected.Length);

        if (bytes.EndsWith("\n"u8))
        {
            bytes = bytes[..^1];
        }

        if (bytes.EndsWith("\r"u8))
        {
            bytes = bytes[..^1];
        }

        string text = Encoding.UTF8.GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        text = TruncateUtf8(text, byteLimit);

        return new(
            truncated ? SessionContextPinStatus.Truncated : SessionContextPinStatus.Current,
            text,
            truncated ? $"Limited to {byteLimit} bytes." : null);
    }

    internal static Task<BoundedFileRead> ReadBoundedFileAsync(
        Stream stream,
        int byteLimit,
        CancellationToken cancellationToken) =>
        ReadBoundedFileAsync(
            stream,
            byteLimit,
            hashCapBytes: long.MaxValue,
            cancellationToken);

    /// <summary>
    /// Reads at most <paramref name="byteLimit"/> bytes of preview and hashes the whole stream only while
    /// it stays within <paramref name="hashCapBytes"/>. A seekable stream that declares more than the cap
    /// is never read past the preview; an unsized stream is read until the cap is crossed. Beyond the cap
    /// <see cref="BoundedFileRead.Sha256"/> is <see langword="null"/>.
    /// </summary>
    internal static async Task<BoundedFileRead> ReadBoundedFileAsync(
        Stream stream,
        int byteLimit,
        long hashCapBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ArgumentOutOfRangeException.ThrowIfNegative(byteLimit);

        ArgumentOutOfRangeException.ThrowIfNegative(hashCapBytes);

        long declaredLength = stream.CanSeek ? stream.Length : -1;

        IncrementalHash? hash = declaredLength > hashCapBytes
            ? null
            : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            using MemoryStream content = new(Math.Min(byteLimit, 16 * 1024));

            byte[] buffer = new byte[64 * 1024];

            long totalRead = 0;

            while (true)
            {
                int wanted = buffer.Length;

                if (hash is null)
                {
                    // Only the preview is still needed, plus one byte to learn that the stream is
                    // longer when its length is not already known to exceed the limit.
                    bool longerKnown = totalRead > byteLimit || declaredLength > byteLimit;

                    int needed = byteLimit - (int)content.Length;

                    if (needed <= 0 && longerKnown)
                    {
                        break;
                    }

                    wanted = Math.Min(buffer.Length, Math.Max(needed, 0) + (longerKnown ? 0 : 1));
                }

                int read = await stream
                    .ReadAsync(buffer.AsMemory(0, wanted), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                totalRead += read;

                if (hash is not null)
                {
                    if (totalRead > hashCapBytes)
                    {
                        hash.Dispose();

                        hash = null;
                    }
                    else
                    {
                        hash.AppendData(buffer, 0, read);
                    }
                }

                int remaining = byteLimit - (int)content.Length;

                if (remaining > 0)
                {
                    content.Write(buffer, 0, Math.Min(remaining, read));
                }
            }

            string text = Encoding.UTF8.GetString(content.GetBuffer(), 0, (int)content.Length);

            text = TruncateUtf8(text, byteLimit);

            string? sha256 = hash is null
                ? null
                : Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

            return new BoundedFileRead(
                text,
                sha256,
                totalRead > byteLimit || declaredLength > byteLimit);
        }
        finally
        {
            hash?.Dispose();
        }
    }

    internal sealed record BoundedFileRead(
        string Content,
        string? Sha256,
        bool Truncated);

    private async Task<MaterializedPin> MaterializeEntryAsync(
        SessionContextPinRecord pin, Guid sessionId, int byteLimit, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(pin.TargetIdentifier, out Guid entryId))
        {
            return new(SessionContextPinStatus.Error, null, "Invalid entry identifier.");
        }

        RetroDownfall.Arcanum.Core.Storage.Entities.Entry? entry = await sessions
            .GetEntryAsync(sessionId, entryId, cancellationToken)
            .ConfigureAwait(false);

        return entry is null
            ? new(SessionContextPinStatus.Missing, null, "Entry no longer exists in this session.")
            : FromText(entry.Content, byteLimit);
    }

    private async Task<MaterializedPin> MaterializeAttachmentAsync(
        SessionContextPinRecord pin, Guid sessionId, int byteLimit, CancellationToken cancellationToken)
    {
        SessionAttachmentRecord? record = Guid.TryParse(pin.TargetIdentifier, out Guid id)
            ? await attachments.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            : await attachments.GetByLogicalAsync(sessionId, pin.TargetIdentifier, null, cancellationToken)
                .ConfigureAwait(false);
        if (record is null || record.SessionId != sessionId)
        {
            return new(SessionContextPinStatus.Missing, null, "Text attachment was not found in this session.");
        }

        if (record.Kind == SessionAttachmentKind.Image)
        {
            return new(
                SessionContextPinStatus.Unsupported,
                null,
                "Image attachment pins remain selected but require an explicit attachment reference for a vision-capable turn.");
        }

        if (record.Kind != SessionAttachmentKind.Text)
        {
            return new(
                SessionContextPinStatus.Unsupported,
                null,
                "This attachment kind is not supported for implicit text materialization.");
        }

        ReadOnlyMemory<byte> source = await attachments.ReadBytesAsync(record, cancellationToken).ConfigureAwait(false);
        MaterializedPin materialized = FromText(Encoding.UTF8.GetString(source.Span), byteLimit);

        return materialized with
        {
            SourceId = record.Id.ToString("N"),
            SourceVersion = record.Version,
            SourceHash = record.ContentSha256,
        };
    }

    private static MaterializedPin FromText(string text, int byteLimit)
    {
        int size = Encoding.UTF8.GetByteCount(text);
        return size <= byteLimit
            ? new(SessionContextPinStatus.Current, text, null)
            : new(SessionContextPinStatus.Truncated, TruncateUtf8(text, byteLimit), $"Limited to {byteLimit} bytes.");
    }

    private static bool TryResolveWorkspacePath(
        string? workingDirectory, string target, out string path, out string error)
    {
        path = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            error = "No workspace was supplied for this turn.";
            return false;
        }

        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
            string candidate = Path.GetFullPath(target, root);

            if (!WorkspacePathPolicy.IsPathUnderWorkspace(root, candidate))
            {
                error = "Path escapes the workspace.";

                return false;
            }

            // Canonical containment: the root and the candidate are each resolved through every link
            // they traverse and then compared. Comparing a link's target text with the root as the
            // caller spelled it rejected every internal link whenever the root itself was reached
            // through a link, and an intermediate directory link that leaves the workspace must still
            // fail here because the no-follow open only guards the final path component.
            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                    root,
                    candidate,
                    out string? resolvedFinalPath))
            {
                error = "Symlink target escapes the workspace.";

                return false;
            }

            path = resolvedFinalPath ?? candidate;

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = DescribeFailure(ex);
            return false;
        }
    }

    /// <summary>
    /// Frames one pin as untrusted data: a single-line header, the content inside an adaptive backtick
    /// fence, and a fixed footer. Returns <see langword="null"/> when <paramref name="budgetBytes"/> cannot
    /// hold even the frame around empty content. When the whole block does not fit, the footer (fence,
    /// truncation notice and end marker) is reserved first and only the content is cut, so the block is
    /// always balanced and ends with the end marker inside the exact budget.
    /// </summary>
    private static string? FormatAsUntrustedData(
        SessionContextPinRecord pin,
        MaterializedPin value,
        string sourceId,
        int budgetBytes)
    {
        string content = value.Content ?? string.Empty;

        int fenceLength = Math.Max(3, LongestBacktickRun(content) + 1);

        string fence = new('`', fenceLength);

        string header =
            StartMarker + "\n"
            + "source-kind: " + pin.Kind + "\n"
            + "source-label: " + SingleLine(pin.DisplayLabel) + "\n"
            + "source-id: " + SingleLine(sourceId) + "\n"
            + "status: " + value.Status + "\n"
            + "diagnostic: " + (value.Diagnostic is null ? "none" : SingleLine(value.Diagnostic)) + "\n"
            + fence + "data\n";

        string footer = "\n" + fence + "\n" + EndMarker;

        int headerBytes = Encoding.UTF8.GetByteCount(header);

        int contentBytes = Encoding.UTF8.GetByteCount(content);

        int footerBytes = Encoding.UTF8.GetByteCount(footer);

        if (headerBytes + contentBytes + footerBytes <= budgetBytes)
        {
            return header + content + footer;
        }

        string truncatedFooter = "\n" + fence + "\n" + PerTurnTruncationNotice + "\n" + EndMarker;

        int contentBudget = budgetBytes - headerBytes - Encoding.UTF8.GetByteCount(truncatedFooter);

        return contentBudget < 0
            ? null
            : header + TruncateUtf8(content, contentBudget) + truncatedFooter;
    }

    /// <summary>
    /// Renders a header value on one bounded line. Line breaks, other control characters, backslashes and
    /// backticks are escaped, so a label, id or diagnostic can neither open a line that looks like the end
    /// marker or a fence nor repeat an arbitrarily large body outside the fenced data.
    /// </summary>
    private static string SingleLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder line = new(Math.Min(value.Length, MaxHeaderValueChars) + 3);

        int taken = 0;

        foreach (char c in value)
        {
            if (taken >= MaxHeaderValueChars)
            {
                if (line.Length > 0 && char.IsHighSurrogate(line[^1]))
                {
                    line.Length--;
                }

                _ = line.Append("...");

                break;
            }

            taken++;

            _ = c switch
            {
                '\\' => line.Append("\\\\"),
                '\r' => line.Append("\\r"),
                '\n' => line.Append("\\n"),
                '\t' => line.Append("\\t"),
                '`' => line.Append("\\u0060"),
                _ when char.IsControl(c) || c is '\u2028' or '\u2029' =>
                    line.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)),
                _ => line.Append(c),
            };
        }

        return line.ToString();
    }

    private static string DescribeFailure(Exception failure) =>
        failure switch
        {
            UnauthorizedAccessException => "Access to the pin source was denied.",
            IOException => "An I/O error prevented reading the pin source.",
            _ => "The pin source could not be materialized.",
        };

    private static int LongestBacktickRun(string value)
    {
        int longest = 0;
        int current = 0;
        foreach (char c in value)
        {
            current = c == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private static string TruncateUtf8(string value, int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return value;
        }

        int length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return Encoding.UTF8.GetString(bytes, 0, length);
    }

    private static string AppendSuffixWithinUtf8Budget(
        string value,
        string suffix,
        int maxBytes)
    {
        if (maxBytes <= 0)
        {
            return string.Empty;
        }

        int suffixBytes = Encoding.UTF8.GetByteCount(suffix);

        if (suffixBytes >= maxBytes)
        {
            return TruncateUtf8(
                suffix,
                maxBytes);
        }

        string prefix = TruncateUtf8(
            value,
            maxBytes - suffixBytes);

        return prefix + suffix;
    }

    private sealed record MaterializedPin(
        SessionContextPinStatus Status,
        string? Content,
        string? Diagnostic,
        string? SourceId = null,
        int? SourceVersion = null,
        string? SourceHash = null);
}
