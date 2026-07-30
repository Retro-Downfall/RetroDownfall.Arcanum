using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Api.Intelligence;

public sealed record SessionContextPinMaterialization(
    IReadOnlyList<AIContent> Contents,
    int IncludedBytes,
    int OmittedCount);

/// <summary>Revalidates and materializes durable context pins as explicitly untrusted model data.</summary>
public sealed class SessionContextPinMaterializer(
    ISessionContextPinStore pins,
    ISessionAttachmentStore attachments,
    ArcanumDbContext db)
{
    public const int MaxPinsPerTurn = 32;
    public const int MaxBytesPerPin = 64 * 1024;
    public const int MaxBytesPerTurn = 256 * 1024;
    private const int MaxDirectoryFiles = 64;

    public async Task<SessionContextPinMaterialization> MaterializeAsync(
        Guid sessionId,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionContextPinRecord> rows =
            await pins.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        List<AIContent> contents = [];
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

            contents.Add(new TextContent(block));
            bytes += blockBytes;
        }

        if (omitted > 0)
        {
            contents.Add(new TextContent(
                $"[SESSION CONTEXT PINS: {omitted} pin(s) omitted by the {MaxPinsPerTurn}-pin/{MaxBytesPerTurn}-byte budget.]"));
        }

        return new(contents, bytes, omitted);
    }

    private async Task<MaterializedPin> MaterializeOneAsync(
        SessionContextPinRecord pin,
        Guid sessionId,
        string? workingDirectory,
        int byteLimit,
        CancellationToken cancellationToken) =>
        pin.Kind switch
        {
            SessionContextPinKind.File => MaterializeFile(pin, workingDirectory, byteLimit),
            SessionContextPinKind.DirectorySnapshot => MaterializeDirectory(pin, workingDirectory, byteLimit),
            SessionContextPinKind.SymbolRange => MaterializeSymbolRange(pin, workingDirectory, byteLimit),
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

    private static MaterializedPin MaterializeFile(
        SessionContextPinRecord pin, string? workingDirectory, int byteLimit)
    {
        if (!TryResolveWorkspacePath(workingDirectory, pin.TargetIdentifier, out string path, out string error))
        {
            return new(SessionContextPinStatus.Unsafe, null, error);
        }

        if (!File.Exists(path))
        {
            return new(SessionContextPinStatus.Missing, null, "File no longer exists.");
        }

        byte[] source = File.ReadAllBytes(path);
        string hash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        SessionContextPinStatus freshness =
            pin.ContentVersion is not null && !string.Equals(pin.ContentVersion, hash, StringComparison.OrdinalIgnoreCase)
                ? SessionContextPinStatus.Modified
                : SessionContextPinStatus.Current;
        string text = Encoding.UTF8.GetString(source);
        MaterializedPin limited = FromText(text, byteLimit);
        return new(limited.Status == SessionContextPinStatus.Truncated ? limited.Status : freshness, limited.Content,
            freshness == SessionContextPinStatus.Modified ? $"Content changed; current sha256={hash}." : $"sha256={hash}.");
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

    private static MaterializedPin MaterializeSymbolRange(
        SessionContextPinRecord pin, string? workingDirectory, int byteLimit)
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
        if (!File.Exists(path))
        {
            return new(SessionContextPinStatus.Missing, null, "File no longer exists.");
        }
        string[] lines = File.ReadAllLines(path);
        string selected = string.Join('\n', lines.Skip(start - 1).Take(end - start + 1));
        return FromText(selected, byteLimit);
    }

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
        return FromText(Encoding.UTF8.GetString(source.Span), byteLimit);
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
        string? Diagnostic);
}
