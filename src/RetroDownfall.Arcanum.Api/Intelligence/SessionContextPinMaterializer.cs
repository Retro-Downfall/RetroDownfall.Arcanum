using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

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
    ArcanumDbContext db)
{
    public const int MaxPinsPerTurn = 32;

    public const int MaxBytesPerPin = 64 * 1024;

    public const int MaxBytesPerTurn = 256 * 1024;

    public const long MaxSourceFileBytes = 64L * 1024L * 1024L;

    private const int MaxDirectoryFiles = 64;

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
        int omitted = Math.Max(0, rows.Count - MaxPinsPerTurn);

        foreach (SessionContextPinRecord pin in rows.Take(MaxPinsPerTurn))
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
                materialized = new(SessionContextPinStatus.Error, null, ex.Message);
            }

            string block = FormatAsUntrustedData(pin, materialized);
            int blockBytes = Encoding.UTF8.GetByteCount(block);
            if (blockBytes > remaining)
            {
                block = TruncateUtf8(block, remaining) + "\n[TRUNCATED BY PER-TURN CONTEXT BUDGET]";
                blockBytes = Encoding.UTF8.GetByteCount(block);
            }

            TextContent content = new(block);

            contents.Add(content);

            items.Add(
                new ContextPinMaterializedItem(
                    pin.Id,
                    pin.Kind,
                    materialized.SourceId ?? pin.TargetIdentifier,
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
                $"[SESSION CONTEXT PINS: {omitted} pin(s) omitted by the {MaxPinsPerTurn}-pin/{MaxBytesPerTurn}-byte budget.]"));
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
            SessionContextPinKind.DirectorySnapshot => MaterializeDirectory(pin, workingDirectory, byteLimit),
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

            if (stream.Length > MaxSourceFileBytes)
            {

                return new(
                    SessionContextPinStatus.Truncated,
                    null,
                    $"File exceeds the {MaxSourceFileBytes}-byte safe materialization limit.");

            }

            BoundedFileRead source = await ReadBoundedFileAsync(
                stream,
                byteLimit,
                cancellationToken).ConfigureAwait(false);

            string hash = source.Sha256;

            SessionContextPinStatus freshness =
                pin.ContentVersion is not null && !string.Equals(pin.ContentVersion, hash, StringComparison.OrdinalIgnoreCase)
                    ? SessionContextPinStatus.Modified
                    : SessionContextPinStatus.Current;

            SessionContextPinStatus status = source.Truncated
                ? SessionContextPinStatus.Truncated
                : freshness;

            return new(
                status,
                source.Content,
                freshness == SessionContextPinStatus.Modified
                    ? $"Content changed; current sha256={hash}."
                    : $"sha256={hash}.");

        }
    }

    private static MaterializedPin MaterializeDirectory(
        SessionContextPinRecord pin, string? workingDirectory, int byteLimit)
    {
        if (!TryResolveWorkspacePath(workingDirectory, pin.TargetIdentifier, out string path, out string error))
        {
            return new(SessionContextPinStatus.Unsafe, null, error);
        }

        if (!Directory.Exists(path))
        {
            return new(SessionContextPinStatus.Missing, null, "Directory no longer exists.");
        }

        string[] files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .Take(MaxDirectoryFiles + 1)
            .ToArray();
        StringBuilder snapshot = new();
        foreach (string file in files.Take(MaxDirectoryFiles))
        {
            string resolved = Path.GetFullPath(file);
            if (!resolved.StartsWith(Path.GetFullPath(path) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }
            snapshot.Append(Path.GetRelativePath(path, file)).Append('\t').Append(new FileInfo(file).Length).AppendLine();
        }

        if (files.Length > MaxDirectoryFiles)
        {
            snapshot.AppendLine($"[TRUNCATED: more than {MaxDirectoryFiles} files]");
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
            || end < start || end - start > 2_000)
        {
            return new(SessionContextPinStatus.Error, null, "Invalid or excessive line range.");
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

            if (stream.Length > MaxSourceFileBytes)
            {

                return new(
                    SessionContextPinStatus.Truncated,
                    null,
                    $"File exceeds the {MaxSourceFileBytes}-byte safe materialization limit.");

            }

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

    internal static async Task<BoundedFileRead> ReadBoundedFileAsync(
        Stream stream,
        int byteLimit,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(stream);

        ArgumentOutOfRangeException.ThrowIfNegative(byteLimit);

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        using MemoryStream content = new(Math.Min(byteLimit, 16 * 1024));

        byte[] buffer = new byte[64 * 1024];

        long totalRead = 0;

        int read;

        while ((read = await stream
            .ReadAsync(buffer, cancellationToken)
            .ConfigureAwait(false)) > 0)
        {

            hash.AppendData(buffer, 0, read);

            totalRead += read;

            int remaining = byteLimit - (int)content.Length;

            if (remaining > 0)
            {

                content.Write(buffer, 0, Math.Min(remaining, read));

            }

        }

        string text = Encoding.UTF8.GetString(content.GetBuffer(), 0, (int)content.Length);

        text = TruncateUtf8(text, byteLimit);

        string sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        return new BoundedFileRead(text, sha256, totalRead > byteLimit);

    }

    internal sealed record BoundedFileRead(
        string Content,
        string Sha256,
        bool Truncated);

    private async Task<MaterializedPin> MaterializeEntryAsync(
        SessionContextPinRecord pin, Guid sessionId, int byteLimit, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(pin.TargetIdentifier, out Guid entryId))
        {
            return new(SessionContextPinStatus.Error, null, "Invalid entry identifier.");
        }

        string? content = await db.Entries.AsNoTracking()
            .Where(entry => entry.Id == entryId && entry.SessionId == sessionId)
            .Select(entry => entry.Content)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return content is null
            ? new(SessionContextPinStatus.Missing, null, "Entry no longer exists in this session.")
            : FromText(content, byteLimit);
    }

    private async Task<MaterializedPin> MaterializeAttachmentAsync(
        SessionContextPinRecord pin, Guid sessionId, int byteLimit, CancellationToken cancellationToken)
    {
        SessionAttachmentRecord? record = Guid.TryParse(pin.TargetIdentifier, out Guid id)
            ? await attachments.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            : await attachments.GetByLogicalAsync(sessionId, pin.TargetIdentifier, null, cancellationToken)
                .ConfigureAwait(false);
        if (record is null || record.SessionId != sessionId || record.Kind != SessionAttachmentKind.Text)
        {
            return new(SessionContextPinStatus.Missing, null, "Text attachment was not found in this session.");
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
            string prefix = root + Path.DirectorySeparatorChar;
            if (!candidate.Equals(root, StringComparison.Ordinal)
                && !candidate.StartsWith(prefix, StringComparison.Ordinal))
            {
                error = "Path escapes the workspace.";
                return false;
            }

            string relative = Path.GetRelativePath(root, candidate);
            string current = root;
            foreach (string component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.GetFullPath(Path.Combine(current, component));
                if (!File.Exists(current) && !Directory.Exists(current))
                {
                    continue;
                }
                FileSystemInfo info = File.Exists(current)
                    ? new FileInfo(current)
                    : new DirectoryInfo(current);
                FileSystemInfo? link = info.ResolveLinkTarget(returnFinalTarget: true);
                if (link is not null)
                {
                    current = Path.GetFullPath(link.FullName);
                    if (!current.Equals(root, StringComparison.Ordinal)
                        && !current.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        error = "Symlink target escapes the workspace.";
                        path = string.Empty;
                        return false;
                    }
                }
            }
            path = current;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string FormatAsUntrustedData(SessionContextPinRecord pin, MaterializedPin value)
    {
        string content = value.Content ?? string.Empty;
        int fenceLength = Math.Max(3, LongestBacktickRun(content) + 1);
        string fence = new('`', fenceLength);
        return $"""
            [UNTRUSTED SESSION CONTEXT DATA]
            source-kind: {pin.Kind}
            source-label: {pin.DisplayLabel}
            source-id: {pin.TargetIdentifier}
            status: {value.Status}
            diagnostic: {value.Diagnostic ?? "none"}
            {fence}data
            {content}
            {fence}
            [END UNTRUSTED SESSION CONTEXT DATA]
            """;
    }

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

    private sealed record MaterializedPin(
        SessionContextPinStatus Status,
        string? Content,
        string? Diagnostic,
        string? SourceId = null,
        int? SourceVersion = null,
        string? SourceHash = null);
}
