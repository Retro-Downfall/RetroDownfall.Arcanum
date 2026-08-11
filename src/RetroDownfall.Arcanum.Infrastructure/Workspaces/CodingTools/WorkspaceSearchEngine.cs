using System.Buffers;

using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal enum WorkspaceSearchMode
{
    Literal,
    Regex,
}

internal enum WorkspaceSearchPhase
{
    Traversal,
    GlobMatch,
    Read,
    Decode,
    LiteralMatch,
    RegexMatch,
}

internal interface IWorkspaceSearchProgressObserver
{
    void OnCheckpoint(WorkspaceSearchPhase phase);
}

internal sealed record WorkspaceSearchRequest
{

    internal required string Pattern { get; init; }

    internal required WorkspaceSearchMode Mode { get; init; }

    internal required bool CaseSensitive { get; init; }

    internal string? Root { get; init; }

    internal IReadOnlyList<string> Globs { get; init; } = [];

    internal IReadOnlyList<string> Extensions { get; init; } = [];

    internal string? Cursor { get; init; }

}

/// <summary>
/// Exact, cancellable, in-process workspace text search. Files stream through deterministic
/// traversal and results use continuation pages; each logical line is matched independently.
/// </summary>
internal sealed class WorkspaceSearchEngine
{

    private const int ReadBufferSize = 64 * 1024;

    private const int ResultPageSize = 256;

    private static ReadOnlySpan<byte> Utf8Preamble => [0xEF, 0xBB, 0xBF];

    private readonly int _maxPatternChars;

    private readonly int _regexTimeoutMilliseconds;

    private readonly int _maxPreviewChars;

    private readonly IWorkspaceSearchProgressObserver? _progressObserver;

    private readonly IWorkspaceSearchLineSpillObserver? _spillObserver;

    internal WorkspaceSearchEngine(
        WorkspaceSearchSettings settings,
        IWorkspaceSearchProgressObserver? progressObserver = null,
        IWorkspaceSearchLineSpillObserver? spillObserver = null)
    {

        ArgumentNullException.ThrowIfNull(settings);

        _maxPatternChars = ArcanumSettingClamps.WorkspaceSearchMaxPatternChars(
            settings.MaxPatternChars);

        _regexTimeoutMilliseconds =
            ArcanumSettingClamps.WorkspaceSearchRegexTimeoutMilliseconds(
                settings.RegexTimeoutMilliseconds);

        _maxPreviewChars =
            ArcanumSettingClamps.WorkspaceSearchMaxPreviewChars(
                settings.MaxPreviewChars);

        _progressObserver = progressObserver;

        _spillObserver = spillObserver;

    }

