using System.Globalization;

using System.Security.Cryptography;

using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Logging;

internal sealed record AuditLogFileSnapshot(
    string Path,
    int DateStamp,
    long Length);

internal sealed record AuditLogFileIdentity(
    int PrefixLength,
    byte[] Hash);

internal static class AuditLogPageReader
{

    internal const int FileIdentityPrefixBytes = 4 * 1024;

    private const string InvalidCursorMessage =
        "The audit cursor is malformed, belongs to a different query, or no longer references retained log data. Restart without 'cursor', then continue with the returned X-Arcanum-Next-Cursor value.";

    internal static async Task<Result<AuditQueryPage<T>>> QueryAsync<T>(
        string directory,
        string stem,
        string family,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        string? cursor,
        JsonTypeInfo<T> jsonTypeInfo,
        Func<T, string?> timestampSelector,
        Func<T, bool> matches,
        ILogger logger,
        CancellationToken cancellationToken,
        params string?[] queryFilters)
    {

        string fileFamilyIdentity = Path.GetFullPath(Path.Combine(directory, stem));

        byte[] queryFingerprint = AuditLogCursorCodec.CreateQueryFingerprint(
            family,
            fileFamilyIdentity,
            from,
            to,
            queryFilters);

        AuditLogCursorDecodeResult decodeResult = AuditLogCursorCodec.TryDecode(
            cursor,
            queryFingerprint,
            out AuditLogCursorCheckpoint? checkpoint);

        if (decodeResult is not AuditLogCursorDecodeResult.Success)
        {

            return InvalidCursor<T>();

        }

        DateTimeOffset effectiveTo = checkpoint?.SnapshotTo
            ?? to
            ?? DateTimeOffset.UtcNow;

        DateTimeOffset effectiveFrom = from ?? DateTimeOffset.MinValue;

        IReadOnlyList<AuditLogFileSnapshot> files;

        try
        {

            files = EnumerateDatedLogFiles(
                directory,
                stem,
                effectiveFrom,
                effectiveTo);

        }
        catch (IOException ex)
        {

            logger.LogDebug(ex, "Could not enumerate {AuditFamily} audit logs for this query.", family);

            return checkpoint is null
                ? Result<AuditQueryPage<T>>.Success(new AuditQueryPage<T>([], null))
                : InvalidCursor<T>();

        }
        catch (UnauthorizedAccessException ex)
        {

            logger.LogDebug(ex, "Could not enumerate {AuditFamily} audit logs for this query.", family);

            return checkpoint is null
                ? Result<AuditQueryPage<T>>.Success(new AuditQueryPage<T>([], null))
                : InvalidCursor<T>();

        }

        if (files.Count == 0)
        {

            return checkpoint is null
                ? Result<AuditQueryPage<T>>.Success(new AuditQueryPage<T>([], null))
                : InvalidCursor<T>();

        }

        int startFileIndex = 0;

        long startBoundary = files[0].Length;

        if (checkpoint is not null)
        {

            startFileIndex = FindFile(files, checkpoint.FileDateStamp);

            if (startFileIndex < 0
                || checkpoint.BoundaryOffset > files[startFileIndex].Length
                || !await MatchesIdentityAsync(
                    files[startFileIndex],
                    checkpoint,
                    cancellationToken).ConfigureAwait(false))
            {

                return InvalidCursor<T>();

            }

            startBoundary = checkpoint.BoundaryOffset;

        }

        int pageSize = Math.Max(1, limit);

        List<T> records = new(pageSize);

        for (int fileIndex = startFileIndex; fileIndex < files.Count; fileIndex++)
        {

            cancellationToken.ThrowIfCancellationRequested();

            AuditLogFileSnapshot file = files[fileIndex];

            long boundary = fileIndex == startFileIndex
                ? startBoundary
                : file.Length;

            if (boundary <= 0)
            {

                continue;

            }

            try
            {

                await using FileStream stream = OpenRead(file.Path);

                await foreach (ReverseJsonlRecord<T> candidate in ReverseJsonlFileReader
                                   .ReadAsync(
                                       stream,
                                       boundary,
                                       jsonTypeInfo,
                                       cancellationToken)
                                   .ConfigureAwait(false))
                {

                    if (!DateTimeOffset.TryParse(
                            timestampSelector(candidate.Value),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,
                            out DateTimeOffset timestamp)
                        || timestamp < effectiveFrom
                        || timestamp > effectiveTo
                        || !matches(candidate.Value))
                    {

                        continue;

                    }

                    records.Add(candidate.Value);

                    if (records.Count < pageSize)
                    {

                        continue;

                    }

                    Result<string?> nextCursor = await CreateContinuationCursorAsync(
                        files,
                        fileIndex,
                        candidate.NextBoundaryOffset,
                        effectiveTo,
                        queryFingerprint,
                        cancellationToken).ConfigureAwait(false);

                    if (nextCursor.IsFailure)
                    {

                        return Result<AuditQueryPage<T>>.Failure(nextCursor.Error);

                    }

                    return Result<AuditQueryPage<T>>.Success(
                        new AuditQueryPage<T>(records, nextCursor.Value));

                }

            }
            catch (IOException ex)
            {

                logger.LogDebug(
                    ex,
                    "Could not read {AuditFamily} audit log file {FilePath} for this query; skipping.",
                    family,
                    file.Path);

                if (checkpoint is not null && fileIndex == startFileIndex)
                {

                    return InvalidCursor<T>();

                }

            }
            catch (UnauthorizedAccessException ex)
            {

                logger.LogDebug(
                    ex,
                    "Could not read {AuditFamily} audit log file {FilePath} for this query; skipping.",
                    family,
                    file.Path);

                if (checkpoint is not null && fileIndex == startFileIndex)
                {

                    return InvalidCursor<T>();

                }

            }

        }

        return Result<AuditQueryPage<T>>.Success(new AuditQueryPage<T>(records, null));

    }

