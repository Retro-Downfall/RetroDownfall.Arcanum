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

}

/// <summary>
/// Exact, bounded, in-process workspace text search. Files and matches are processed in
/// deterministic order, and each logical line is matched independently.
/// </summary>
internal sealed class WorkspaceSearchEngine
{

    private const int MaxFiltersPerKind = 64;

    private const int ReadBufferSize = 64 * 1024;

    private readonly int _maxPatternChars;

    private readonly int _regexTimeoutMilliseconds;

    private readonly int _maxElapsedMilliseconds;

    private readonly int _maxFiles;

    private readonly long _maxBytes;

    private readonly int _maxTraversalSteps;

    private readonly int _maxMatches;

    private readonly int _maxPreviewChars;

    private readonly TimeProvider _timeProvider;

    private readonly IWorkspaceSearchProgressObserver? _progressObserver;

    internal WorkspaceSearchEngine(
        WorkspaceSearchSettings settings,
        TimeProvider? timeProvider = null,
        IWorkspaceSearchProgressObserver? progressObserver = null)
    {

        ArgumentNullException.ThrowIfNull(settings);

        _maxPatternChars = ArcanumSettingClamps.WorkspaceSearchMaxPatternChars(
            settings.MaxPatternChars);

        _regexTimeoutMilliseconds =
            ArcanumSettingClamps.WorkspaceSearchRegexTimeoutMilliseconds(
                settings.RegexTimeoutMilliseconds);

        _maxElapsedMilliseconds =
            ArcanumSettingClamps.WorkspaceSearchMaxElapsedMilliseconds(
                settings.MaxElapsedMilliseconds);

        _maxFiles = ArcanumSettingClamps.WorkspaceSearchMaxFiles(
            settings.MaxFiles);

        _maxBytes = ArcanumSettingClamps.WorkspaceSearchMaxBytes(
            settings.MaxBytes);

        _maxTraversalSteps =
            ArcanumSettingClamps.WorkspaceSearchMaxTraversalSteps(
                settings.MaxTraversalSteps);

        _maxMatches = ArcanumSettingClamps.WorkspaceSearchMaxMatches(
            settings.MaxMatches);

        _maxPreviewChars =
            ArcanumSettingClamps.WorkspaceSearchMaxPreviewChars(
                settings.MaxPreviewChars);

        _timeProvider = timeProvider ?? TimeProvider.System;
        _progressObserver = progressObserver;

    }

    internal async Task<WorkspaceSearchToolResultEnvelope> SearchAsync(
        string workspaceRoot,
        WorkspaceSearchRequest request,
        CancellationToken cancellationToken)
    {

        long startedAt = _timeProvider.GetTimestamp();

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        bool ElapsedLimitReached() =>
            _timeProvider.GetElapsedTime(startedAt)
                >= TimeSpan.FromMilliseconds(_maxElapsedMilliseconds);

        TimeSpan RemainingElapsed()
        {
            TimeSpan remaining =
                TimeSpan.FromMilliseconds(_maxElapsedMilliseconds)
                - _timeProvider.GetElapsedTime(startedAt);

            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        void ThrowIfCancelledOrElapsed()
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ElapsedLimitReached())
            {

                throw new WorkspaceSearchElapsedException();

            }
        }

        SearchState state = new();

        try
        {

            ThrowIfCancelledOrElapsed();

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

        ThrowIfCancelledOrElapsed();

        if (!TryResolveSearchRoot(
                root,
                request.Root,
                out string? searchRoot,
                out string? rootPrefix,
                ThrowIfCancelledOrElapsed))
        {

            return state.Build(
                status: "invalid_request",
                code: "invalid_root",
                message: "The requested search root is not a contained workspace directory.");

        }

        if (!TryBuildGlobs(
                request.Globs,
                cancellationToken,
                ElapsedLimitReached,
                out WorkspacePathGlob[]? globs)
            || !TryBuildExtensions(
                request.Extensions,
                cancellationToken,
                ElapsedLimitReached,
                out HashSet<string>? extensions))
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (ElapsedLimitReached())
            {

                return state.BuildCapped("max_elapsed");

            }

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

                ThrowIfCancelledOrElapsed();

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

            ThrowIfCancelledOrElapsed();

        }

