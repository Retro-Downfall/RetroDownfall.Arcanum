using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspacePatchPlannedFile(
    string Path,
    UnifiedDiffOperationKind Operation,
    int AppliedHunks,
    int RelocatedHunks,
    IReadOnlyList<int> MatchedLines,
    IReadOnlyList<WorkspacePatchHunkDiagnostic> Hunks);

internal sealed record WorkspacePatchPlan(
    IReadOnlyList<WorkspacePatchPlannedFile> Files,
    IReadOnlyList<WorkspaceFileCommitOperation> CommitOperations,
    IReadOnlyList<string> NormalizedPaths);

internal sealed record WorkspacePatchPlanResult(
    bool Success,
    WorkspacePatchPlan? Plan,
    string? Code,
    string? Message,
    WorkspacePatchHunkDiagnostic? Diagnostic = null);

internal sealed class WorkspacePatchPlannerOptions
{
    internal Action<string>? AfterSourceHandleOpened { get; init; }

    internal Action? ExactMatchCheckpoint { get; init; }

    internal Action? FuzzyMatchCheckpoint { get; init; }

    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal long? StartedAtTimestamp { get; init; }

    internal TimeSpan? WorkDuration { get; init; }
}

/// <summary>
/// Filesystem-aware half of patch processing. Parsing remains pure; this planner performs
/// containment, strict text, identity/link, fingerprint, and hunk-match validation without
/// creating a file or directory.
/// </summary>
internal sealed class WorkspacePatchPlanner
{

    private readonly WorkspacePatchSettings _settings;

    private readonly WorkspacePatchPlannerOptions _options;

    internal WorkspacePatchPlanner(
        WorkspacePatchSettings settings,
        WorkspacePatchPlannerOptions? options = null)
    {

        _settings =
            ArcanumSettingClamps.NormalizeWorkspacePatchSettings(
                settings
                ?? throw new ArgumentNullException(nameof(settings)));

        _options = options ?? new WorkspacePatchPlannerOptions();

    }

    internal Task<WorkspacePatchPlanResult> PlanAsync(
        string workspaceRoot,
        UnifiedDiffManifest manifest,
        CancellationToken cancellationToken) =>
        PlanCoreAsync(
            workspaceRoot,
            manifest,
            cancellationToken);

