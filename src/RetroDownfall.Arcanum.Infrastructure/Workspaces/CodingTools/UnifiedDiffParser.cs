using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal enum UnifiedDiffOperationKind
{
    Create,
    Modify,
    Rename,
    Delete,
}

internal sealed record UnifiedDiffLine(
    char Kind,
    string Text,
    bool OldHasNoFinalNewline = false,
    bool NewHasNoFinalNewline = false);

internal sealed record UnifiedDiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    string? Section,
    IReadOnlyList<UnifiedDiffLine> Lines);

internal sealed record UnifiedDiffFile(
    UnifiedDiffOperationKind Operation,
    string? SourcePath,
    string? DestinationPath,
    IReadOnlyList<UnifiedDiffHunk> Hunks,
    UnixFileMode? NewFileUnixMode)
{
    internal string ResultPath => DestinationPath ?? SourcePath!;
}

internal sealed record UnifiedDiffManifest(
    IReadOnlyList<UnifiedDiffFile> Files,
    IReadOnlyList<string> NormalizedPaths);

internal sealed record UnifiedDiffParseResult(
    bool Success,
    UnifiedDiffManifest? Manifest,
    string? Code,
    string? Message);

internal static class UnifiedDiffParser
{

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static UnifiedDiffParseResult Parse(
        string? patch,
        WorkspacePatchSettings settings,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(patch))
        {
            return Failure("invalid_patch", "The unified diff is empty.");
        }

        int patchBytes;

        try
        {
            patchBytes = StrictUtf8.GetByteCount(patch);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (EncoderFallbackException)
        {
            return Failure(
                "invalid_patch",
                "The unified diff contains invalid Unicode.");
        }

        if (patchBytes > settings.MaxPatchBytes)
        {
            return Failure(
                "patch_too_large",
                $"Physical resource boundary: the unified diff is {patchBytes} UTF-8 bytes; the per-request allocation limit is {settings.MaxPatchBytes} bytes. No workspace changes were made. Split the diff into smaller apply_patch calls.");
        }

        try
        {
            return new Parser(
                patch,
                settings,
                cancellationToken).Parse();
        }
        catch (UnifiedDiffParseException exception)
        {
            return Failure(exception.Code, exception.Message);
        }

    }

    private static UnifiedDiffParseResult Failure(
        string code,
        string message) =>
        new(
            Success: false,
            Manifest: null,
            Code: code,
            Message: message);

    private sealed class Parser
    {

        private readonly IReadOnlyList<string> _lines;

        private readonly WorkspacePatchSettings _settings;

        private readonly CancellationToken _cancellationToken;

        private readonly List<UnifiedDiffFile> _files = [];

        internal Parser(
            string patch,
            WorkspacePatchSettings settings,
            CancellationToken cancellationToken)
        {

            _settings = settings;

            _cancellationToken = cancellationToken;

            _lines = SplitLogicalLines(
                patch,
                cancellationToken);

        }

        internal UnifiedDiffParseResult Parse()
        {

            FileBuilder? current = null;

            for (int index = 0; index < _lines.Count;)
            {
                Checkpoint();

                string line = _lines[index];

                if (line.Length == 0)
                {
                    index++;

                    continue;
                }

                if (line.StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    FinalizeCurrent(ref current);

                    (string oldPath, string newPath) = ParseGitHeaderPaths(
                        line["diff --git ".Length..]);

                    current = new FileBuilder
                    {
                        GitOldPath = NormalizePath(oldPath, PathSide.Old),
                        GitNewPath = NormalizePath(newPath, PathSide.New),
                    };

                    index++;

                    continue;
                }

                if (line.StartsWith("diff --", StringComparison.Ordinal))
                {
                    throw Invalid(
                        "unsupported_metadata",
                        "Combined and non-Git diff headers are not supported.");
                }

                if (IsBinaryMarker(line))
                {
                    throw Invalid(
                        "binary_patch",
                        "Binary unified-diff records are not supported.");
                }

                if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    if (current?.HasUnifiedHeaders == true)
                    {
                        FinalizeCurrent(ref current);
                    }

                    current ??= new FileBuilder();

                    current.OldHeaderPath = ParseHeaderPath(
                        line["--- ".Length..],
                        PathSide.Old);

                    index++;

                    if (index >= _lines.Count
                        || !_lines[index].StartsWith("+++ ", StringComparison.Ordinal))
                    {
                        throw Invalid(
                            "invalid_patch",
                            "Every old-file header must be followed by a new-file header.");
                    }

                    current.NewHeaderPath = ParseHeaderPath(
                        _lines[index]["+++ ".Length..],
                        PathSide.New);

                    current.HasUnifiedHeaders = true;

                    index++;

                    continue;
                }

                if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    throw Invalid(
                        "invalid_patch",
                        "A new-file header appeared without an old-file header.");
                }