        bool TraversalShouldStop()
        {
            _progressObserver?.OnCheckpoint(WorkspaceSearchPhase.Traversal);
            cancellationToken.ThrowIfCancellationRequested();

            return ElapsedLimitReached();
        }

        void FilterCheckpoint()
        {

            _progressObserver?.OnCheckpoint(
                WorkspaceSearchPhase.GlobMatch);

            ThrowIfCancelledOrElapsed();

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

        WorkspaceTraversalResult traversal = DeterministicWorkspaceTraversal.Traverse(
            searchRoot,
            new WorkspaceTraversalLimits(
                MaxSteps: _maxTraversalSteps,
                MaxFiles: _maxFiles),
            cancellationToken: cancellationToken,
            shouldStop: TraversalShouldStop,
            includeFile: IncludeTraversalFile,
            traverseDirectory: TraverseFilteredDirectory);

        state.TraversalSteps = traversal.Steps;
        state.SkippedDirectorySymlinkCount =
            traversal.SkippedDirectorySymlinkCount;
        state.SkippedFilteredFileCount =
            traversal.SkippedFilteredFileCount
            + traversal.SkippedPrunedDirectoryCount;
        state.SkippedUnreadableFileCount =
            Math.Max(
                0,
                traversal.Skipped
                - traversal.SkippedDirectorySymlinkCount
                - traversal.SkippedFilteredFileCount
                - traversal.SkippedPrunedDirectoryCount);

        if (traversal.StopRequested)
        {

            return state.BuildCapped("max_elapsed");

        }

        HashSet<FileHandleIdentity> seenIdentities = [];

        foreach (WorkspaceTraversalFile file in traversal.Files)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (ElapsedLimitReached())
            {

                return state.BuildCapped("max_elapsed");

            }

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

            ThrowIfCancelledOrElapsed();

            if (file.IsSymbolicLink
                && !IsContainedFileSymlink(
                    root,
                    absolutePath,
                    ThrowIfCancelledOrElapsed))
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

            ThrowIfCancelledOrElapsed();

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

            ThrowIfCancelledOrElapsed();

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

            ThrowIfCancelledOrElapsed();

            byte[] bytes;

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
                    ThrowIfCancelledOrElapsed();

                    if (openedLength < 0
                        || openedLength > int.MaxValue
                        || openedLength > _maxBytes - state.BytesSearched)
                    {

                        return state.BuildCapped("max_bytes");

                    }

                    bytes = new byte[(int)openedLength];
                    ThrowIfCancelledOrElapsed();

                    int bytesRead = 0;

                    while (bytesRead < bytes.Length)
                    {

                        _progressObserver?.OnCheckpoint(WorkspaceSearchPhase.Read);
                        cancellationToken.ThrowIfCancellationRequested();

                        if (ElapsedLimitReached())
                        {

                            state.BytesSearched += bytesRead;

                            return state.BuildCapped("max_elapsed");

                        }

                        int read = await stream.ReadAsync(
                            bytes.AsMemory(
                                bytesRead,
                                Math.Min(ReadBufferSize, bytes.Length - bytesRead)),
                            cancellationToken).ConfigureAwait(false);

                        if (read == 0)
                        {

                            throw new EndOfStreamException(
                                "The workspace file changed while it was being read.");

                        }

                        bytesRead += read;

                    }

                    if (!TryValidateSearchFile(
                            root,
                            absolutePath,
                            stream,
                            pathMetadata.Identity))
                    {

                        throw new IOException(
                            "The workspace search target changed type, identity, or link count while being read.");

                    }

                }

            }
            catch (OperationCanceledException)
            {

                throw;

            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {

                state.SkippedUnreadableFileCount++;

                continue;

            }

            state.BytesSearched += bytes.LongLength;
            ThrowIfCancelledOrElapsed();

            WorkspaceTextFile document;

            try
            {

                document = WorkspaceTextFile.Decode(
                    bytes,
                    () =>
                    {
                        _progressObserver?.OnCheckpoint(
                            WorkspaceSearchPhase.Decode);
                        ThrowIfCancelledOrElapsed();
                    });

            }
            catch (WorkspaceTextDecodingException)
            {

                state.SkippedBinaryFileCount++;

                continue;

            }

            state.FilesSearched++;
            ThrowIfCancelledOrElapsed();

            for (int lineIndex = 0; lineIndex < document.Lines.Count; lineIndex++)
            {

                cancellationToken.ThrowIfCancellationRequested();

                if (ElapsedLimitReached())
                {

                    return state.BuildCapped("max_elapsed");

                }

                WorkspaceTextLine line = document.Lines[lineIndex];

                try
                {

                    bool capped = request.Mode == WorkspaceSearchMode.Literal
                        ? AddLiteralMatches(
                            state,
                            modelPath,
                            lineIndex,
                            line.Text,
                            request.Pattern,
                            request.CaseSensitive,
                            cancellationToken,
                            ElapsedLimitReached,
                            _progressObserver)
                        : AddRegexMatches(
                            state,
                            modelPath,
                            lineIndex,
                            line.Text,
                            regex!,
                            request.Pattern,
                            request.CaseSensitive,
                            cancellationToken,
                            ElapsedLimitReached,
                            RemainingElapsed,
                            _progressObserver);

                    if (capped)
                    {

                        return state.BuildCapped("max_matches");

                    }

                    if (ElapsedLimitReached())
                    {

                        return state.BuildCapped("max_elapsed");

                    }

                }
                catch (RegexMatchTimeoutException)
                {

                    cancellationToken.ThrowIfCancellationRequested();

                    return state.Build(
                        status: "timed_out",
                        code: "regex_timeout",
                        message: "Regex matching exceeded the per-match timeout.");

                }

            }

        }

        if (traversal.StepLimitReached)
        {

            return state.BuildCapped("max_traversal_steps");

        }

        if (traversal.FileLimitReached)
        {

            return state.BuildCapped("max_files");

        }

        ThrowIfCancelledOrElapsed();

        return state.Build(
            status: state.Matches.Count == 0 ? "no_match" : "ok",
            code: null,
            message: null);

        }
        catch (WorkspaceSearchElapsedException)
        {

            return state.BuildCapped("max_elapsed");

        }

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
        int zeroBasedLine,
        string line,
        string pattern,
        bool caseSensitive,
        CancellationToken cancellationToken,
        Func<bool> elapsedLimitReached,
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

            if (elapsedLimitReached())
            {

                return false;

            }

            int scanLength = Math.Min(
                ReadBufferSize + pattern.Length - 1,
                line.Length - searchFrom);
            int index = line.IndexOf(
                pattern,
                searchFrom,
                scanLength,
                comparison);

            if (index < 0)
            {

                searchFrom += Math.Min(ReadBufferSize, scanLength);

                continue;

            }

            state.Matches.Add(
                new WorkspaceSearchToolResultItem(
                    path,
                    zeroBasedLine + 1,
                    index + 1,
                    CreatePreview(line, index, pattern.Length)));

            if (state.Matches.Count >= _maxMatches)
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
        int zeroBasedLine,
        string line,
        Regex reusableRegex,
        string pattern,
        bool caseSensitive,
        CancellationToken cancellationToken,
        Func<bool> elapsedLimitReached,
        Func<TimeSpan> remainingElapsed,
        IWorkspaceSearchProgressObserver? progressObserver)
    {

        long maxContinuationOperations = Math.Min(
            (long)line.Length + 2,
            (long)_maxMatches - state.Matches.Count + 1);
        TimeSpan remaining = remainingElapsed();
        bool deadlineConstrained =
            reusableRegex.MatchTimeout >= remaining;
        Regex continuationRegex = reusableRegex;

        if (remaining <= TimeSpan.Zero)
        {

            throw new WorkspaceSearchElapsedException();

        }

        if (deadlineConstrained)
        {

            if (remaining.Ticks < maxContinuationOperations)
            {

                throw new WorkspaceSearchElapsedException();

            }

            TimeSpan continuationSlice = TimeSpan.FromTicks(
                remaining.Ticks / maxContinuationOperations);
            RuntimeWorkspaceRegexCreationResult temporary =
                RuntimeWorkspaceRegexFactory.Create(
                    pattern,
                    caseSensitive,
                    continuationSlice);

            if (!temporary.Success)
            {

                throw new InvalidOperationException(
                    "A previously validated search regex could not be recreated.");

            }

            continuationRegex = temporary.Regex!;

        }

        void PrepareContinuationOperation()
        {

            progressObserver?.OnCheckpoint(WorkspaceSearchPhase.RegexMatch);
            cancellationToken.ThrowIfCancellationRequested();

            if (elapsedLimitReached()
                || continuationRegex.MatchTimeout >= remainingElapsed())
            {

                throw new WorkspaceSearchElapsedException();

            }

        }

        Match RunContinuationOperation(Func<Match> operation)
        {

            PrepareContinuationOperation();

            try
            {

                Match result = operation();

                cancellationToken.ThrowIfCancellationRequested();

                if (elapsedLimitReached())
                {

                    throw new WorkspaceSearchElapsedException();

                }

                return result;

            }
            catch (RegexMatchTimeoutException) when (deadlineConstrained)
            {

                cancellationToken.ThrowIfCancellationRequested();

                throw new WorkspaceSearchElapsedException();

            }

        }

        Match match = RunContinuationOperation(
            () => continuationRegex.Match(line));

        while (match.Success)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (elapsedLimitReached())
            {

                throw new WorkspaceSearchElapsedException();

            }

            state.Matches.Add(
                new WorkspaceSearchToolResultItem(
                    path,
                    zeroBasedLine + 1,
                    match.Index + 1,
                    CreatePreview(line, match.Index, match.Length)));

            if (state.Matches.Count >= _maxMatches)
            {

                return true;

            }

            Match previous = match;
            match = RunContinuationOperation(previous.NextMatch);

        }

        return false;

    }

    private string CreatePreview(string line, int matchIndex, int matchLength)
    {

        if (line.Length <= _maxPreviewChars)
        {

            return line;

        }

        int desiredStart = matchIndex - Math.Max(1, (_maxPreviewChars - matchLength) / 2);
        int start = Math.Clamp(desiredStart, 0, line.Length - _maxPreviewChars);
        int end = start + _maxPreviewChars;

        if (start > 0 && char.IsLowSurrogate(line[start]))
        {

            start++;

        }

        if (end < line.Length && end > start && char.IsHighSurrogate(line[end - 1]))
        {

            end--;

        }

        string preview = line[start..end];

        if (start > 0 && preview.Length > 0)
        {

            preview = $"…{preview[1..]}";

        }

        if (end < line.Length && preview.Length > 0)
        {

            preview = $"{preview[..^1]}…";

        }

        return preview;

    }

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
        Func<bool> elapsedLimitReached,
        out WorkspacePathGlob[] globs)
    {

        globs = [];

        if (patterns is null || patterns.Count == 0)
        {

            return true;

        }

        if (patterns.Count > MaxFiltersPerKind)
        {

            return false;

        }

        List<WorkspacePathGlob> built = new(patterns.Count);

        foreach (string pattern in patterns)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (elapsedLimitReached())
            {

                return false;

            }

            if (pattern is null
                || pattern.Length > _maxPatternChars
                || !WorkspacePathGlob.TryCreate(pattern, out WorkspacePathGlob? glob))
            {

                return false;

            }

            built.Add(glob);

            cancellationToken.ThrowIfCancellationRequested();

            if (elapsedLimitReached())
            {

                return false;

            }

        }

        globs = built.ToArray();

        return true;

    }

    private bool TryBuildExtensions(
        IReadOnlyList<string>? requested,
        CancellationToken cancellationToken,
        Func<bool> elapsedLimitReached,
        out HashSet<string> extensions)
    {

        extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (requested is null || requested.Count == 0)
        {

            return true;

        }

        if (requested.Count > MaxFiltersPerKind)
        {

            return false;

        }

        foreach (string value in requested)
        {

            cancellationToken.ThrowIfCancellationRequested();

            if (elapsedLimitReached())
            {

                return false;

            }

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

            if (elapsedLimitReached())
            {

                return false;

            }

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

    private sealed class WorkspaceSearchElapsedException : Exception
    {
    }

    private sealed class SearchState
    {

        internal List<WorkspaceSearchToolResultItem> Matches { get; } = [];

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

        internal WorkspaceSearchToolResultEnvelope BuildCapped(string code) =>
            Build(
                status: "capped",
                code,
                message: "Workspace search stopped at a configured resource limit.",
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
                TotalMatchCount = Matches.Count,
                OmittedMatchCount = 0,
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