    internal async Task<WorkspaceSearchToolResultEnvelope> SearchAsync(
        string workspaceRoot,
        WorkspaceSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        void Checkpoint()
        {

            cancellationToken.ThrowIfCancellationRequested();

        }

        byte[] queryFingerprint =
            WorkspaceSearchContinuationCursor.CreateQueryFingerprint(
                request,
                cancellationToken);

        WorkspaceSearchContinuationDecodeResult cursorDecodeResult =
            WorkspaceSearchContinuationCursor.TryDecode(
                request.Cursor,
                queryFingerprint,
                out WorkspaceSearchMatchCheckpoint? continuationCheckpoint);

        SearchState state = new(
            request.Cursor,
            queryFingerprint,
            continuationCheckpoint,
            ResultPageSize);

        Checkpoint();

        if (cursorDecodeResult != WorkspaceSearchContinuationDecodeResult.Success)
        {

            return state.Build(
                status: "invalid_cursor",
                code: cursorDecodeResult
                    == WorkspaceSearchContinuationDecodeResult.QueryMismatch
                    ? "continuation_query_mismatch"
                    : "continuation_invalid",
                message: cursorDecodeResult
                    == WorkspaceSearchContinuationDecodeResult.QueryMismatch
                    ? "The continuation cursor belongs to different search arguments. Restart with cursor omitted."
                    : "The workspace-search continuation cursor is invalid. Restart with cursor omitted.");

        }

        if (string.IsNullOrEmpty(request.Pattern))
        {

            return state.Build(
                status: "invalid_pattern",
                code: "invalid_pattern",
                message: "The search pattern must be non-empty.");

        }

        if (request.Pattern.Length > _maxPatternChars)
        {

            return state.Build(
                status: "invalid_pattern",
                code: "pattern_too_long",
                message: $"The search pattern exceeds the {_maxPatternChars} character limit.");

        }

        string root = Path.GetFullPath(workspaceRoot);

        Checkpoint();

        if (!TryResolveSearchRoot(
                root,
                request.Root,
                out string? searchRoot,
                out string? rootPrefix,
                Checkpoint))
        {

            return state.Build(
                status: "invalid_request",
                code: "invalid_root",
                message: "The requested search root is not a contained workspace directory.");

        }

        if (!TryBuildGlobs(
                request.Globs,
                cancellationToken,
                out WorkspacePathGlob[]? globs)
            || !TryBuildExtensions(
                request.Extensions,
                cancellationToken,
                out HashSet<string>? extensions))
        {

            cancellationToken.ThrowIfCancellationRequested();

            return state.Build(
                status: "invalid_request",
                code: "invalid_filter",
                message: "Search globs and extensions must be bounded normalized relative filters.");

        }

        Regex? regex = null;

        if (request.Mode == WorkspaceSearchMode.Regex)
        {

            RuntimeWorkspaceRegexCreationResult creation =
                RuntimeWorkspaceRegexFactory.Create(
                    request.Pattern,
                    request.CaseSensitive,
                    TimeSpan.FromMilliseconds(_regexTimeoutMilliseconds));

            if (!creation.Success)
            {

                Checkpoint();

                return state.Build(
                    status: "invalid_pattern",
                    code: creation.ErrorCode ?? "invalid_pattern",
                    message: "The regex pattern is invalid.");

            }

            regex = creation.Regex;

            state.RegexEngine = creation.Engine switch
            {
                RuntimeWorkspaceRegexEngine.NonBacktracking => "non_backtracking",
                RuntimeWorkspaceRegexEngine.Interpreted => "interpreted",
                _ => null,
            };

            Checkpoint();

        }

        bool TraversalShouldStop()
        {

            _progressObserver?.OnCheckpoint(WorkspaceSearchPhase.Traversal);

            cancellationToken.ThrowIfCancellationRequested();

            return false;

        }

        void FilterCheckpoint()
        {

            _progressObserver?.OnCheckpoint(
                WorkspaceSearchPhase.GlobMatch);

            Checkpoint();

        }

        bool IncludeTraversalFile(string relativePath) =>
            MatchesFilters(
                relativePath,
                globs,
                extensions,
                FilterCheckpoint);

        bool TraverseFilteredDirectory(string relativePath) =>
            globs.Length == 0
            || globs.Any(
                glob => glob.CanMatchDescendant(
                    relativePath,
                    FilterCheckpoint));

        WorkspaceTraversalStream traversal = DeterministicWorkspaceTraversal.Stream(
            searchRoot,
            cancellationToken: cancellationToken,
            shouldStop: TraversalShouldStop,
            includeFile: IncludeTraversalFile,
            traverseDirectory: TraverseFilteredDirectory);

        void CaptureTraversalState()
        {

            state.TraversalSteps = traversal.Steps;

            state.SkippedDirectorySymlinkCount =
                traversal.SkippedDirectorySymlinkCount;

            state.SkippedFilteredFileCount =
                traversal.SkippedFilteredFileCount
                + traversal.SkippedPrunedDirectoryCount;

            state.SkippedUnreadableFileCount +=
                Math.Max(
                    0,
                    traversal.Skipped
                    - traversal.SkippedDirectorySymlinkCount
                    - traversal.SkippedFilteredFileCount
                    - traversal.SkippedPrunedDirectoryCount);

        }

        HashSet<FileHandleIdentity> seenIdentities = [];

        foreach (WorkspaceTraversalFile file in traversal.EnumerateFiles())
        {

            cancellationToken.ThrowIfCancellationRequested();

            state.FilesVisited++;

            string relativeToSearchRoot = file.RelativePath;

            string modelPath = rootPrefix.Length == 0
                ? relativeToSearchRoot
                : $"{rootPrefix}/{relativeToSearchRoot}";

            string absolutePath = Path.GetFullPath(
                Path.Combine(
                    searchRoot,
                    relativeToSearchRoot.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));

            Checkpoint();

            if (file.IsSymbolicLink
                && !IsContainedFileSymlink(
                    root,
                    absolutePath,
                    Checkpoint))
            {

                state.SkippedEscapingSymlinkCount++;

                continue;

            }

            if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                    root,
                    absolutePath,
                    out string? resolvedPath))
            {

                if (file.IsSymbolicLink)
                {

                    state.SkippedEscapingSymlinkCount++;

                }
                else
                {

                    state.SkippedUnreadableFileCount++;

                }

                continue;

            }