                if (line.StartsWith("@@ ", StringComparison.Ordinal)
                    || line == "@@")
                {
                    if (current?.HasUnifiedHeaders != true)
                    {
                        throw Invalid(
                            "invalid_patch",
                            "A hunk appeared before its file headers.");
                    }

                    current.Hunks.Add(ParseHunk(ref index));

                    continue;
                }

                if (line == @"\ No newline at end of file")
                {
                    throw Invalid(
                        "invalid_patch",
                        "A no-final-newline marker appeared outside a hunk.");
                }

                current ??= new FileBuilder();

                ParseMetadata(current, line);

                index++;
            }

            FinalizeCurrent(ref current);

            if (_files.Count == 0)
            {
                throw Invalid(
                    "invalid_patch",
                    "The unified diff does not contain any file records.");
            }

            ValidateManifestAliasesAndCycles();

            List<string> normalizedPaths = [];

            HashSet<string> seenPaths = new(WorkspaceRelativePath.Comparer);

            foreach (UnifiedDiffFile file in _files)
            {
                Checkpoint();

                if (file.SourcePath is not null
                    && seenPaths.Add(file.SourcePath))
                {
                    normalizedPaths.Add(file.SourcePath);
                }

                if (file.DestinationPath is not null
                    && seenPaths.Add(file.DestinationPath))
                {
                    normalizedPaths.Add(file.DestinationPath);
                }
            }

            UnifiedDiffManifest manifest = new(
                Array.AsReadOnly(_files.ToArray()),
                Array.AsReadOnly(normalizedPaths.ToArray()));