    private async Task<WorkspacePatchPlanResult> PlanCoreAsync(
        string workspaceRoot,
        UnifiedDiffManifest manifest,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        ArgumentNullException.ThrowIfNull(manifest);

        PatchPlanningBudget budget = new(
            _settings,
            _options.TimeProvider,
            _options.StartedAtTimestamp,
            _options.WorkDuration);

        try
        {
            Checkpoint(budget, cancellationToken);

            string root = Path.GetFullPath(workspaceRoot);

            List<WorkspacePatchPlannedFile> plannedFiles = [];

            List<WorkspaceFileCommitOperation> commitOperations = [];

            List<(string Path, WorkspaceFileFingerprint Fingerprint)> existingInputs = [];

            foreach (UnifiedDiffFile file in manifest.Files)
            {
                Checkpoint(budget, cancellationToken);

                switch (file.Operation)
                {
                    case UnifiedDiffOperationKind.Create:
                        await PlanCreateAsync(
                            root,
                            file,
                            plannedFiles,
                            commitOperations,
                            budget,
                            cancellationToken).ConfigureAwait(false);

                        break;

                    case UnifiedDiffOperationKind.Modify:
                        await PlanModifyAsync(
                            root,
                            file,
                            plannedFiles,
                            commitOperations,
                            existingInputs,
                            budget,
                            cancellationToken).ConfigureAwait(false);

                        break;

                    case UnifiedDiffOperationKind.Delete:
                        await PlanDeleteAsync(
                            root,
                            file,
                            plannedFiles,
                            commitOperations,
                            existingInputs,
                            budget,
                            cancellationToken).ConfigureAwait(false);

                        break;

                    case UnifiedDiffOperationKind.Rename:
                        await PlanRenameAsync(
                            root,
                            file,
                            plannedFiles,
                            commitOperations,
                            existingInputs,
                            budget,
                            cancellationToken).ConfigureAwait(false);

                        break;

                    default:
                        throw PlanningFailure(
                            "invalid_operation",
                            "The parsed patch contains an unsupported operation.");
                }
            }

            ValidateNoIdentityAliases(
                existingInputs,
                budget,
                cancellationToken);

            WorkspacePatchPlan plan = new(
                Array.AsReadOnly(plannedFiles.ToArray()),
                Array.AsReadOnly(commitOperations.ToArray()),
                Array.AsReadOnly(manifest.NormalizedPaths.ToArray()));

            return new WorkspacePatchPlanResult(
                Success: true,
                Plan: plan,
                Code: null,
                Message: null,
                Diagnostic: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PatchPlanningException exception)
        {
            return Failure(
                exception.Code,
                exception.Message,
                exception.Diagnostic);
        }
        catch (WorkspaceTextDecodingException exception)
        {
            return exception.Rejection == WorkspaceTextRejection.BinaryContent
                ? Failure(
                    "binary_file",
                    "A patch target is binary and cannot be edited as unified text.")
                : Failure(
                    "unsupported_encoding",
                    "A patch target is not strict UTF-8 text.");
        }
        catch (WorkspaceMutationRejectedException exception)
        {
            return Failure(
                MapMutationRejection(exception.Rejection),
                MutationRejectionMessage(exception.Rejection));
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return Failure(
                "workspace_read_failed",
                "A patch target could not be safely read and fingerprinted.");
        }

    }

    private static WorkspacePatchPlanResult Failure(
        string code,
        string message,
        WorkspacePatchHunkDiagnostic? diagnostic = null) =>
        new(
            Success: false,
            Plan: null,
            Code: code,
            Message: message,
            diagnostic);

    private async Task PlanCreateAsync(
        string root,
        UnifiedDiffFile file,
        List<WorkspacePatchPlannedFile> plannedFiles,
        List<WorkspaceFileCommitOperation> commitOperations,
        PatchPlanningBudget budget,
        CancellationToken cancellationToken)
    {

        string destination = file.DestinationPath!;

        WorkspaceFileFingerprint destinationFingerprint =
            await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                root,
                destination,
                cancellationToken).ConfigureAwait(false);

        if (destinationFingerprint.Exists)
        {
            throw PlanningFailure(
                "destination_exists",
                "A create destination already exists.");
        }

        AppliedDocument applied = ApplyHunks(
            destination,
            WorkspaceTextFile.CreateEmpty(),
            file.Hunks,
            budget,
            cancellationToken);

        byte[] output = EncodeOutput(
            applied.Document,
            stagingInputBytes: 0,
            budget,
            cancellationToken);

        commitOperations.Add(
            new WorkspaceFileCommitOperation(
                destination,
                destinationFingerprint,
                output,
                file.NewFileUnixMode));

        plannedFiles.Add(ToPlannedFile(file, applied));

    }

    private async Task PlanModifyAsync(
        string root,
        UnifiedDiffFile file,
        List<WorkspacePatchPlannedFile> plannedFiles,
        List<WorkspaceFileCommitOperation> commitOperations,
        List<(string Path, WorkspaceFileFingerprint Fingerprint)> existingInputs,
        PatchPlanningBudget budget,
        CancellationToken cancellationToken)
    {

        string path = file.SourcePath!;

        LoadedWorkspaceText loaded = await LoadExistingTextAsync(
            root,
            path,
            budget,
            cancellationToken).ConfigureAwait(false);

        existingInputs.Add((path, loaded.Fingerprint));

        AppliedDocument applied = ApplyHunks(
            path,
            loaded.Document,
            file.Hunks,
            budget,
            cancellationToken);

        byte[] output = EncodeOutput(
            applied.Document,
            loaded.Fingerprint.Length,
            budget,
            cancellationToken);

        commitOperations.Add(
            new WorkspaceFileCommitOperation(
                path,
                loaded.Fingerprint,
                output));

        plannedFiles.Add(ToPlannedFile(file, applied));

    }