            Checkpoint();

            string identityPath = Path.GetFullPath(resolvedPath ?? absolutePath);

            if (!FileHandleIdentityInterop.TryGetPathMetadata(
                    identityPath,
                    out FileHandleMetadata pathMetadata)
                || pathMetadata.Kind != FileSystemObjectKind.RegularFile
                || pathMetadata.HardLinkCount != 1)
            {

                state.SkippedUnreadableFileCount++;

                continue;

            }

            Checkpoint();

            if (!seenIdentities.Add(pathMetadata.Identity))
            {

                state.SkippedDuplicateFileCount++;

                continue;

            }

            if (!SandboxedFileIo.TryOpenForRead(
                    root,
                    identityPath,
                    out FileStream? stream,
                    out _))
            {

                if (file.IsSymbolicLink
                    && !WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                        root,
                        absolutePath,
                        out _))
                {

                    state.SkippedEscapingSymlinkCount++;

                }
                else
                {

                    state.SkippedUnreadableFileCount++;

                }

                continue;

            }

            Checkpoint();

            SearchState.MatchCheckpoint matchCheckpoint =
                state.CreateMatchCheckpoint();

            try
            {

                await using (stream)
                {

                    if (!TryValidateSearchFile(
                            root,
                            absolutePath,
                            stream,
                            pathMetadata.Identity))
                    {

                        throw new IOException(
                            "The workspace search target is not a stable single-link regular file.");

                    }

                    long openedLength = stream.Length;

                    Checkpoint();

                    SkipUtf8Preamble(stream);

                    using StreamReader reader = new(
                        stream,
                        new System.Text.UTF8Encoding(
                            encoderShouldEmitUTF8Identifier: false,
                            throwOnInvalidBytes: true),
                        detectEncodingFromByteOrderMarks: false,
                        bufferSize: ReadBufferSize,
                        leaveOpen: true);

                    byte[] pathHash =
                        WorkspaceSearchContinuationCursor.CreatePathHash(modelPath);

                    bool pageReady = await SearchDecodedLinesAsync(
                            reader,
                            request,
                            state,
                            modelPath,
                            pathHash,
                            regex,
                            cancellationToken)
                        .ConfigureAwait(false);

                    long bytesReadForFile = Math.Min(stream.Position, openedLength);

                    if (!TryValidateSearchFile(
                            root,
                            absolutePath,
                            stream,
                            pathMetadata.Identity))
                    {

                        throw new IOException(
                            "The workspace search target changed type, identity, or link count while being read.");

                    }

                    state.BytesSearched += bytesReadForFile;

                    state.FilesSearched++;

                    if (pageReady)
                    {

                        CaptureTraversalState();

                        return state.BuildPage();

                    }

                }

            }
            catch (OperationCanceledException)
            {

                throw;

            }
            catch (RegexMatchTimeoutException)
            {

                cancellationToken.ThrowIfCancellationRequested();

                state.RestoreMatchCheckpoint(matchCheckpoint);

                CaptureTraversalState();

                return state.Build(
                    status: "timed_out",
                    code: "regex_timeout",
                    message:
                        $"Physical resource protection: regex matching exceeded the {_regexTimeoutMilliseconds} ms per-match limit. Work was not checkpointed; narrow the pattern or use literal mode, then retry.");

            }
            catch (WorkspaceSearchLineSpillException)
            {

                state.RestoreMatchCheckpoint(matchCheckpoint);

                CaptureTraversalState();

                return state.Build(
                    status: "resource_exhausted",
                    code: "line_spill_unavailable",
                    message:
                        "Physical resource protection: an owner-only temporary spill for one oversized logical line could not be created, written, or mapped. Work for the current file was not checkpointed; free temporary-disk space and retry the same search.");

            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or System.Text.DecoderFallbackException)
            {

                state.RestoreMatchCheckpoint(matchCheckpoint);

                if (exception is System.Text.DecoderFallbackException)
                {

                    state.SkippedBinaryFileCount++;

                }
                else
                {

                    state.SkippedUnreadableFileCount++;

                }

                continue;

            }

        }

        Checkpoint();

        CaptureTraversalState();

        if (state.ContinuationCheckpointMissing)
        {

            return state.Build(
                status: "invalid_cursor",
                code: "continuation_checkpoint_missing",
                message:
                    "The workspace changed and the continuation checkpoint no longer exists. Restart with cursor omitted.");

        }

        return state.Build(
            status: state.Matches.Count == 0 && !state.HasContinuation
                ? "no_match"
                : "ok",
            code: null,
            message: null);

    }

    private async Task<bool> SearchDecodedLinesAsync(
        StreamReader reader,
        WorkspaceSearchRequest request,
        SearchState state,
        string modelPath,
        byte[] pathHash,
        Regex? regex,
        CancellationToken cancellationToken)
    {

        char[] readBuffer = ArrayPool<char>.Shared.Rent(ReadBufferSize);

        using WorkspaceSearchLogicalLine line = new(_spillObserver);

        byte[] previousLineContextHash = new byte[32];

        int lineIndex = 0;

        bool skipLeadingLineFeed = false;

        bool ProcessCurrentLine()
        {

            try
            {

                byte[]? lineContextHash = null;

                bool pageReady = line.Read(
                    lineCharacters =>
                    {

                        cancellationToken.ThrowIfCancellationRequested();

                        lineContextHash =
                            WorkspaceSearchContinuationCursor.CreateLineContextHash(
                                previousLineContextHash,
                                lineCharacters);

                        cancellationToken.ThrowIfCancellationRequested();

                        return request.Mode == WorkspaceSearchMode.Literal
                            ? AddLiteralMatches(
                                state,
                                modelPath,
                                pathHash,
                                lineContextHash,
                                lineIndex,
                                lineCharacters,
                                request.Pattern,
                                request.CaseSensitive,
                                cancellationToken,
                                _progressObserver)
                            : AddRegexMatches(
                                state,
                                modelPath,
                                pathHash,
                                lineContextHash,
                                lineIndex,
                                lineCharacters,
                                regex!,
                                cancellationToken,
                                _progressObserver);

                    });

                previousLineContextHash = lineContextHash!;

                lineIndex = checked(lineIndex + 1);

                return pageReady;

            }
            finally
            {

                line.Reset();

            }

        }

        try
        {

            while (true)
            {

                _progressObserver?.OnCheckpoint(WorkspaceSearchPhase.Read);

                cancellationToken.ThrowIfCancellationRequested();

                int charactersRead = await reader
                    .ReadAsync(
                        readBuffer.AsMemory(0, ReadBufferSize),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (charactersRead == 0)
                {

                    break;

                }

                _progressObserver?.OnCheckpoint(WorkspaceSearchPhase.Decode);

                cancellationToken.ThrowIfCancellationRequested();

                int index = 0;

                if (skipLeadingLineFeed)
                {

                    if (readBuffer[0] == '\n')
                    {

                        index = 1;

                    }

                    skipLeadingLineFeed = false;

                }

                int segmentStart = index;

                while (index < charactersRead)
                {

                    char current = readBuffer[index];

                    if (current is not ('\r' or '\n'))
                    {

                        index++;

                        continue;

                    }

                    line.Append(
                        readBuffer.AsSpan(
                            segmentStart,
                            index - segmentStart));

                    if (ProcessCurrentLine())
                    {

                        return true;

                    }

                    if (current == '\r')
                    {

                        if (index + 1 < charactersRead
                            && readBuffer[index + 1] == '\n')
                        {

                            index++;

                        }
                        else if (index + 1 == charactersRead)
                        {

                            skipLeadingLineFeed = true;

                        }

                    }

                    index++;

                    segmentStart = index;

                }

                line.Append(
                    readBuffer.AsSpan(
                        segmentStart,
                        charactersRead - segmentStart));

            }

            return line.Length > 0
                && ProcessCurrentLine();

        }
        finally
        {

            ArrayPool<char>.Shared.Return(readBuffer);

        }

    }

    /// <summary>
    /// Consumes a single leading UTF-8 preamble so the strict UTF-8 decoder never sees it, mirroring
    /// <see cref="WorkspaceTextFile"/>. Byte-order marks are never allowed to substitute a
    /// replacement-fallback encoding for the strict decoder, so UTF-16/UTF-32 input is rejected as binary.
    /// </summary>
    private static void SkipUtf8Preamble(FileStream stream)
    {

        Span<byte> head = stackalloc byte[3];

        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);

        if (read == head.Length && head.SequenceEqual(Utf8Preamble))
        {

            return;

        }

        stream.Position = 0;

    }

    private static bool TryValidateSearchFile(
        string workspaceRoot,
        string absolutePath,
        FileStream stream,
        FileHandleIdentity expectedIdentity)
    {

        if (!FileHandleIdentityInterop.TryGetHandleMetadata(
                stream.SafeFileHandle,
                out FileHandleMetadata handleMetadata)
            || handleMetadata.Kind != FileSystemObjectKind.RegularFile
            || handleMetadata.HardLinkCount != 1
            || !FileHandleIdentity.IdentitiesMatch(
                expectedIdentity,
                handleMetadata.Identity)
            || !WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                workspaceRoot,
                absolutePath,
                out string? resolvedPath))
        {

            return false;

        }

        string identityPath = Path.GetFullPath(resolvedPath ?? absolutePath);

        return FileHandleIdentityInterop.TryGetPathMetadata(
                identityPath,
                out FileHandleMetadata pathMetadata)
            && pathMetadata.Kind == FileSystemObjectKind.RegularFile
            && pathMetadata.HardLinkCount == 1
            && FileHandleIdentity.IdentitiesMatch(
                expectedIdentity,
                pathMetadata.Identity);

    }

    private bool AddLiteralMatches(
        SearchState state,
        string path,
        byte[] pathHash,
        byte[] lineContextHash,
        int zeroBasedLine,
        ReadOnlySpan<char> line,
        string pattern,
        bool caseSensitive,
        CancellationToken cancellationToken,
        IWorkspaceSearchProgressObserver? progressObserver)
    {

        StringComparison comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        int searchFrom = 0;

        while (searchFrom <= line.Length - pattern.Length)
        {

            progressObserver?.OnCheckpoint(WorkspaceSearchPhase.LiteralMatch);

            cancellationToken.ThrowIfCancellationRequested();

            int scanLength = Math.Min(
                ReadBufferSize + pattern.Length - 1,
                line.Length - searchFrom);
            int relativeIndex = line
                .Slice(searchFrom, scanLength)
                .IndexOf(pattern.AsSpan(), comparison);

            int index = relativeIndex < 0
                ? -1
                : searchFrom + relativeIndex;

            if (index < 0)
            {

                searchFrom += Math.Min(ReadBufferSize, scanLength);

                continue;

            }

            if (state.RecordMatch(
                    new WorkspaceSearchToolResultItem(
                        path,
                        zeroBasedLine + 1,
                        index + 1,
                        CreatePreview(line, index, pattern.Length)),
                    pathHash,
                    lineContextHash,
                    pattern.Length))
            {

                return true;

            }

            searchFrom = index + pattern.Length;

        }

        return false;

    }

    private bool AddRegexMatches(
        SearchState state,
        string path,
        byte[] pathHash,
        byte[] lineContextHash,
        int zeroBasedLine,
        ReadOnlySpan<char> line,
        Regex reusableRegex,
        CancellationToken cancellationToken,
        IWorkspaceSearchProgressObserver? progressObserver)
    {

        void PrepareContinuationOperation()
        {

            progressObserver?.OnCheckpoint(WorkspaceSearchPhase.RegexMatch);

            cancellationToken.ThrowIfCancellationRequested();

        }

        Regex.ValueMatchEnumerator matches = reusableRegex.EnumerateMatches(line);

        while (true)
        {

            PrepareContinuationOperation();

            bool hasMatch = matches.MoveNext();

            cancellationToken.ThrowIfCancellationRequested();

            if (!hasMatch)
            {

                break;

            }

            ValueMatch match = matches.Current;

            if (state.RecordMatch(
                    new WorkspaceSearchToolResultItem(
                        path,
                        zeroBasedLine + 1,
                        match.Index + 1,
                        CreatePreview(line, match.Index, match.Length)),
                    pathHash,
                    lineContextHash,
                    match.Length))
            {

                return true;

            }

        }

        return false;

    }

    private string CreatePreview(
        ReadOnlySpan<char> line,
        int matchIndex,
        int matchLength)
    {

        if (line.Length <= _maxPreviewChars)
        {

            return line.ToString();

        }

        int desiredStart = matchIndex - Math.Max(1, (_maxPreviewChars - matchLength) / 2);
        int start = Math.Clamp(desiredStart, 0, line.Length - _maxPreviewChars);

        if (start > 0 && char.IsLowSurrogate(line[start]))
        {

            start++;

        }

        // The budget is measured from the adjusted start, so stepping past a split pair never costs a
        // character of the preview.
        int end = Math.Min(line.Length, start + _maxPreviewChars);

        if (end < line.Length && end > start && char.IsHighSurrogate(line[end - 1]))
        {

            end--;

        }

        ReadOnlySpan<char> window = line[start..end];
        bool leading = start > 0;
        bool trailing = end < line.Length;

        // Each ellipsis replaces a whole code point: taking a single char would strand the other half
        // of an astral pair and emit an unpaired surrogate.
        if (leading && window.Length > 0)
        {

            window = window[LeadingCodePointLength(window)..];

        }

        if (trailing && window.Length > 0)
        {

            window = window[..^TrailingCodePointLength(window)];

        }

        return leading
            ? trailing
                ? $"…{window}…"
                : $"…{window}"
            : trailing
                ? $"{window}…"
                : window.ToString();

    }

    private static int LeadingCodePointLength(
        ReadOnlySpan<char> value) =>
        value.Length > 1
        && char.IsHighSurrogate(value[0])
        && char.IsLowSurrogate(value[1])
            ? 2
            : 1;

    private static int TrailingCodePointLength(
        ReadOnlySpan<char> value) =>
        value.Length > 1
        && char.IsLowSurrogate(value[^1])
        && char.IsHighSurrogate(value[^2])
            ? 2
            : 1;

    private static bool MatchesFilters(
        string relativePath,
        IReadOnlyList<WorkspacePathGlob> globs,
        IReadOnlySet<string> extensions,
        Action checkpoint)
    {

        bool anyGlobMatched = globs.Count == 0;

        foreach (WorkspacePathGlob glob in globs)
        {

            checkpoint();

            if (glob.IsMatch(relativePath, checkpoint))
            {

                anyGlobMatched = true;

                break;

            }

        }

        if (!anyGlobMatched)
        {

            return false;

        }

        return extensions.Count == 0
            || extensions.Contains(Path.GetExtension(relativePath));

    }

    private bool TryBuildGlobs(
        IReadOnlyList<string>? patterns,
        CancellationToken cancellationToken,
        out WorkspacePathGlob[] globs)
    {

        globs = [];

        if (patterns is null || patterns.Count == 0)
        {

            return true;

        }

        List<WorkspacePathGlob> built = new(patterns.Count);

        foreach (string pattern in patterns)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (pattern is null
                || pattern.Length > _maxPatternChars
                || !WorkspacePathGlob.TryCreate(pattern, out WorkspacePathGlob? glob))
            {

                return false;

            }

            built.Add(glob);

            cancellationToken.ThrowIfCancellationRequested();

        }

        globs = built.ToArray();

        return true;

    }

    private bool TryBuildExtensions(
        IReadOnlyList<string>? requested,
        CancellationToken cancellationToken,
        out HashSet<string> extensions)
    {

        extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (requested is null || requested.Count == 0)
        {

            return true;

        }

        foreach (string value in requested)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(value))
            {

                return false;

            }

            string extension = value.Trim();

            if (extension.Length > _maxPatternChars
                || extension.IndexOfAny(
                    ['/', '\\', '\0', '*', '?', '[', ']', ':']) >= 0)
            {

                return false;

            }

            if (!extension.StartsWith('.'))
            {

                extension = $".{extension}";

            }

            if (extension.Length == 1)
            {

                return false;

            }

            extensions.Add(extension);

            cancellationToken.ThrowIfCancellationRequested();

        }

        return true;

    }

    private static bool TryResolveSearchRoot(
        string workspaceRoot,
        string? requestedRoot,
        out string searchRoot,
        out string rootPrefix,
        Action checkpoint)
    {

        checkpoint();

        searchRoot = workspaceRoot;
        rootPrefix = string.Empty;

        if (requestedRoot is null)
        {

            bool exists = Directory.Exists(workspaceRoot);
            checkpoint();

            return exists;

        }

        if (string.IsNullOrWhiteSpace(requestedRoot))
        {

            return false;

        }

        string trimmed = requestedRoot.Trim();

        if (trimmed is "." or "./" or @".\")
        {

            bool exists = Directory.Exists(workspaceRoot);
            checkpoint();

            return exists;

        }

        if (!WorkspaceRelativePath.TryResolve(
                workspaceRoot,
                trimmed,
                out string? resolved,
                out string? normalized)
            || !Directory.Exists(resolved)
            || HasDirectorySymlinkComponent(
                workspaceRoot,
                normalized,
                checkpoint))
        {

            return false;

        }

        if (!WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(
                workspaceRoot,
                resolved,
                out _))
        {

            return false;

        }

        checkpoint();

        searchRoot = resolved;
        rootPrefix = normalized;

        return true;

    }

    private static bool HasDirectorySymlinkComponent(
        string workspaceRoot,
        string normalizedRelativePath,
        Action checkpoint)
    {

        string current = workspaceRoot;

        try
        {

            foreach (string segment in normalizedRelativePath.Split('/'))
            {

                checkpoint();
                current = Path.Combine(current, segment);

                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {

                    return true;

                }

                checkpoint();

            }

            return false;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or FileNotFoundException)
        {

            return true;

        }

    }

    private static bool IsContainedFileSymlink(
        string workspaceRoot,
        string symbolicLinkPath,
        Action checkpoint)
    {

        try
        {

            checkpoint();

            FileSystemInfo? target = File.ResolveLinkTarget(
                symbolicLinkPath,
                returnFinalTarget: true);

            bool contained = target is not null
                && WorkspacePathPolicy.IsPathUnderWorkspace(
                    workspaceRoot,
                    Path.GetFullPath(target.FullName));

            checkpoint();

            return contained;

        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {

            return false;

        }

    }

    private sealed class SearchState
    {

        private readonly string? _pageStartCursor;

        private readonly byte[] _queryFingerprint;

        private readonly WorkspaceSearchMatchCheckpoint? _continuationCheckpoint;

        private readonly int _pageSize;

        private long _observedMatchCount;

        private long _eligibleMatchCount;

        private bool _continuationCheckpointFound;

        internal SearchState(
            string? pageStartCursor,
            byte[] queryFingerprint,
            WorkspaceSearchMatchCheckpoint? continuationCheckpoint,
            int pageSize)
        {

            _pageStartCursor = pageStartCursor;

            _queryFingerprint = queryFingerprint;

            _continuationCheckpoint = continuationCheckpoint;

            _pageSize = pageSize;

            _continuationCheckpointFound = continuationCheckpoint is null;

        }

        internal List<WorkspaceSearchToolResultItem> Matches { get; } = [];

        internal List<string> MatchCursors { get; } = [];

        internal bool HasContinuation => _continuationCheckpoint is not null;

        internal bool ContinuationCheckpointMissing =>
            _continuationCheckpoint is not null
            && !_continuationCheckpointFound;

        internal string? RegexEngine { get; set; }

        internal int FilesVisited { get; set; }

        internal int FilesSearched { get; set; }

        internal long BytesSearched { get; set; }

        internal int TraversalSteps { get; set; }

        internal int SkippedDirectorySymlinkCount { get; set; }

        internal int SkippedEscapingSymlinkCount { get; set; }

        internal int SkippedDuplicateFileCount { get; set; }

        internal int SkippedBinaryFileCount { get; set; }

        internal int SkippedFilteredFileCount { get; set; }

        internal int SkippedUnreadableFileCount { get; set; }

        internal bool RecordMatch(
            WorkspaceSearchToolResultItem match,
            ReadOnlySpan<byte> pathHash,
            ReadOnlySpan<byte> lineContextHash,
            int matchLength)
        {

            _observedMatchCount = checked(_observedMatchCount + 1);

            if (!_continuationCheckpointFound)
            {

                if (_continuationCheckpoint!.Matches(
                        pathHash,
                        lineContextHash,
                        match.Column,
                        matchLength))
                {

                    _continuationCheckpointFound = true;

                }

                return false;

            }

            _eligibleMatchCount = checked(_eligibleMatchCount + 1);

            if (Matches.Count >= _pageSize)
            {

                return true;

            }

            Matches.Add(match);

            MatchCursors.Add(
                WorkspaceSearchContinuationCursor.Encode(
                    _queryFingerprint,
                    pathHash,
                    lineContextHash,
                    match.Column,
                    matchLength));

            return false;

        }

        internal readonly record struct MatchCheckpoint(
            int MatchCount,
            int MatchCursorCount,
            long ObservedMatchCount,
            long EligibleMatchCount,
            bool ContinuationCheckpointFound);

        internal MatchCheckpoint CreateMatchCheckpoint() =>
            new(
                Matches.Count,
                MatchCursors.Count,
                _observedMatchCount,
                _eligibleMatchCount,
                _continuationCheckpointFound);

        internal void RestoreMatchCheckpoint(MatchCheckpoint checkpoint)
        {

            if (Matches.Count > checkpoint.MatchCount)
            {

                Matches.RemoveRange(
                    checkpoint.MatchCount,
                    Matches.Count - checkpoint.MatchCount);

            }

            if (MatchCursors.Count > checkpoint.MatchCursorCount)
            {

                MatchCursors.RemoveRange(
                    checkpoint.MatchCursorCount,
                    MatchCursors.Count - checkpoint.MatchCursorCount);

            }

            _observedMatchCount = checkpoint.ObservedMatchCount;

            _eligibleMatchCount = checkpoint.EligibleMatchCount;

            _continuationCheckpointFound = checkpoint.ContinuationCheckpointFound;

        }

        internal WorkspaceSearchToolResultEnvelope BuildPage() =>
            Build(
                status: "ok",
                code: null,
                message: null,
                truncated: true);

        internal WorkspaceSearchToolResultEnvelope Build(
            string status,
            string? code,
            string? message,
            bool truncated = false) =>
            new()
            {
                Status = status,
                Code = code,
                Message = message,
                Matches = Matches.ToArray(),
                PageStartCursor = _pageStartCursor,
                MatchCursors = MatchCursors.ToArray(),
                NextCursor = truncated
                    ? MatchCursors[^1]
                    : null,
                ContinuationAction = truncated
                    ? "Call search_workspace again with cursor set to nextCursor and the same search arguments."
                    : null,
                TotalMatchCount = checked((int)Math.Min(int.MaxValue, _observedMatchCount)),
                OmittedMatchCount = truncated
                    ? checked(
                        (int)Math.Min(
                                int.MaxValue,
                                Math.Max(
                                    0,
                                    _eligibleMatchCount
                                    - Matches.Count)))
                    : 0,
                RegexEngine = RegexEngine,
                FilesVisited = FilesVisited,
                FilesSearched = FilesSearched,
                BytesSearched = BytesSearched,
                TraversalSteps = TraversalSteps,
                SkippedDirectorySymlinkCount = SkippedDirectorySymlinkCount,
                SkippedEscapingSymlinkCount = SkippedEscapingSymlinkCount,
                SkippedDuplicateFileCount = SkippedDuplicateFileCount,
                SkippedBinaryFileCount = SkippedBinaryFileCount,
                SkippedFilteredFileCount = SkippedFilteredFileCount,
                SkippedUnreadableFileCount = SkippedUnreadableFileCount,
                Truncated = truncated,
            };

    }

}
