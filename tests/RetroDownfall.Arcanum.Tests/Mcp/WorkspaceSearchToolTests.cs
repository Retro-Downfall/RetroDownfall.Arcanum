using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Mcp;

[Collection("WorkspacePathPolicy")]
public sealed class WorkspaceSearchToolTests : IAsyncLifetime
{

    private TempWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {

        _workspace = new TempWorkspace();

        await _workspace.InitializeAsync();

    }

    public async Task DisposeAsync()
    {

        await _workspace.DisposeAsync();

    }

    [Fact]
    public async Task Literal_search_uses_explicit_ordinal_case_modes()
    {

        _workspace.WriteFile("case.txt", "Alpha\nalpha\nALPHA");

        WorkspaceSearchToolResultEnvelope sensitive = await SearchAsync(
            "alpha",
            WorkspaceSearchMode.Literal,
            caseSensitive: true);

        WorkspaceSearchToolResultEnvelope insensitive = await SearchAsync(
            "alpha",
            WorkspaceSearchMode.Literal,
            caseSensitive: false);

        Assert.Equal([2], sensitive.Matches.Select(static match => match.Line));
        Assert.Equal([1, 2, 3], insensitive.Matches.Select(static match => match.Line));
        Assert.All(insensitive.Matches, static match => Assert.Equal(1, match.Column));

    }

    [Fact]
    public async Task Search_is_line_scoped_and_numbers_mixed_newlines_from_one()
    {

        _workspace.WriteFile(
            "mixed.txt",
            "first\r\nsecond\nthird\rfourth");

        WorkspaceSearchToolResultEnvelope noCrossLine = await SearchAsync(
            @"first\s+second",
            WorkspaceSearchMode.Regex,
            caseSensitive: true);

        WorkspaceSearchToolResultEnvelope fourth = await SearchAsync(
            "fourth",
            WorkspaceSearchMode.Literal,
            caseSensitive: true);

        Assert.Equal("no_match", noCrossLine.Status);
        Assert.Empty(noCrossLine.Matches);
        Assert.Single(fourth.Matches);
        Assert.Equal(4, fourth.Matches[0].Line);
        Assert.Equal(1, fourth.Matches[0].Column);

    }

    [Fact]
    public async Task Search_handles_unicode_text_and_preserves_valid_compact_previews()
    {

        _workspace.WriteFile(
            "unicode.txt",
            "prefix prefix prefix ✨ MAGIC suffix suffix suffix");

        WorkspaceSearchSettings settings = DefaultSettings() with
        {
            MaxPreviewChars = 20,
        };

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "magic",
            WorkspaceSearchMode.Literal,
            caseSensitive: false,
            settings);

        WorkspaceSearchToolResultItem match = Assert.Single(result.Matches);