    private static IReadOnlyList<AuditLogFileSnapshot> EnumerateDatedLogFiles(
        string directory,
        string stem,
        DateTimeOffset from,
        DateTimeOffset to)
    {

        string prefix = stem + "-";

        DateTime fromDate = from.UtcDateTime.Date;

        DateTime toDate = to.UtcDateTime.Date;

        List<AuditLogFileSnapshot> files = [];

        foreach (string path in Directory.EnumerateFiles(
                     directory,
                     "*.jsonl",
                     SearchOption.TopDirectoryOnly))
        {

            int? dateStamp = ParseDatedLogFile(path, prefix);

            if (!dateStamp.HasValue
                || !DateTime.TryParseExact(
                    dateStamp.Value.ToString("D8", CultureInfo.InvariantCulture),
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out DateTime fileDate)
                || fileDate < fromDate
                || fileDate > toDate)
            {

                continue;

            }

            try
            {

                files.Add(new AuditLogFileSnapshot(
                    path,
                    dateStamp.Value,
                    new FileInfo(path).Length));

            }
            catch (IOException)
            {

            }
            catch (UnauthorizedAccessException)
            {

            }

        }

        files.Sort(static (left, right) => right.DateStamp.CompareTo(left.DateStamp));

        return files;

    }

    private static int? ParseDatedLogFile(
        string path,
        string prefix)
    {

        string name = Path.GetFileNameWithoutExtension(path);

        return name.StartsWith(prefix, StringComparison.Ordinal)
            && name.Length == prefix.Length + 8
            && int.TryParse(
                name.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int dateStamp)
            ? dateStamp
            : null;

    }

    private static int FindFile(
        IReadOnlyList<AuditLogFileSnapshot> files,
        int dateStamp)
    {

        for (int index = 0; index < files.Count; index++)
        {

            if (files[index].DateStamp == dateStamp)
            {

                return index;

            }

        }

        return -1;

    }

    private static async Task<Result<string?>> CreateContinuationCursorAsync(
        IReadOnlyList<AuditLogFileSnapshot> files,
        int currentFileIndex,
        long currentBoundary,
        DateTimeOffset snapshotTo,
        ReadOnlyMemory<byte> queryFingerprint,
        CancellationToken cancellationToken)
    {

        int targetIndex = currentFileIndex;

        long targetBoundary = currentBoundary;

        while (targetBoundary <= 0)
        {

            targetIndex++;

            if (targetIndex >= files.Count)
            {

                return Result<string?>.Success(null);

            }

            targetBoundary = files[targetIndex].Length;

        }

        AuditLogFileSnapshot target = files[targetIndex];

        AuditLogFileIdentity? identity = await ReadIdentityAsync(
            target,
            expectedPrefixLength: null,
            cancellationToken).ConfigureAwait(false);

        if (identity is null)
        {

            return Result<string?>.Failure(
                new Error(
                    ErrorCodes.Hub.Error,
                    "The next audit page could not be anchored because its retained log file changed during the read. Retry this page without changing data, or restart without 'cursor'."));

        }

        return Result<string?>.Success(
            AuditLogCursorCodec.Encode(
                queryFingerprint.Span,
                new AuditLogCursorCheckpoint(
                    snapshotTo,
                    target.DateStamp,
                    targetBoundary,
                    identity.PrefixLength,
                    identity.Hash)));

    }

    private static async Task<bool> MatchesIdentityAsync(
        AuditLogFileSnapshot file,
        AuditLogCursorCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {

        AuditLogFileIdentity? actual = await ReadIdentityAsync(
            file,
            checkpoint.IdentityPrefixLength,
            cancellationToken).ConfigureAwait(false);

        return actual is not null
            && CryptographicOperations.FixedTimeEquals(
                actual.Hash,
                checkpoint.IdentityHash);

    }

    private static async Task<AuditLogFileIdentity?> ReadIdentityAsync(
        AuditLogFileSnapshot file,
        int? expectedPrefixLength,
        CancellationToken cancellationToken)
    {

        try
        {

            await using FileStream stream = OpenRead(file.Path);

            int prefixLength = expectedPrefixLength
                ?? checked((int)Math.Min(file.Length, FileIdentityPrefixBytes));

            if (prefixLength <= 0 || stream.Length < prefixLength)
            {

                return null;

            }

            byte[] prefix = new byte[prefixLength];

            await stream
                .ReadExactlyAsync(prefix, cancellationToken)
                .ConfigureAwait(false);

            return new AuditLogFileIdentity(
                prefixLength,
                SHA256.HashData(prefix));

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

    private static FileStream OpenRead(string path) =>
        new(
            path,
            new FileStreamOptions
            {

                Mode = FileMode.Open,

                Access = FileAccess.Read,

                Share = FileShare.ReadWrite | FileShare.Delete,

                BufferSize = ReverseJsonlFileReader.ScanBufferBytes,

                Options = FileOptions.Asynchronous | FileOptions.RandomAccess,

            });

    private static Result<AuditQueryPage<T>> InvalidCursor<T>() =>
        Result<AuditQueryPage<T>>.Failure(
            new Error(
                ErrorCodes.Validation.InvalidQuery,
                InvalidCursorMessage));

}