            return new UnifiedDiffParseResult(
                Success: true,
                Manifest: manifest,
                Code: null,
                Message: null);

        }

        private UnifiedDiffHunk ParseHunk(ref int index)
        {

            string header = _lines[index];

            ParseHunkHeader(
                header,
                out int oldStart,
                out int oldCount,
                out int newStart,
                out int newCount,
                out string? section);

            index++;

            List<UnifiedDiffLine> lines = [];

            int observedOld = 0;

            int observedNew = 0;

            while (index < _lines.Count)
            {
                Checkpoint();

                string line = _lines[index];

                if (line == @"\ No newline at end of file")
                {
                    if (lines.Count == 0)
                    {
                        throw Invalid(
                            "invalid_patch",
                            "A no-final-newline marker has no preceding hunk line.");
                    }

                    UnifiedDiffLine preceding = lines[^1];

                    preceding = preceding.Kind switch
                    {
                        ' ' when !preceding.OldHasNoFinalNewline
                            && !preceding.NewHasNoFinalNewline =>
                            preceding with
                            {
                                OldHasNoFinalNewline = true,
                                NewHasNoFinalNewline = true,
                            },
                        '-' when !preceding.OldHasNoFinalNewline =>
                            preceding with { OldHasNoFinalNewline = true },
                        '+' when !preceding.NewHasNoFinalNewline =>
                            preceding with { NewHasNoFinalNewline = true },
                        _ => throw Invalid(
                            "invalid_patch",
                            "A no-final-newline marker was duplicated."),
                    };

                    lines[^1] = preceding;

                    index++;

                    continue;
                }

                if (observedOld == oldCount
                    && observedNew == newCount)
                {
                    break;
                }

                if (line.Length == 0
                    || line[0] is not (' ' or '-' or '+'))
                {
                    throw Invalid(
                        "invalid_patch",
                        "A hunk body contains an unsupported line.");
                }

                char kind = line[0];

                string text = line[1..];

                if (kind is ' ' or '-')
                {
                    observedOld++;
                }

                if (kind is ' ' or '+')
                {
                    observedNew++;
                }

                if (observedOld > oldCount || observedNew > newCount)
                {
                    throw Invalid(
                        "invalid_patch",
                        "A hunk body exceeds the counts declared by its header.");
                }

                lines.Add(new UnifiedDiffLine(kind, text));

                index++;
            }

            if (observedOld != oldCount || observedNew != newCount)
            {
                throw Invalid(
                    "invalid_patch",
                    "A hunk body does not match the counts declared by its header.");
            }

            return new UnifiedDiffHunk(
                oldStart,
                oldCount,
                newStart,
                newCount,
                section,
                Array.AsReadOnly(lines.ToArray()));

        }

        private void ParseMetadata(
            FileBuilder file,
            string line)
        {

            if (line.StartsWith("index ", StringComparison.Ordinal))
            {
                RegisterMetadata(
                    file,
                    MetadataKind.Index,
                    order: 40);
                ValidateIndexMetadata(line["index ".Length..]);

                file.SawIndex = true;

                return;
            }

            if (line.StartsWith("new file mode ", StringComparison.Ordinal))
            {
                RegisterMetadata(
                    file,
                    MetadataKind.NewFileMode,
                    order: 10);
                ParsedMode mode = ParseMode(line["new file mode ".Length..]);

                file.NewFileMode = mode.UnixMode;

                file.HasNewFileMode = true;

                return;
            }

            if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
            {
                RegisterMetadata(
                    file,
                    MetadataKind.DeletedFileMode,
                    order: 10);
                _ = ParseMode(line["deleted file mode ".Length..]);

                file.HasDeletedFileMode = true;

                return;
            }

            if (line.StartsWith("similarity index ", StringComparison.Ordinal))
            {
                RegisterMetadata(
                    file,
                    MetadataKind.Similarity,
                    order: 10);
                ValidateSimilarity(line["similarity index ".Length..]);

                file.HasSimilarity = true;

                return;
            }

            if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                RegisterMetadata(
                    file,
                    MetadataKind.RenameFrom,
                    order: 20);
                file.RenameFrom = NormalizePath(
                    ParseMetadataPath(line["rename from ".Length..]),
                    PathSide.Metadata);

                return;
            }

            if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                RegisterMetadata(
                    file,
                    MetadataKind.RenameTo,
                    order: 30);
                file.RenameTo = NormalizePath(
                    ParseMetadataPath(line["rename to ".Length..]),
                    PathSide.Metadata);

                return;
            }

            if (line.StartsWith("old mode ", StringComparison.Ordinal)
                || line.StartsWith("new mode ", StringComparison.Ordinal)
                || line.StartsWith("copy from ", StringComparison.Ordinal)
                || line.StartsWith("copy to ", StringComparison.Ordinal)
                || line.StartsWith("dissimilarity index ", StringComparison.Ordinal)
                || line.StartsWith("mode ", StringComparison.Ordinal))
            {
                throw Invalid(
                    "unsupported_metadata",
                    "The unified diff contains unsupported file metadata.");
            }

            if (line.StartsWith("Subproject commit ", StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid(
                    "submodule_patch",
                    "Submodule unified-diff records are not supported.");
            }

            throw Invalid(
                "unsupported_metadata",
                "The unified diff contains unsupported metadata.");

        }

        private static void RegisterMetadata(
            FileBuilder file,
            MetadataKind kind,
            int order)
        {

            if (file.GitOldPath is null
                || file.GitNewPath is null
                || file.HasUnifiedHeaders
                || file.Hunks.Count > 0
                || !file.MetadataKinds.Add(kind)
                || order <= file.LastMetadataOrder)
            {
                throw Invalid(
                    "invalid_metadata",
                    "Git file metadata is duplicated, out of order, or outside its file-header preamble.");
            }

            file.LastMetadataOrder = order;

        }

        private void FinalizeCurrent(ref FileBuilder? current)
        {

            if (current is null)
            {
                return;
            }

            UnifiedDiffFile file = BuildFile(current);

            _files.Add(file);

            current = null;

        }

        private static UnifiedDiffFile BuildFile(FileBuilder file)
        {

            bool hasRenameFrom = file.RenameFrom is not null;

            bool hasRenameTo = file.RenameTo is not null;

            if (hasRenameFrom != hasRenameTo)
            {
                throw Invalid(
                    "invalid_rename",
                    "Rename metadata must contain both source and destination paths.");
            }

            string? source = file.HasUnifiedHeaders
                ? file.OldHeaderPath
                : file.RenameFrom;

            string? destination = file.HasUnifiedHeaders
                ? file.NewHeaderPath
                : file.RenameTo;

            if (!file.HasUnifiedHeaders)
            {
                if (file.HasNewFileMode)
                {
                    destination = file.GitNewPath;
                }
                else if (file.HasDeletedFileMode)
                {
                    source = file.GitOldPath;
                }
            }

            UnifiedDiffOperationKind operation;

            if (hasRenameFrom)
            {
                source = file.RenameFrom;

                destination = file.RenameTo;

                operation = UnifiedDiffOperationKind.Rename;
            }
            else if (source is null && destination is not null)
            {
                operation = UnifiedDiffOperationKind.Create;
            }
            else if (source is not null && destination is null)
            {
                operation = UnifiedDiffOperationKind.Delete;
            }
            else if (source is not null
                && destination is not null
                && WorkspaceRelativePath.Comparer.Equals(source, destination))
            {
                operation = UnifiedDiffOperationKind.Modify;
            }
            else if (source is not null && destination is not null)
            {
                throw Invalid(
                    "invalid_rename",
                    "Differing file headers require explicit rename metadata.");
            }
            else
            {
                throw Invalid(
                    "invalid_patch",
                    "A file record does not identify a source or destination.");
            }

            ValidateOperationMetadata(file, operation);

            ValidateHeaderPathAgreement(
                file,
                operation,
                source,
                destination);

            if (operation == UnifiedDiffOperationKind.Modify
                && file.Hunks.Count == 0)
            {
                throw Invalid(
                    "mode_only_patch",
                    "Mode-only and empty modification records are not supported.");
            }

            if (operation == UnifiedDiffOperationKind.Rename
                && source is not null
                && destination is not null
                && WorkspaceRelativePath.Comparer.Equals(source, destination))
            {
                throw Invalid(
                    "invalid_rename",
                    "A rename source and destination must be distinct.");
            }

            return new UnifiedDiffFile(
                operation,
                source,
                destination,
                Array.AsReadOnly(file.Hunks.ToArray()),
                operation == UnifiedDiffOperationKind.Create
                    ? file.NewFileMode
                    : null);

        }

        private static void ValidateOperationMetadata(
            FileBuilder file,
            UnifiedDiffOperationKind operation)
        {

            if (file.HasNewFileMode
                && operation != UnifiedDiffOperationKind.Create)
            {
                throw Invalid(
                    "invalid_patch",
                    "New-file mode metadata does not describe a create operation.");
            }

            if (file.HasDeletedFileMode
                && operation != UnifiedDiffOperationKind.Delete)
            {
                throw Invalid(
                    "invalid_patch",
                    "Deleted-file mode metadata does not describe a delete operation.");
            }

            if (file.HasSimilarity
                && operation != UnifiedDiffOperationKind.Rename)
            {
                throw Invalid(
                    "invalid_patch",
                    "Similarity metadata is supported only for renames.");
            }

            if (operation == UnifiedDiffOperationKind.Create
                && file.HasDeletedFileMode)
            {
                throw Invalid(
                    "invalid_patch",
                    "A create operation cannot carry delete metadata.");
            }

            if (operation == UnifiedDiffOperationKind.Delete
                && file.HasNewFileMode)
            {
                throw Invalid(
                    "invalid_patch",
                    "A delete operation cannot carry create metadata.");
            }

        }

        private static void ValidateHeaderPathAgreement(
            FileBuilder file,
            UnifiedDiffOperationKind operation,
            string? source,
            string? destination)
        {

            if (file.RenameFrom is not null
                && file.HasUnifiedHeaders
                && (source is null
                    || destination is null
                    || file.OldHeaderPath is null
                    || file.NewHeaderPath is null
                    || !WorkspaceRelativePath.Comparer.Equals(
                        file.RenameFrom,
                        file.OldHeaderPath)
                    || !WorkspaceRelativePath.Comparer.Equals(
                        file.RenameTo,
                        file.NewHeaderPath)))
            {
                throw Invalid(
                    "invalid_rename",
                    "Rename metadata does not agree with the unified file headers.");
            }

            if (file.GitOldPath is null && file.GitNewPath is null)
            {
                return;
            }

            if (file.GitOldPath is null || file.GitNewPath is null)
            {
                throw Invalid(
                    "invalid_patch",
                    "A Git diff header must identify both sides.");
            }

            bool agrees = operation switch
            {
                UnifiedDiffOperationKind.Create =>
                    destination is not null
                    && WorkspaceRelativePath.Comparer.Equals(
                        file.GitOldPath,
                        destination)
                    && WorkspaceRelativePath.Comparer.Equals(
                        file.GitNewPath,
                        destination),
                UnifiedDiffOperationKind.Delete =>
                    source is not null
                    && WorkspaceRelativePath.Comparer.Equals(
                        file.GitOldPath,
                        source)
                    && WorkspaceRelativePath.Comparer.Equals(
                        file.GitNewPath,
                        source),
                _ =>
                    source is not null
                    && destination is not null
                    && WorkspaceRelativePath.Comparer.Equals(
                        file.GitOldPath,
                        source)
                    && WorkspaceRelativePath.Comparer.Equals(
                        file.GitNewPath,
                        destination),
            };

            if (!agrees)
            {
                throw Invalid(
                    "path_mismatch",
                    "Git and unified file headers identify different paths.");
            }

        }

        private void ValidateManifestAliasesAndCycles()
        {

            ValidateRenameCycles();

            HashSet<string> destinations =
                new(WorkspaceRelativePath.Comparer);

            Dictionary<string, int> affectedOwners =
                new(WorkspaceRelativePath.Comparer);

            List<string> topologyPaths = [];

            for (int fileIndex = 0; fileIndex < _files.Count; fileIndex++)
            {
                Checkpoint();

                UnifiedDiffFile file = _files[fileIndex];

                if (!destinations.Add(file.ResultPath))
                {
                    throw Invalid(
                        "duplicate_destination",
                        "Multiple file records resolve to the same destination.");
                }

                HashSet<string> pathsInFile =
                    new(WorkspaceRelativePath.Comparer);

                if (file.SourcePath is not null)
                {
                    pathsInFile.Add(file.SourcePath);
                }

                if (file.DestinationPath is not null)
                {
                    pathsInFile.Add(file.DestinationPath);
                }

                foreach (string path in pathsInFile)
                {
                    Checkpoint();

                    if (topologyPaths.Any(
                            existing =>
                                WorkspaceRelativePath.HasAncestorCollision(
                                    existing,
                                    path)))
                    {
                        throw Invalid(
                            "path_topology",
                            "Patch paths cannot be ancestors or descendants of one another.");
                    }

                    if (affectedOwners.TryGetValue(path, out int owner)
                        && owner != fileIndex)
                    {
                        throw Invalid(
                            "path_alias",
                            "Multiple file records contain filesystem path aliases.");
                    }

                    affectedOwners[path] = fileIndex;
                    topologyPaths.Add(path);
                }
            }

        }

        private void ValidateRenameCycles()
        {

            Dictionary<string, string> edges =
                new(WorkspaceRelativePath.Comparer);

            foreach (UnifiedDiffFile file in _files)
            {
                Checkpoint();

                if (file.Operation == UnifiedDiffOperationKind.Rename)
                {
                    edges[file.SourcePath!] = file.DestinationPath!;
                }
            }

            HashSet<string> completed =
                new(WorkspaceRelativePath.Comparer);

            foreach (string start in edges.Keys)
            {
                Checkpoint();

                if (completed.Contains(start))
                {
                    continue;
                }

                HashSet<string> path =
                    new(WorkspaceRelativePath.Comparer);

                string current = start;

                while (edges.TryGetValue(current, out string? next))
                {
                    Checkpoint();

                    if (!path.Add(current))
                    {
                        throw Invalid(
                            "rename_cycle",
                            "Rename cycles are not supported.");
                    }

                    if (path.Contains(next))
                    {
                        throw Invalid(
                            "rename_cycle",
                            "Rename cycles are not supported.");
                    }

                    current = next;
                }

                completed.UnionWith(path);
            }

        }

        private static IReadOnlyList<string> SplitLogicalLines(
            string patch,
            CancellationToken cancellationToken)
        {

            List<string> lines = [];

            int start = 0;

            for (int index = 0; index < patch.Length; index++)
            {
                if ((index & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (patch[index] is not ('\r' or '\n'))
                {
                    continue;
                }

                lines.Add(patch[start..index]);

                if (patch[index] == '\r'
                    && index + 1 < patch.Length
                    && patch[index + 1] == '\n')
                {
                    index++;
                }

                start = index + 1;
            }

            if (start < patch.Length)
            {
                lines.Add(patch[start..]);
            }

            cancellationToken.ThrowIfCancellationRequested();

            return new ReadOnlyCollection<string>(lines);

        }

        private static void ParseHunkHeader(
            string header,
            out int oldStart,
            out int oldCount,
            out int newStart,
            out int newCount,
            out string? section)
        {

            if (!header.StartsWith("@@ -", StringComparison.Ordinal))
            {
                throw Invalid(
                    "invalid_hunk",
                    "The hunk header is malformed.");
            }

            int terminator = header.IndexOf(" @@", 4, StringComparison.Ordinal);

            if (terminator < 0)
            {
                throw Invalid(
                    "invalid_hunk",
                    "The hunk header is malformed.");
            }

            string ranges = header[3..terminator];

            int separator = ranges.IndexOf(' ');

            if (separator <= 0
                || separator == ranges.Length - 1
                || ranges[0] != '-'
                || ranges[separator + 1] != '+'
                || ranges[(separator + 1)..].Contains(' ', StringComparison.Ordinal))
            {
                throw Invalid(
                    "invalid_hunk",
                    "The hunk ranges are malformed.");
            }

            ParseRange(
                ranges.AsSpan(1, separator - 1),
                out oldStart,
                out oldCount);

            ParseRange(
                ranges.AsSpan(separator + 2),
                out newStart,
                out newCount);

            section = header[(terminator + 3)..].TrimStart();

            if (section.Length == 0)
            {
                section = null;
            }

        }

        private static void ParseRange(
            ReadOnlySpan<char> range,
            out int start,
            out int count)
        {

            int comma = range.IndexOf(',');

            ReadOnlySpan<char> startSpan =
                comma < 0 ? range : range[..comma];

            ReadOnlySpan<char> countSpan =
                comma < 0 ? "1" : range[(comma + 1)..];

            if (!int.TryParse(
                    startSpan,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out start)
                || !int.TryParse(
                    countSpan,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out count)
                || start < 0
                || count < 0
                || (start == 0 && count != 0)
                || (start > 0 && count == 0 && comma < 0))
            {
                throw Invalid(
                    "invalid_hunk",
                    "A hunk range is invalid.");
            }

        }

        private static (string OldPath, string NewPath) ParseGitHeaderPaths(
            string value)
        {

            int index = 0;

            string oldPath = ParseToken(value, ref index);

            SkipHorizontalWhitespace(value, ref index);

            string newPath = ParseToken(value, ref index);

            SkipHorizontalWhitespace(value, ref index);

            if (index != value.Length)
            {
                throw Invalid(
                    "invalid_patch",
                    "The Git diff header contains extra path data.");
            }

            return (oldPath, newPath);

        }

        private static string ParseHeaderPath(
            string value,
            PathSide side)
        {

            string path;

            if (value.StartsWith('"'))
            {
                int index = 0;

                path = ParseToken(value, ref index);

                if (index < value.Length
                    && value[index] != '\t'
                    && !string.IsNullOrWhiteSpace(value[index..]))
                {
                    throw Invalid(
                        "invalid_path",
                        "A quoted file header path contains trailing data.");
                }
            }
            else
            {
                int tab = value.IndexOf('\t');

                path = (tab < 0 ? value : value[..tab]).TrimEnd();
            }

            return NormalizePath(path, side);

        }

        private static string ParseMetadataPath(string value)
        {

            if (!value.StartsWith('"'))
            {
                return value;
            }

            int index = 0;

            string path = ParseToken(value, ref index);

            SkipHorizontalWhitespace(value, ref index);

            if (index != value.Length)
            {
                throw Invalid(
                    "invalid_path",
                    "A quoted metadata path contains trailing data.");
            }

            return path;

        }

        private static string ParseToken(
            string value,
            ref int index)
        {

            SkipHorizontalWhitespace(value, ref index);

            if (index >= value.Length)
            {
                throw Invalid(
                    "invalid_path",
                    "A required path is missing.");
            }

            if (value[index] != '"')
            {
                int start = index;

                while (index < value.Length
                    && value[index] is not (' ' or '\t'))
                {
                    index++;
                }

                return value[start..index];
            }

            index++;

            using MemoryStream bytes = new();

            char[] chars = new char[2];

            byte[] encoded = new byte[8];

            while (index < value.Length)
            {
                char character = value[index++];

                if (character == '"')
                {
                    try
                    {
                        return StrictUtf8.GetString(bytes.GetBuffer().AsSpan(
                            0,
                            checked((int)bytes.Length)));
                    }
                    catch (DecoderFallbackException)
                    {
                        throw Invalid(
                            "invalid_path",
                            "A quoted path contains invalid UTF-8 bytes.");
                    }
                }

                if (character != '\\')
                {
                    int charCount = 1;

                    chars[0] = character;

                    if (char.IsHighSurrogate(character)
                        && index < value.Length
                        && char.IsLowSurrogate(value[index]))
                    {
                        chars[1] = value[index++];

                        charCount = 2;
                    }

                    int byteCount;

                    try
                    {
                        byteCount = StrictUtf8.GetBytes(
                            chars.AsSpan(0, charCount),
                            encoded.AsSpan());
                    }
                    catch (EncoderFallbackException)
                    {
                        throw Invalid(
                            "invalid_path",
                            "A quoted path contains invalid Unicode.");
                    }

                    bytes.Write(encoded.AsSpan(0, byteCount));

                    continue;
                }

                if (index >= value.Length)
                {
                    throw Invalid(
                        "invalid_path",
                        "A quoted path ends with an incomplete escape.");
                }

                char escaped = value[index++];

                if (escaped is >= '0' and <= '7')
                {
                    int octal = escaped - '0';

                    int digits = 1;

                    while (digits < 3
                        && index < value.Length
                        && value[index] is >= '0' and <= '7')
                    {
                        octal = (octal * 8) + value[index] - '0';

                        index++;

                        digits++;
                    }

                    if (octal > byte.MaxValue)
                    {
                        throw Invalid(
                            "invalid_path",
                            "A quoted path contains an out-of-range octal escape.");
                    }

                    bytes.WriteByte((byte)octal);

                    continue;
                }

                byte escapedByte = escaped switch
                {
                    'a' => 0x07,
                    'b' => 0x08,
                    'f' => 0x0C,
                    'n' => 0x0A,
                    'r' => 0x0D,
                    't' => 0x09,
                    'v' => 0x0B,
                    '\\' => (byte)'\\',
                    '"' => (byte)'"',
                    _ => throw Invalid(
                        "invalid_path",
                        "A quoted path contains an unsupported escape."),
                };

                bytes.WriteByte(escapedByte);
            }

            throw Invalid(
                "invalid_path",
                "A quoted path is not terminated.");

        }

        private static string NormalizePath(
            string path,
            PathSide side)
        {

            if (string.Equals(path, "/dev/null", StringComparison.Ordinal))
            {
                return null!;
            }

            if (string.IsNullOrWhiteSpace(path)
                || path.Contains('\0', StringComparison.Ordinal)
                || path.Any(static character =>
                    character < ' ' || character == '\u007F'))
            {
                throw Invalid(
                    "invalid_path",
                    "A file path is empty or contains a forbidden character.");
            }

            string slashPath = path.Replace('\\', '/');

            if (slashPath.StartsWith("/", StringComparison.Ordinal)
                || slashPath.StartsWith("//", StringComparison.Ordinal)
                || (slashPath.Length >= 2
                    && char.IsAsciiLetter(slashPath[0])
                    && slashPath[1] == ':'))
            {
                throw Invalid(
                    "absolute_path",
                    "Absolute file paths are not supported.");
            }

            if (side == PathSide.Old
                && slashPath.StartsWith("a/", StringComparison.Ordinal))
            {
                slashPath = slashPath[2..];
            }
            else if (side == PathSide.New
                && slashPath.StartsWith("b/", StringComparison.Ordinal))
            {
                slashPath = slashPath[2..];
            }

            if (!WorkspaceRelativePath.TryNormalize(
                    slashPath,
                    out string? normalized))
            {
                throw Invalid(
                    "invalid_path",
                    "A file path is not a safe normalized workspace-relative path.");
            }

            return normalized;

        }

        private static void ValidateIndexMetadata(string value)
        {

            string[] fields = value.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length is < 1 or > 2)
            {
                throw Invalid(
                    "unsupported_metadata",
                    "Index metadata is malformed.");
            }

            int separator = fields[0].IndexOf("..", StringComparison.Ordinal);

            if (separator <= 0
                || separator == fields[0].Length - 2
                || fields[0].IndexOf("..", separator + 2, StringComparison.Ordinal) >= 0
                || !IsHex(fields[0].AsSpan(0, separator))
                || !IsHex(fields[0].AsSpan(separator + 2)))
            {
                throw Invalid(
                    "unsupported_metadata",
                    "Index metadata is malformed.");
            }

            if (fields.Length == 2)
            {
                _ = ParseMode(fields[1]);
            }

        }

        private static bool IsHex(ReadOnlySpan<char> value)
        {

            if (value.Length == 0)
            {
                return false;
            }

            foreach (char character in value)
            {
                if (!char.IsAsciiHexDigit(character))
                {
                    return false;
                }
            }

            return true;

        }

        private static ParsedMode ParseMode(string value)
        {

            if (value.Length != 6
                || value.Any(static character =>
                    character is < '0' or > '7'))
            {
                throw Invalid(
                    "unsupported_metadata",
                    "A Git file mode is malformed.");
            }

            int numeric = Convert.ToInt32(value, 8);

            int fileType = numeric & 0xF000;

            if (fileType == 0xE000)
            {
                throw Invalid(
                    "submodule_patch",
                    "Submodule unified-diff records are not supported.");
            }

            if (fileType == 0xA000)
            {
                throw Invalid(
                    "symlink_patch",
                    "Symbolic-link unified-diff records are not supported.");
            }

            if (fileType != 0x8000)
            {
                throw Invalid(
                    "unsupported_metadata",
                    "Only regular-file Git modes are supported.");
            }

            return new ParsedMode((UnixFileMode)(numeric & 0xFFF));

        }

        private static void ValidateSimilarity(string value)
        {

            if (!value.EndsWith('%')
                || !int.TryParse(
                    value.AsSpan(0, value.Length - 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int similarity)
                || similarity is < 0 or > 100)
            {
                throw Invalid(
                    "unsupported_metadata",
                    "Rename similarity metadata is malformed.");
            }

        }

        private static bool IsBinaryMarker(string line) =>
            line == "GIT binary patch"
            || line.StartsWith("Binary files ", StringComparison.Ordinal)
            || line.StartsWith("Binary file ", StringComparison.Ordinal);

        private static void SkipHorizontalWhitespace(
            string value,
            ref int index)
        {

            while (index < value.Length
                && value[index] is ' ' or '\t')
            {
                index++;
            }

        }

        private static UnifiedDiffParseException Invalid(
            string code,
            string message) =>
            new(code, message);

        private void Checkpoint() =>
            _cancellationToken.ThrowIfCancellationRequested();

        private sealed class FileBuilder
        {
            internal string? GitOldPath { get; set; }

            internal string? GitNewPath { get; set; }

            internal string? OldHeaderPath { get; set; }

            internal string? NewHeaderPath { get; set; }

            internal string? RenameFrom { get; set; }

            internal string? RenameTo { get; set; }

            internal bool HasUnifiedHeaders { get; set; }

            internal bool HasNewFileMode { get; set; }

            internal bool HasDeletedFileMode { get; set; }

            internal bool HasSimilarity { get; set; }

            internal bool SawIndex { get; set; }

            internal UnixFileMode? NewFileMode { get; set; }

            internal int LastMetadataOrder { get; set; } = -1;

            internal HashSet<MetadataKind> MetadataKinds { get; } = [];

            internal List<UnifiedDiffHunk> Hunks { get; } = [];
        }

        private readonly record struct ParsedMode(UnixFileMode UnixMode);

        private enum PathSide
        {
            Old,
            New,
            Metadata,
        }

        private enum MetadataKind
        {
            NewFileMode,
            DeletedFileMode,
            Similarity,
            RenameFrom,
            RenameTo,
            Index,
        }
    }

    private sealed class UnifiedDiffParseException(
        string code,
        string message) : Exception(message)
    {
        internal string Code { get; } = code;
    }
}