    private async Task PlanDeleteAsync(
        string root,
        UnifiedDiffFile file,
        List<WorkspacePatchPlannedFile> plannedFiles,
        List<WorkspaceFileCommitOperation> commitOperations,
        List<(string Path, WorkspaceFileFingerprint Fingerprint)> existingInputs,
        PatchPlanningBudget budget,
        CancellationToken cancellationToken)
    {

        string source = file.SourcePath!;

        LoadedWorkspaceText loaded = await LoadExistingTextAsync(
            root,
            source,
            budget,
            cancellationToken).ConfigureAwait(false);

        existingInputs.Add((source, loaded.Fingerprint));

        AppliedDocument applied = ApplyHunks(
            source,
            loaded.Document,
            file.Hunks,
            budget,
            cancellationToken);

        if (applied.Document.Lines.Count != 0)
        {
            throw PlanningFailure(
                "delete_not_complete",
                "A delete patch must account for the complete source file.");
        }

        RegisterStaging(
            loaded.Fingerprint.Length,
            budget,
            cancellationToken);

        commitOperations.Add(
            new WorkspaceFileCommitOperation(
                source,
                loaded.Fingerprint,
                OutputBytes: null));

        plannedFiles.Add(ToPlannedFile(file, applied));

    }

    private async Task PlanRenameAsync(
        string root,
        UnifiedDiffFile file,
        List<WorkspacePatchPlannedFile> plannedFiles,
        List<WorkspaceFileCommitOperation> commitOperations,
        List<(string Path, WorkspaceFileFingerprint Fingerprint)> existingInputs,
        PatchPlanningBudget budget,
        CancellationToken cancellationToken)
    {

        string source = file.SourcePath!;

        string destination = file.DestinationPath!;

        LoadedWorkspaceText loaded = await LoadExistingTextAsync(
            root,
            source,
            budget,
            cancellationToken).ConfigureAwait(false);

        WorkspaceFileFingerprint destinationFingerprint =
            await WorkspaceFileFingerprintService.CaptureForMutationAsync(
                root,
                destination,
                cancellationToken).ConfigureAwait(false);

        if (destinationFingerprint.Exists)
        {
            throw PlanningFailure(
                "destination_exists",
                "A rename destination already exists.");
        }

        existingInputs.Add((source, loaded.Fingerprint));

        AppliedDocument applied = ApplyHunks(
            destination,
            loaded.Document,
            file.Hunks,
            budget,
            cancellationToken);

        byte[] output = EncodeOutput(
            applied.Document,
            loaded.Fingerprint.Length,
            budget,
            cancellationToken);

        commitOperations.Add(
            new WorkspaceFileCommitOperation(
                destination,
                destinationFingerprint,
                output,
                loaded.Fingerprint.UnixMode));

        commitOperations.Add(
            new WorkspaceFileCommitOperation(
                source,
                loaded.Fingerprint,
                OutputBytes: null));

        plannedFiles.Add(ToPlannedFile(file, applied));

    }

    private async Task<LoadedWorkspaceText> LoadExistingTextAsync(
        string root,
        string relativePath,
        PatchPlanningBudget budget,
        CancellationToken cancellationToken)
    {

        Checkpoint(budget, cancellationToken);

        long totalRemaining = Math.Max(
            0,
            _settings.MaxTotalInputBytes - budget.TotalInputBytes);

        long maximumRead = Math.Min(
            Math.Max(0, _settings.MaxInputBytesPerFile),
            totalRemaining);

        WorkspaceMutationRead source;

        try
        {

            source = await WorkspaceFileFingerprintService.ReadForMutationAsync(
                    root,
                    relativePath,
                    cancellationToken,
                    () => _options.AfterSourceHandleOpened?.Invoke(relativePath),
                    maximumRead)
                .ConfigureAwait(false);

        }
        catch (WorkspaceMutationReadLimitExceededException exception)
        {

            throw exception.ObservedLength > _settings.MaxInputBytesPerFile
                ? PlanningFailure(
                    "input_file_too_large",
                    "A patch source exceeds the per-file input byte limit.")
                : PlanningFailure(
                    "input_total_too_large",
                    "Patch sources exceed the aggregate input byte limit.");

        }

        if (!source.Fingerprint.Exists)
        {
            throw PlanningFailure(
                "file_not_found",
                "A patch source does not exist.");
        }

        budget.TotalInputBytes = checked(
            budget.TotalInputBytes + source.Bytes.LongLength);

        Checkpoint(budget, cancellationToken);

        WorkspaceTextFile document = WorkspaceTextFile.Decode(
            source.Bytes,
            () => Checkpoint(budget, cancellationToken));

        return new LoadedWorkspaceText(
            document,
            source.Fingerprint);

    }