        Assert.Equal(1, match.Line);
        Assert.Equal(24, match.Column);
        Assert.InRange(match.Preview.Length, 1, 20);
        Assert.Contains("MAGIC", match.Preview, StringComparison.Ordinal);
        Assert.Contains('…', match.Preview);

    }

    [Fact]
    public void Runtime_regex_factory_uses_non_backtracking_without_compilation()
    {

        RuntimeWorkspaceRegexCreationResult created = RuntimeWorkspaceRegexFactory.Create(
            @"magic\d+",
            caseSensitive: false,
            TimeSpan.FromMilliseconds(75));

        Assert.True(created.Success);
        Assert.False(created.FallbackAttempted);
        Assert.Equal(RuntimeWorkspaceRegexEngine.NonBacktracking, created.Engine);
        Assert.NotNull(created.Regex);
        Assert.Matches(created.Regex!, "MAGIC42");
        Assert.Equal(TimeSpan.FromMilliseconds(75), created.Regex.MatchTimeout);
        Assert.True(created.Regex.Options.HasFlag(RegexOptions.CultureInvariant));
        Assert.True(created.Regex.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.True(created.Regex.Options.HasFlag(RegexOptions.IgnoreCase));
        Assert.False(created.Regex.Options.HasFlag(RegexOptions.Compiled));

    }

    [Theory]
    [InlineData(@"(\w+)\s+\1", "echo echo")]
    [InlineData(@"magic(?=\d+)", "magic42")]
    public void Runtime_regex_factory_falls_back_only_for_non_backtracking_unsupported_features(
        string pattern,
        string input)
    {

        RuntimeWorkspaceRegexCreationResult created = RuntimeWorkspaceRegexFactory.Create(
            pattern,
            caseSensitive: true,
            TimeSpan.FromMilliseconds(75));

        Assert.True(created.Success);
        Assert.True(created.FallbackAttempted);
        Assert.Equal(RuntimeWorkspaceRegexEngine.Interpreted, created.Engine);
        Assert.NotNull(created.Regex);
        Assert.Matches(created.Regex!, input);
        Assert.True(created.Regex.Options.HasFlag(RegexOptions.CultureInvariant));
        Assert.False(created.Regex.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.False(created.Regex.Options.HasFlag(RegexOptions.Compiled));

    }

    [Fact]
    public void Runtime_regex_factory_returns_invalid_pattern_without_fallback()
    {

        RuntimeWorkspaceRegexCreationResult created = RuntimeWorkspaceRegexFactory.Create(
            "[",
            caseSensitive: true,
            TimeSpan.FromMilliseconds(75));

        Assert.False(created.Success);
        Assert.False(created.FallbackAttempted);
        Assert.Null(created.Engine);
        Assert.Null(created.Regex);
        Assert.Equal("invalid_pattern", created.ErrorCode);

    }

    [Fact]
    public async Task Invalid_regex_is_a_normal_structured_outcome()
    {

        _workspace.WriteFile("sample.txt", "sample");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "[",
            WorkspaceSearchMode.Regex,
            caseSensitive: true);

        Assert.Equal("invalid_pattern", result.Status);
        Assert.Equal("invalid_pattern", result.Code);
        Assert.Empty(result.Matches);
        Assert.Equal(0, result.FilesSearched);

    }

    [Fact]
    public async Task Regex_timeout_is_a_normal_structured_outcome()
    {

        _workspace.WriteFile("expensive.txt", new string('a', 100_000) + "!");

        WorkspaceSearchSettings settings = DefaultSettings() with
        {
            RegexTimeoutMilliseconds = 1,
        };

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            @"^(a+)+(?=b)",
            WorkspaceSearchMode.Regex,
            caseSensitive: true,
            settings);

        Assert.Equal("timed_out", result.Status);
        Assert.Equal("regex_timeout", result.Code);
        Assert.True(result.RegexEngine == "interpreted");

    }

    [Fact]
    public async Task Regex_search_preserves_next_match_continuation_for_zero_width_G_anchor()
    {
        const string line = "abc";
        const string pattern = @"\G(?=.)";
        _workspace.WriteFile("stateful.txt", line);

        RuntimeWorkspaceRegexCreationResult created =
            RuntimeWorkspaceRegexFactory.Create(
                pattern,
                caseSensitive: true,
                TimeSpan.FromMilliseconds(250));
        List<int> expectedColumns = [];

        for (Match match = created.Regex!.Match(line);
             match.Success;
             match = match.NextMatch())
        {
            expectedColumns.Add(match.Index + 1);
        }

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            pattern,
            WorkspaceSearchMode.Regex,
            caseSensitive: true);

        Assert.Single(expectedColumns);
        Assert.Equal(
            expectedColumns,
            result.Matches.Select(static match => match.Column));
    }

    [Fact]
    public async Task Deadline_sliced_interpreted_regex_remains_valid()
    {
        _workspace.WriteFile("stateful.txt", "abc");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            @"\G(?=.)",
            WorkspaceSearchMode.Regex,
            caseSensitive: true,
            DefaultSettings() with
            {
                MaxElapsedMilliseconds = 100,
                RegexTimeoutMilliseconds = 500,
            },
            globs: new DelayedReadOnlyList("**/*", TimeSpan.FromMilliseconds(25)));

        WorkspaceSearchToolResultItem match = Assert.Single(result.Matches);

        Assert.Equal("ok", result.Status);
        Assert.Equal("interpreted", result.RegexEngine);
        Assert.Equal(1, match.Column);
    }

    [Fact]
    public async Task Search_normalizes_globs_and_filters_extensions_under_explicit_root()
    {

        _workspace.WriteFile("src/root.cs", "needle");
        _workspace.WriteFile("src/nested/child.CS", "needle");
        _workspace.WriteFile("src/nested/child.txt", "needle");
        _workspace.WriteFile("tests/ignored.cs", "needle");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            root: @"src\.",
            globs: [@"**\*"],
            extensions: ["CS"]);

        Assert.Equal(
            ["src/nested/child.CS", "src/root.cs"],
            result.Matches.Select(static match => match.Path));
        Assert.Equal(1, result.SkippedFilteredFileCount);

    }

    [Fact]
    public async Task Search_glob_question_and_star_wildcards_match_complete_path_segments()
    {
        _workspace.WriteFile("src/test/alpha.cs", "needle");
        _workspace.WriteFile("src/tests/alpha.cs", "needle");
        _workspace.WriteFile("src/test/beta.cs", "needle");
        _workspace.WriteFile("src/test/alpha.txt", "needle");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            globs: ["src/?est/a*.cs"]);

        WorkspaceSearchToolResultItem match = Assert.Single(result.Matches);

        Assert.Equal("src/test/alpha.cs", match.Path);
        Assert.Equal(3, result.SkippedFilteredFileCount);
    }

    [Fact]
    public async Task Search_recursive_glob_matches_root_and_nested_files()
    {
        _workspace.WriteFile("root.cs", "needle");
        _workspace.WriteFile("nested/child.cs", "needle");
        _workspace.WriteFile("nested/child.txt", "needle");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            globs: ["**/*.cs"]);

        Assert.Equal(
            ["nested/child.cs", "root.cs"],
            result.Matches.Select(static match => match.Path));
        Assert.Equal(1, result.SkippedFilteredFileCount);
    }

    [Fact]
    public async Task Search_applies_filters_before_the_file_cap()
    {

        _workspace.WriteFile("a-ignored.txt", "needle");

        _workspace.WriteFile("z-target.cs", "needle");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            DefaultSettings() with { MaxFiles = 1 },
            globs: ["**/*.cs"]);

        WorkspaceSearchToolResultItem match = Assert.Single(result.Matches);

        Assert.Equal("z-target.cs", match.Path);

        Assert.NotEqual("max_files", result.Code);

        Assert.Equal(1, result.SkippedFilteredFileCount);

    }

    [Fact]
    public async Task Search_parent_traversal_glob_returns_structured_invalid_filter_error()
    {
        _workspace.WriteFile("sample.txt", "needle");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            globs: ["../*.txt"]);

        Assert.Equal("invalid_request", result.Status);
        Assert.Equal("invalid_filter", result.Code);
        Assert.Empty(result.Matches);
        Assert.Equal(0, result.FilesSearched);
    }

    [Fact]
    public async Task Search_skips_and_reports_binary_files()
    {

        _workspace.WriteFile("text.txt", "needle");
        await File.WriteAllBytesAsync(
            Path.Combine(_workspace.Root, "binary.bin"),
            [0x6E, 0x65, 0x65, 0x64, 0x6C, 0x65, 0x00, 0xFF]);

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true);

        Assert.Single(result.Matches);
        Assert.Equal("text.txt", result.Matches[0].Path);
        Assert.Equal(1, result.SkippedBinaryFileCount);

    }

    [Fact]
    public async Task Search_is_deterministically_ordered_by_path_line_and_column()
    {

        _workspace.WriteFile("z.txt", "needle");
        _workspace.WriteFile("a.txt", "needle needle\nneedle");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true);

        Assert.Equal(
            [
                ("a.txt", 1, 1),
                ("a.txt", 1, 8),
                ("a.txt", 2, 1),
                ("z.txt", 1, 1),
            ],
            result.Matches.Select(static match => (match.Path, match.Line, match.Column)));

    }

    [Fact]
    public async Task Search_enforces_pattern_and_match_caps()
    {

        _workspace.WriteFile("matches.txt", "x x x x");

        WorkspaceSearchToolResultEnvelope patternLimited = await SearchAsync(
            "toolong",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            DefaultSettings() with { MaxPatternChars = 3 });

        WorkspaceSearchToolResultEnvelope matchLimited = await SearchAsync(
            "x",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            DefaultSettings() with { MaxMatches = 2 });

        Assert.Equal("invalid_pattern", patternLimited.Status);
        Assert.Equal("pattern_too_long", patternLimited.Code);
        Assert.Equal("capped", matchLimited.Status);
        Assert.Equal("max_matches", matchLimited.Code);
        Assert.Equal(2, matchLimited.Matches.Length);
        Assert.True(matchLimited.Truncated);

    }

    [Fact]
    public async Task Search_enforces_file_and_traversal_caps()
    {

        _workspace.WriteFile("a.txt", "needle");
        _workspace.WriteFile("b.txt", "needle");

        WorkspaceSearchToolResultEnvelope fileLimited = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            DefaultSettings() with { MaxFiles = 1 });

        WorkspaceSearchToolResultEnvelope stepLimited = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            DefaultSettings() with { MaxTraversalSteps = 1 });

        Assert.Equal("capped", fileLimited.Status);
        Assert.Equal("max_files", fileLimited.Code);
        Assert.Single(fileLimited.Matches);
        Assert.Equal("capped", stepLimited.Status);
        Assert.Equal("max_traversal_steps", stepLimited.Code);
        Assert.Equal(1, stepLimited.TraversalSteps);

    }

    [Fact]
    public async Task Search_enforces_total_byte_cap_before_reading_the_next_file()
    {

        _workspace.WriteFile("a.txt", "needle" + new string('a', 700));
        _workspace.WriteFile("b.txt", "needle" + new string('b', 700));

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            DefaultSettings() with { MaxBytes = 1_024 });

        Assert.Equal("capped", result.Status);
        Assert.Equal("max_bytes", result.Code);
        Assert.Single(result.Matches);
        Assert.Equal("a.txt", result.Matches[0].Path);
        Assert.InRange(result.BytesSearched, 700, 1_024);

    }

    [Fact]
    public async Task Search_enforces_monotonic_elapsed_cap()
    {

        _workspace.WriteFile("a.txt", "needle");

        WorkspaceSearchEngine engine = new(
            DefaultSettings() with { MaxElapsedMilliseconds = 100 },
            new IncrementingTimeProvider());

        WorkspaceSearchToolResultEnvelope result = await engine.SearchAsync(
            _workspace.Root,
            new WorkspaceSearchRequest
            {
                Pattern = "needle",
                Mode = WorkspaceSearchMode.Literal,
                CaseSensitive = true,
            },
            CancellationToken.None);

        Assert.Equal("capped", result.Status);
        Assert.Equal("max_elapsed", result.Code);
        Assert.True(result.Truncated);

    }

    [Fact]
    public async Task Search_elapsed_budget_starts_before_filter_setup()
    {
        _workspace.WriteFile("a.txt", "needle");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            DefaultSettings() with { MaxElapsedMilliseconds = 100 },
            globs: new DelayedReadOnlyList("**/*", TimeSpan.FromMilliseconds(150)));

        Assert.Equal("capped", result.Status);
        Assert.Equal("max_elapsed", result.Code);
        Assert.Equal(0, result.TraversalSteps);
    }

    [Fact]
    public async Task Search_cancellation_interrupts_filter_setup_cooperatively()
    {
        _workspace.WriteFile("a.txt", "needle");
        using CancellationTokenSource cancellation = new();
        CancelDuringEnumerationReadOnlyList globs = new(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SearchAsync(
                "needle",
                WorkspaceSearchMode.Literal,
                caseSensitive: true,
                globs: globs,
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Search_regex_operation_never_outlives_remaining_elapsed_budget()
    {
        _workspace.WriteFile("expensive.txt", new string('a', 100_000) + "!");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            @"^(a+)+(?=b)",
            WorkspaceSearchMode.Regex,
            caseSensitive: true,
            DefaultSettings() with
            {
                MaxElapsedMilliseconds = 100,
                RegexTimeoutMilliseconds = 500,
            },
            globs: new DelayedReadOnlyList("**/*", TimeSpan.FromMilliseconds(75)));

        Assert.Equal("capped", result.Status);
        Assert.Equal("max_elapsed", result.Code);
    }

    [Theory]
    [InlineData((int)WorkspaceSearchPhase.Traversal)]
    [InlineData((int)WorkspaceSearchPhase.GlobMatch)]
    [InlineData((int)WorkspaceSearchPhase.Read)]
    [InlineData((int)WorkspaceSearchPhase.Decode)]
    [InlineData((int)WorkspaceSearchPhase.LiteralMatch)]
    [InlineData((int)WorkspaceSearchPhase.RegexMatch)]
    public async Task Search_propagates_in_flight_cancellation(
        int phaseValue)
    {
        WorkspaceSearchPhase phase = (WorkspaceSearchPhase)phaseValue;

        for (int index = 0; index < 16; index++)
        {
            _workspace.WriteFile(
                $"file-{index:D2}.txt",
                phase == WorkspaceSearchPhase.RegexMatch
                    ? string.Join(' ', Enumerable.Repeat("needle", 64))
                    : new string('x', 256 * 1024));
        }

        using CancellationTokenSource cancellation = new();
        CancellingSearchObserver observer = new(phase, cancellation);
        WorkspaceSearchEngine engine = new(
            DefaultSettings(),
            TimeProvider.System,
            observer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.SearchAsync(
                _workspace.Root,
                new WorkspaceSearchRequest
                {
                    Pattern = phase == WorkspaceSearchPhase.RegexMatch
                        ? "needle"
                        : "absent",
                    Mode = phase == WorkspaceSearchPhase.RegexMatch
                        ? WorkspaceSearchMode.Regex
                        : WorkspaceSearchMode.Literal,
                    CaseSensitive = true,
                    Globs = phase == WorkspaceSearchPhase.GlobMatch
                        ? ["**/*"]
                        : [],
                },
                cancellation.Token));

        Assert.True(observer.MatchingCheckpointCount >= 2);
    }

    [Fact]
    public async Task Search_skips_directory_symlink_cycles_rejects_escapes_and_deduplicates_file_identity()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            return;
        }

        string target = _workspace.WriteFile("z-target.txt", "needle");
        File.CreateSymbolicLink(Path.Combine(_workspace.Root, "a-alias.txt"), target);

        string loopDirectory = _workspace.CreateSubdir("loop");
        Directory.CreateSymbolicLink(Path.Combine(loopDirectory, "cycle"), _workspace.Root);

        string outside = Path.Combine(
            Path.GetTempPath(),
            $"arcanum-search-outside-{Guid.NewGuid():N}.txt");

        try
        {

            await File.WriteAllTextAsync(outside, "needle");
            File.CreateSymbolicLink(Path.Combine(_workspace.Root, "escape.txt"), outside);

            WorkspaceSearchToolResultEnvelope result = await SearchAsync(
                "needle",
                WorkspaceSearchMode.Literal,
                caseSensitive: true);

            WorkspaceSearchToolResultItem match = Assert.Single(result.Matches);

            Assert.Equal("a-alias.txt", match.Path);
            Assert.Equal(1, result.SkippedDirectorySymlinkCount);
            Assert.Equal(1, result.SkippedEscapingSymlinkCount);
            Assert.Equal(1, result.SkippedDuplicateFileCount);

        }
        finally
        {

            File.Delete(outside);

        }

    }

    [Fact]
    public async Task Search_rejects_files_with_multiple_hard_links()
    {

        if (!OperatingSystem.IsMacOS()
            && !OperatingSystem.IsLinux()
            && !OperatingSystem.IsWindows())
        {

            return;

        }

        string target = _workspace.WriteFile("linked.txt", "needle");

        string alias = Path.Combine(_workspace.Root, "linked-alias.txt");

        Assert.True(HardLinkTestSupport.TryCreate(alias, target));

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true);

        Assert.Empty(result.Matches);

        Assert.True(result.SkippedUnreadableFileCount >= 2);

    }

    [Fact]
    public async Task Search_rejects_fifo_before_opening_it()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string fifo = Path.Combine(_workspace.Root, "blocked.fifo");

        Assert.Equal(0, UnixNative.CreateFifo(fifo, 0x180U));

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            cancellationToken: deadline.Token);

        Assert.Equal("no_match", result.Status);

        Assert.Equal(1, result.SkippedUnreadableFileCount);

    }

    [Fact]
    public async Task Search_rejects_link_count_change_after_open_and_read()
    {

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {

            return;

        }

        string target = _workspace.WriteFile("link-race.txt", "needle");

        string alias = Path.Combine(_workspace.Root, "link-race-alias.txt");

        HardLinkingSearchObserver observer = new(target, alias);

        WorkspaceSearchToolResultEnvelope result = await new WorkspaceSearchEngine(
            DefaultSettings(),
            TimeProvider.System,
            observer)
            .SearchAsync(
                _workspace.Root,
                new WorkspaceSearchRequest
                {
                    Pattern = "needle",
                    Mode = WorkspaceSearchMode.Literal,
                    CaseSensitive = true,
                },
                CancellationToken.None);

        Assert.True(observer.Linked);

        Assert.Empty(result.Matches);

        Assert.Equal(1, result.SkippedUnreadableFileCount);

    }

    [Fact]
    public async Task Search_propagates_caller_cancellation()
    {

        _workspace.WriteFile("a.txt", "needle");

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SearchAsync(
                "needle",
                WorkspaceSearchMode.Literal,
                caseSensitive: true,
                cancellationToken: cancellation.Token));

    }

    private Task<WorkspaceSearchToolResultEnvelope> SearchAsync(
        string pattern,
        WorkspaceSearchMode mode,
        bool caseSensitive,
        WorkspaceSearchSettings? settings = null,
        string? root = null,
        IReadOnlyList<string>? globs = null,
        IReadOnlyList<string>? extensions = null,
        CancellationToken cancellationToken = default)
    {

        WorkspaceSearchEngine engine = new(settings ?? DefaultSettings());

        return engine.SearchAsync(
            _workspace.Root,
            new WorkspaceSearchRequest
            {
                Pattern = pattern,
                Mode = mode,
                CaseSensitive = caseSensitive,
                Root = root,
                Globs = globs ?? [],
                Extensions = extensions ?? [],
            },
            cancellationToken);

    }

    private static WorkspaceSearchSettings DefaultSettings() =>
        new()
        {
            MaxPatternChars = 4_096,
            RegexTimeoutMilliseconds = 250,
            MaxElapsedMilliseconds = 10_000,
            MaxFiles = 2_000,
            MaxBytes = 32L * 1024L * 1024L,
            MaxTraversalSteps = 100_000,
            MaxMatches = 1_000,
            MaxPreviewChars = 512,
        };

    private sealed class IncrementingTimeProvider : TimeProvider
    {

        private long _timestamp;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Interlocked.Add(ref _timestamp, 1_000);

    }

    private sealed class HardLinkingSearchObserver(
        string target,
        string alias) : IWorkspaceSearchProgressObserver
    {
        public bool Linked { get; private set; }

        public void OnCheckpoint(WorkspaceSearchPhase phase)
        {

            if (phase != WorkspaceSearchPhase.Read || Linked)
            {

                return;

            }

            Linked = HardLinkTestSupport.TryCreate(alias, target);

            Assert.True(Linked);

        }
    }

    private static class UnixNative
    {
        [DllImport(
            "libc",
            EntryPoint = "mkfifo",
            SetLastError = true)]
        internal static extern int CreateFifo(string path, uint mode);
    }

    private sealed class DelayedReadOnlyList(
        string value,
        TimeSpan delay) : IReadOnlyList<string>
    {
        public int Count => 1;

        public string this[int index] => index == 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<string> GetEnumerator()
        {
            Thread.Sleep(delay);
            yield return value;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class CancelDuringEnumerationReadOnlyList(
        CancellationTokenSource cancellation) : IReadOnlyList<string>
    {
        public int Count => 2;

        public string this[int index] => index switch
        {
            0 => "**/*",
            1 => "*.cs",
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        public IEnumerator<string> GetEnumerator() => new Enumerator(cancellation);

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();

        private sealed class Enumerator(
            CancellationTokenSource cancellation) : IEnumerator<string>
        {
            private bool _started;

            public string Current
            {
                get
                {
                    cancellation.Cancel();
                    return "**/*";
                }
            }

            object System.Collections.IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (!_started)
                {
                    _started = true;
                    return true;
                }

                throw new InvalidOperationException(
                    "The search continued enumerating filters after cancellation.");
            }

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
            }
        }
    }

    private sealed class CancellingSearchObserver(
        WorkspaceSearchPhase phase,
        CancellationTokenSource cancellation) : IWorkspaceSearchProgressObserver
    {
        public int MatchingCheckpointCount { get; private set; }

        public void OnCheckpoint(WorkspaceSearchPhase currentPhase)
        {
            if (currentPhase != phase)
            {
                return;
            }

            MatchingCheckpointCount++;

            if (MatchingCheckpointCount == 2)
            {
                cancellation.Cancel();
            }
        }
    }

}