    private AppliedDocument ApplyHunks(
        string path,
        WorkspaceTextFile source,
        IReadOnlyList<UnifiedDiffHunk> hunks,
        PatchPlanningBudget budget,
        CancellationToken cancellationToken)
    {

        List<WorkspaceTextLine> lines = [.. source.Lines];

        List<int> matchedLines = [];

        List<WorkspacePatchHunkDiagnostic> diagnostics = [];

        int relocatedHunks = 0;

        int runningOffset = 0;

        int minimumCandidate = 0;

        for (int hunkIndex = 0; hunkIndex < hunks.Count; hunkIndex++)
        {
            Checkpoint(budget, cancellationToken);

            UnifiedDiffHunk hunk = hunks[hunkIndex];

            int expectedIndex = ExpectedIndex(hunk) + runningOffset;

            HunkMatch match = FindMatch(
                lines,
                hunk,
                expectedIndex,
                minimumCandidate,
                budget,
                cancellationToken);

            WorkspacePatchHunkDiagnostic diagnostic = CreateHunkDiagnostic(
                path,
                hunkIndex + 1,
                hunk,
                expectedIndex,
                match);

            if (!match.Success)
            {
                throw PlanningFailure(
                    match.Ambiguous
                        ? "ambiguous_hunk"
                        : "hunk_conflict",
                    match.Ambiguous
                        ? "A hunk has multiple equally good relocation candidates."
                        : "A hunk does not match its expected or bounded relocation range.",
                    diagnostic);
            }

            diagnostics.Add(diagnostic);

            List<WorkspaceTextLine> replacement = BuildReplacement(
                lines,
                match.Index,
                hunk,
                source.DominantNewline,
                () => Checkpoint(budget, cancellationToken));

            lines.RemoveRange(match.Index, hunk.OldCount);

            lines.InsertRange(match.Index, replacement);

            ValidateTouchedDelimiterBoundaries(
                lines,
                match.Index,
                replacement.Count,
                () => Checkpoint(budget, cancellationToken));

            matchedLines.Add(match.Index + 1);

            if (match.Index != expectedIndex)
            {
                relocatedHunks++;
            }

            runningOffset +=
                hunk.NewCount
                - hunk.OldCount
                + match.Index
                - expectedIndex;

            minimumCandidate = match.Index + replacement.Count;
        }

        return new AppliedDocument(
            source.WithLines(
                lines,
                () => Checkpoint(budget, cancellationToken)),
            relocatedHunks,
            Array.AsReadOnly(matchedLines.ToArray()),
            Array.AsReadOnly(diagnostics.ToArray()));

    }

    private HunkMatch FindMatch(
        IReadOnlyList<WorkspaceTextLine> lines,
        UnifiedDiffHunk hunk,
        int expectedIndex,
        int minimumCandidate,
        PatchPlanningBudget budget,
        CancellationToken cancellationToken)
    {

        int maximumStart = lines.Count - hunk.OldCount;

        if (maximumStart < 0)
        {
            return HunkMatch.Failed;
        }

        _options.ExactMatchCheckpoint?.Invoke();

        Checkpoint(budget, cancellationToken);

        if (expectedIndex >= minimumCandidate
            && expectedIndex <= maximumStart
            && MatchesAt(
                lines,
                hunk,
                expectedIndex,
                normalizeContextWhitespace: false,
                () => Checkpoint(budget, cancellationToken),
                out _))
        {
            return new HunkMatch(
                Success: true,
                Ambiguous: false,
                Index: expectedIndex,
                CandidateIndexes: [expectedIndex]);
        }

        int window = Math.Max(0, _settings.FuzzyMatchWindowLines);

        int lower = Math.Max(
            minimumCandidate,
            Math.Max(0, expectedIndex - window));

        int upper = Math.Min(
            maximumStart,
            expectedIndex + window);

        int bestIndex = -1;

        int bestExactContext = -1;

        int bestDistance = int.MaxValue;

        List<int> bestCandidates = [];

        for (int candidate = lower; candidate <= upper; candidate++)
        {
            _options.FuzzyMatchCheckpoint?.Invoke();

            Checkpoint(budget, cancellationToken);

            if (!MatchesAt(
                    lines,
                    hunk,
                    candidate,
                    normalizeContextWhitespace: true,
                    () => Checkpoint(budget, cancellationToken),
                    out int exactContext))
            {
                continue;
            }

            int distance = Math.Abs(candidate - expectedIndex);

            bool better =
                exactContext > bestExactContext
                || (exactContext == bestExactContext
                    && distance < bestDistance);

            if (better)
            {
                bestIndex = candidate;

                bestExactContext = exactContext;

                bestDistance = distance;

                bestCandidates.Clear();

                bestCandidates.Add(candidate);

                continue;
            }

            if (exactContext == bestExactContext
                && distance == bestDistance)
            {
                bestCandidates.Add(candidate);
            }
        }

        if (bestIndex < 0)
        {
            return HunkMatch.Failed;
        }

        return new HunkMatch(
            Success: bestCandidates.Count == 1,
            Ambiguous: bestCandidates.Count > 1,
            Index: bestIndex,
            CandidateIndexes: Array.AsReadOnly(bestCandidates.ToArray()));

    }

    private static WorkspacePatchHunkDiagnostic CreateHunkDiagnostic(
        string path,
        int ordinal,
        UnifiedDiffHunk hunk,
        int expectedIndex,
        HunkMatch match)
    {

        const int maxCandidateLines = 64;

        int[] allCandidates = match.CandidateIndexes
            .Select(static index => index + 1)
            .ToArray();

        int[] retainedCandidates = allCandidates
            .Take(maxCandidateLines)
            .ToArray();

        string section = string.IsNullOrEmpty(hunk.Section)
            ? string.Empty
            : $" {hunk.Section}";

        string header =
            $"@@ -{FormatHunkRange(hunk.OldStart, hunk.OldCount)}"
            + $" +{FormatHunkRange(hunk.NewStart, hunk.NewCount)} @@"
            + section;

        return new WorkspacePatchHunkDiagnostic(
            path,
            ordinal,
            header,
            expectedIndex + 1,
            match.Success ? match.Index + 1 : null,
            match.Success && match.Index != expectedIndex,
            retainedCandidates,
            allCandidates.Length,
            allCandidates.Length - retainedCandidates.Length);

    }

    private static string FormatHunkRange(int start, int count) =>
        count == 1 ? start.ToString(
            System.Globalization.CultureInfo.InvariantCulture)
        : string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{start},{count}");

    private static bool MatchesAt(
        IReadOnlyList<WorkspaceTextLine> lines,
        UnifiedDiffHunk hunk,
        int candidate,
        bool normalizeContextWhitespace,
        Action checkpoint,
        out int exactContextCount)
    {

        exactContextCount = 0;

        int sourceIndex = candidate;

        for (int lineIndex = 0; lineIndex < hunk.Lines.Count; lineIndex++)
        {
            if ((lineIndex & 0xFF) == 0)
            {
                checkpoint();
            }

            UnifiedDiffLine patchLine = hunk.Lines[lineIndex];

            if (patchLine.Kind == '+')
            {
                continue;
            }

            if (sourceIndex >= lines.Count)
            {
                return false;
            }

            WorkspaceTextLine sourceLine = lines[sourceIndex++];

            if (patchLine.OldHasNoFinalNewline
                ? sourceLine.Delimiter.Length != 0
                : sourceLine.Delimiter.Length == 0)
            {
                return false;
            }

            if (patchLine.Kind == '-')
            {
                if (!string.Equals(
                        sourceLine.Text,
                        patchLine.Text,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                continue;
            }

            bool exact = string.Equals(
                sourceLine.Text,
                patchLine.Text,
                StringComparison.Ordinal);

            if (exact)
            {
                exactContextCount++;

                continue;
            }

            if (!normalizeContextWhitespace
                || !HorizontalWhitespaceEquals(
                    sourceLine.Text,
                    patchLine.Text,
                    checkpoint))
            {
                return false;
            }
        }

        return true;

    }

    private static List<WorkspaceTextLine> BuildReplacement(
        IReadOnlyList<WorkspaceTextLine> source,
        int matchIndex,
        UnifiedDiffHunk hunk,
        string dominantNewline,
        Action checkpoint)
    {

        List<WorkspaceTextLine> replacement = [];

        int sourceIndex = matchIndex;

        for (int lineIndex = 0; lineIndex < hunk.Lines.Count; lineIndex++)
        {
            if ((lineIndex & 0xFF) == 0)
            {
                checkpoint();
            }

            UnifiedDiffLine patchLine = hunk.Lines[lineIndex];

            switch (patchLine.Kind)
            {
                case ' ':
                {
                    WorkspaceTextLine existing = source[sourceIndex++];

                    string delimiter = patchLine.NewHasNoFinalNewline
                        ? string.Empty
                        : existing.Delimiter;

                    replacement.Add(existing with { Delimiter = delimiter });

                    break;
                }

                case '-':
                    sourceIndex++;

                    break;

                case '+':
                    replacement.Add(
                        new WorkspaceTextLine(
                            patchLine.Text,
                            patchLine.NewHasNoFinalNewline
                                ? string.Empty
                                : dominantNewline));

                    break;

                default:
                    throw PlanningFailure(
                        "invalid_hunk",
                        "A parsed hunk contains an unsupported line kind.");
            }
        }

        return replacement;

    }

    private static void ValidateTouchedDelimiterBoundaries(
        IReadOnlyList<WorkspaceTextLine> lines,
        int replacementStart,
        int replacementCount,
        Action checkpoint)
    {

        if (lines.Count <= 1)
        {
            return;
        }

        int lower = Math.Max(0, replacementStart - 1);

        int upper = Math.Min(
            lines.Count - 2,
            replacementStart + replacementCount);

        for (int index = lower; index <= upper; index++)
        {
            if ((index & 0xFF) == 0)
            {

                checkpoint();

            }

            if (lines[index].Delimiter.Length == 0)
            {
                throw PlanningFailure(
                    "invalid_no_newline",
                    "A no-final-newline marker would occur before the final logical line.");
            }
        }

    }

    private static int ExpectedIndex(UnifiedDiffHunk hunk) =>
        hunk.OldCount == 0
            ? hunk.OldStart
            : hunk.OldStart - 1;

    private static bool HorizontalWhitespaceEquals(
        string left,
        string right,
        Action checkpoint)
    {

        int leftIndex = 0;

        int rightIndex = 0;

        SkipHorizontalWhitespace(left, ref leftIndex, checkpoint);

        SkipHorizontalWhitespace(right, ref rightIndex, checkpoint);

        while (leftIndex < left.Length
            && rightIndex < right.Length)
        {
            if (((leftIndex + rightIndex) & 0xFF) == 0)
            {

                checkpoint();

            }

            bool leftWhitespace =
                left[leftIndex] is ' ' or '\t';

            bool rightWhitespace =
                right[rightIndex] is ' ' or '\t';

            if (leftWhitespace || rightWhitespace)
            {
                if (!leftWhitespace || !rightWhitespace)
                {
                    return false;
                }

                SkipHorizontalWhitespace(left, ref leftIndex, checkpoint);

                SkipHorizontalWhitespace(right, ref rightIndex, checkpoint);

                continue;
            }

            if (left[leftIndex] != right[rightIndex])
            {
                return false;
            }

            leftIndex++;

            rightIndex++;
        }

        SkipHorizontalWhitespace(left, ref leftIndex, checkpoint);

        SkipHorizontalWhitespace(right, ref rightIndex, checkpoint);

        return leftIndex == left.Length
            && rightIndex == right.Length;

    }

    private static void SkipHorizontalWhitespace(
        string value,
        ref int index,
        Action checkpoint)
    {

        while (index < value.Length
            && value[index] is ' ' or '\t')
        {
            if ((index & 0xFF) == 0)
            {

                checkpoint();

            }

            index++;
        }

    }

    private byte[] EncodeOutput(
        WorkspaceTextFile document,
        long stagingInputBytes,
        PatchPlanningBudget budget,
        CancellationToken cancellationToken)
    {

        Checkpoint(budget, cancellationToken);

        long outputBytes;

        try
        {

            outputBytes = document.GetEncodedByteCount(
                () => Checkpoint(budget, cancellationToken));

        }
        catch (OverflowException)
        {

            throw PlanningFailure(
                "output_file_too_large",
                "A patched file exceeds the per-file output byte limit.");

        }

        if (outputBytes > _settings.MaxOutputBytesPerFile)
        {

            throw PlanningFailure(
                "output_file_too_large",
                "A patched file exceeds the per-file output byte limit.");

        }

        if (outputBytes > _settings.MaxTotalOutputBytes - budget.TotalOutputBytes)
        {

            throw PlanningFailure(
                "output_total_too_large",
                "Patched files exceed the aggregate output byte limit.");

        }

        long stagingBytes;

        try
        {

            stagingBytes = checked(stagingInputBytes + outputBytes);

        }
        catch (OverflowException)
        {

            throw PlanningFailure(
                "staging_file_too_large",
                "A patch file exceeds the per-file staging byte limit.");

        }

        ValidateStaging(stagingBytes, budget);

        byte[] encoded = document.Encode();

        if (encoded.LongLength != outputBytes)
        {

            throw new InvalidOperationException(
                "Patch output byte preflight did not match exact encoding.");

        }

        budget.TotalOutputBytes += outputBytes;

        budget.TotalStagingBytes += stagingBytes;

        Checkpoint(budget, cancellationToken);

        return encoded;

    }

    private void RegisterStaging(
        long stagingBytes,
        PatchPlanningBudget budget,
        CancellationToken cancellationToken)
    {

        Checkpoint(budget, cancellationToken);

        ValidateStaging(stagingBytes, budget);

        budget.TotalStagingBytes += stagingBytes;

    }

    private void ValidateStaging(
        long stagingBytes,
        PatchPlanningBudget budget)
    {

        if (stagingBytes > _settings.MaxStagingBytesPerFile)
        {

            throw PlanningFailure(
                "staging_file_too_large",
                "A patch file exceeds the per-file staging byte limit.");

        }

        if (stagingBytes
            > _settings.MaxTotalStagingBytes - budget.TotalStagingBytes)
        {

            throw PlanningFailure(
                "staging_total_too_large",
                "Patch files exceed the aggregate staging byte limit.");

        }

    }

    private static void Checkpoint(
        PatchPlanningBudget budget,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (budget.TimeProvider.GetElapsedTime(budget.StartedAt)
            >= budget.WorkDuration)
        {

            throw PlanningFailure(
                "max_elapsed",
                "Patch planning exceeded its absolute work deadline.");

        }

    }

    private static WorkspacePatchPlannedFile ToPlannedFile(
        UnifiedDiffFile file,
        AppliedDocument applied) =>
        new(
            file.ResultPath,
            file.Operation,
            file.Hunks.Count,
            applied.RelocatedHunks,
            applied.MatchedLines,
            applied.Hunks);

    private static void ValidateNoIdentityAliases(
        IReadOnlyList<(string Path, WorkspaceFileFingerprint Fingerprint)> inputs,
        PatchPlanningBudget budget,
        CancellationToken cancellationToken)
    {

        for (int left = 0; left < inputs.Count; left++)
        {
            Checkpoint(budget, cancellationToken);

            for (int right = left + 1; right < inputs.Count; right++)
            {
                if ((right & 0x3F) == 0)
                {

                    Checkpoint(budget, cancellationToken);

                }

                if (!WorkspaceRelativePath.Comparer.Equals(
                        inputs[left].Path,
                        inputs[right].Path)
                    && FileHandleIdentity.IdentitiesMatch(
                        inputs[left].Fingerprint.Identity,
                        inputs[right].Fingerprint.Identity))
                {
                    throw PlanningFailure(
                        "path_alias",
                        "Multiple patch paths resolve to the same file identity.");
                }
            }
        }

    }

    private static string MapMutationRejection(
        WorkspaceMutationRejection rejection) =>
        rejection switch
        {
            WorkspaceMutationRejection.InvalidRelativePath => "invalid_path",
            WorkspaceMutationRejection.PathEscapesWorkspace => "path_escape",
            WorkspaceMutationRejection.SymbolicLink => "symlink",
            WorkspaceMutationRejection.NotRegularFile => "not_regular_file",
            WorkspaceMutationRejection.MetadataUnavailable => "metadata_unavailable",
            WorkspaceMutationRejection.HardLinkedFile => "hard_link",
            WorkspaceMutationRejection.ConcurrentModification => "concurrent_edit",
            _ => "validation_failed",
        };

    private static string MutationRejectionMessage(
        WorkspaceMutationRejection rejection) =>
        rejection switch
        {
            WorkspaceMutationRejection.SymbolicLink =>
                "Symbolic-link patch targets and path components are rejected.",
            WorkspaceMutationRejection.HardLinkedFile =>
                "Patch targets with multiple hard links are rejected.",
            WorkspaceMutationRejection.ConcurrentModification =>
                "A patch target changed during validation.",
            _ =>
                "A patch target failed workspace containment or metadata validation.",
        };

    private static PatchPlanningException PlanningFailure(
        string code,
        string message,
        WorkspacePatchHunkDiagnostic? diagnostic = null) =>
        new(code, message, diagnostic);

    private sealed record LoadedWorkspaceText(
        WorkspaceTextFile Document,
        WorkspaceFileFingerprint Fingerprint);

    private sealed record AppliedDocument(
        WorkspaceTextFile Document,
        int RelocatedHunks,
        IReadOnlyList<int> MatchedLines,
        IReadOnlyList<WorkspacePatchHunkDiagnostic> Hunks);

    private sealed class PatchPlanningBudget(
        WorkspacePatchSettings settings,
        TimeProvider timeProvider,
        long? startedAtTimestamp,
        TimeSpan? workDuration)
    {
        internal TimeProvider TimeProvider { get; } =
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        internal long StartedAt { get; } =
            startedAtTimestamp ?? timeProvider.GetTimestamp();

        internal TimeSpan WorkDuration { get; } =
            workDuration
            ?? TimeSpan.FromMilliseconds(
                Math.Max(
                    1,
                    settings.MaxElapsedMilliseconds
                    - settings.RollbackReserveMilliseconds));

        internal long TotalInputBytes { get; set; }

        internal long TotalOutputBytes { get; set; }

        internal long TotalStagingBytes { get; set; }
    }

    private readonly record struct HunkMatch(
        bool Success,
        bool Ambiguous,
        int Index,
        IReadOnlyList<int> CandidateIndexes)
    {
        internal static HunkMatch Failed { get; } =
            new(
                Success: false,
                Ambiguous: false,
                Index: -1,
                CandidateIndexes: Array.Empty<int>());
    }

    private sealed class PatchPlanningException(
        string code,
        string message,
        WorkspacePatchHunkDiagnostic? diagnostic = null) : Exception(message)
    {
        internal string Code { get; } = code;

        internal WorkspacePatchHunkDiagnostic? Diagnostic { get; } = diagnostic;
    }
}
