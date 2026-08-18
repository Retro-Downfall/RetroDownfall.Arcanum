using System.Security.Cryptography;

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
    public async Task Search_previews_trim_astral_characters_whole_at_the_ellipsis_boundary()
    {

        string astral = char.ConvertFromUtf32(0x1F600);

        // Line 1 opens the window on the high half of a pair, line 2 closes it on the low half, and
        // line 3 makes the leading-low-surrogate guard fire.
        _workspace.WriteFile(
            "astral.txt",
            string.Join(
                '\n',
                new string('a', 10) + astral + "cccccMAGIC" + new string('z', 20),
                new string('a', 10)
                    + "bbcccccMAGIC"
                    + new string('y', 6)
                    + astral
                    + new string('z', 20),
                new string('a', 9) + astral + "ccccccMAGIC" + new string('z', 20)));

        WorkspaceSearchSettings settings = DefaultSettings() with
        {
            MaxPreviewChars = 20,
        };

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "MAGIC",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            settings);

        Assert.Equal(3, result.Matches.Length);
        Assert.All(
            result.Matches,
            static match =>
            {

                Assert.Contains("MAGIC", match.Preview, StringComparison.Ordinal);
                Assert.InRange(match.Preview.Length, 1, 20);
                AssertNoUnpairedSurrogate(match.Preview);

            });

        // Stepping the window past a leading low surrogate must not cost a character of the budget.
        Assert.Equal(20, result.Matches[2].Preview.Length);

    }

    private static void AssertNoUnpairedSurrogate(string value)
    {

        for (int index = 0; index < value.Length; index++)
        {

            if (char.IsHighSurrogate(value[index]))
            {

                Assert.True(
                    index + 1 < value.Length
                    && char.IsLowSurrogate(value[index + 1]),
                    $"Unpaired high surrogate at index {index} of '{value}'.");

                index++;

                continue;

            }

            Assert.False(
                char.IsLowSurrogate(value[index]),
                $"Unpaired low surrogate at index {index} of '{value}'.");

        }

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
    public async Task Interpreted_regex_remains_valid_after_slow_filter_setup()
    {
        _workspace.WriteFile("stateful.txt", "abc");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            @"\G(?=.)",
            WorkspaceSearchMode.Regex,
            caseSensitive: true,
            DefaultSettings() with
            {
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
    public async Task Search_applies_filters_without_a_total_file_cap()
    {

        _workspace.WriteFile("a-ignored.txt", "needle");

        _workspace.WriteFile("z-target.cs", "needle");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            DefaultSettings(),
            globs: ["**/*.cs"]);

        WorkspaceSearchToolResultItem match = Assert.Single(result.Matches);

        Assert.Equal("z-target.cs", match.Path);

        Assert.Null(result.Code);

        Assert.Equal(1, result.SkippedFilteredFileCount);

    }

    [Fact]
    public async Task Search_accepts_filters_beyond_the_former_per_kind_total()
    {

        _workspace.WriteFile("target.txt", "needle");

        List<string> globs = Enumerable
            .Range(0, 64)
            .Select(static index => $"missing-{index}.txt")
            .Append("target.txt")
            .ToList();

        List<string> extensions = Enumerable
            .Range(0, 64)
            .Select(static index => $".missing-{index}")
            .Append(".txt")
            .ToList();

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            globs: globs,
            extensions: extensions);

        WorkspaceSearchToolResultItem match = Assert.Single(result.Matches);

        Assert.Equal("target.txt", match.Path);

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
    public async Task Search_skips_binary_files_that_start_with_a_utf8_bom()
    {

        _workspace.WriteFile("text.txt", "needle");

        await File.WriteAllBytesAsync(
            Path.Combine(_workspace.Root, "binary.bin"),
            [0xEF, 0xBB, 0xBF, 0x6E, 0x65, 0x65, 0x64, 0x6C, 0x65, 0x00, 0xFF]);

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true);

        Assert.Single(result.Matches);
        Assert.Equal("text.txt", result.Matches[0].Path);
        Assert.Equal(1, result.SkippedBinaryFileCount);

    }

    [Fact]
    public async Task Search_skips_utf16_files_that_start_with_a_byte_order_mark()
    {

        _workspace.WriteFile("text.txt", "needle");

        await File.WriteAllBytesAsync(
            Path.Combine(_workspace.Root, "utf16.txt"),
            [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("needle")]);

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true);

        Assert.Single(result.Matches);
        Assert.Equal("text.txt", result.Matches[0].Path);
        Assert.Equal(1, result.SkippedBinaryFileCount);

    }

    [Fact]
    public async Task Search_reports_column_one_for_a_match_after_a_utf8_bom()
    {

        await File.WriteAllBytesAsync(
            Path.Combine(_workspace.Root, "bom.txt"),
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("needle\nneedle")]);

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "^needle",
            WorkspaceSearchMode.Regex,
            caseSensitive: true);

        Assert.Equal(
            [("bom.txt", 1, 1), ("bom.txt", 2, 1)],
            result.Matches.Select(static match => (match.Path, match.Line, match.Column)));

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
    public async Task Search_retains_the_pattern_allocation_boundary()
    {

        _workspace.WriteFile("matches.txt", "x x x x");

        WorkspaceSearchToolResultEnvelope patternLimited = await SearchAsync(
            "toolong",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            DefaultSettings() with { MaxPatternChars = 3 });

        Assert.Equal("invalid_pattern", patternLimited.Status);
        Assert.Equal("pattern_too_long", patternLimited.Code);

    }

    [Fact]
    public async Task Search_pages_every_match_beyond_the_former_total_cap()
    {

        const int expectedCount = 1_105;

        _workspace.WriteFile(
            "matches.txt",
            string.Join('\n', Enumerable.Repeat("needle", expectedCount)));

        List<WorkspaceSearchToolResultItem> allMatches = [];

        string? cursor = null;

        do
        {

            WorkspaceSearchToolResultEnvelope page = await SearchAsync(
                "needle",
                WorkspaceSearchMode.Literal,
                caseSensitive: true,
                cursor: cursor);

            allMatches.AddRange(page.Matches);

            if (page.NextCursor is not string nextCursor)
            {

                break;

            }

            Assert.True(page.Truncated);

            Assert.NotEmpty(nextCursor);

            Assert.DoesNotContain("matches.txt", nextCursor, StringComparison.Ordinal);

            Assert.Equal(
                "Call search_workspace again with cursor set to nextCursor and the same search arguments.",
                page.ContinuationAction);

            cursor = nextCursor;

        } while (true);

        Assert.Equal(expectedCount, allMatches.Count);

        Assert.Equal(
            Enumerable.Range(1, expectedCount),
            allMatches.Select(static match => match.Line));

    }

    [Fact]
    public async Task Search_continuation_anchors_to_the_last_match_when_an_earlier_file_is_added()
    {

        _workspace.WriteFile(
            "b.txt",
            string.Join(
                '\n',
                Enumerable.Range(1, 300).Select(
                    static line => $"needle {line:D3}")));

        WorkspaceSearchToolResultEnvelope first = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true);

        string cursor = Assert.IsType<string>(first.NextCursor);

        _workspace.WriteFile(
            "a.txt",
            string.Join('\n', Enumerable.Repeat("needle inserted", 10)));

        WorkspaceSearchToolResultEnvelope second = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            cursor: cursor);

        Assert.Equal("ok", second.Status);

        Assert.Equal(44, second.Matches.Length);

        Assert.All(
            second.Matches,
            static match => Assert.Equal("b.txt", match.Path));

        Assert.Equal(
            Enumerable.Range(257, 44),
            second.Matches.Select(static match => match.Line));

    }

    [Fact]
    public async Task Search_continuation_requests_restart_when_checkpoint_identity_vanished()
    {

        _workspace.WriteFile(
            "matches.txt",
            string.Join(
                '\n',
                Enumerable.Range(1, 300).Select(
                    static line => $"needle {line:D3}")));

        WorkspaceSearchToolResultEnvelope first = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true);

        string cursor = Assert.IsType<string>(first.NextCursor);

        _workspace.WriteFile(
            "matches.txt",
            string.Join(
                '\n',
                Enumerable.Range(1, 300)
                    .Where(static line => line != 256)
                    .Select(static line => $"needle {line:D3}")));

        WorkspaceSearchToolResultEnvelope second = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            cursor: cursor);

        Assert.Equal("invalid_cursor", second.Status);

        Assert.Equal("continuation_checkpoint_missing", second.Code);

        Assert.Empty(second.Matches);

        Assert.Null(second.NextCursor);

        Assert.Equal(
            "The workspace changed and the continuation checkpoint no longer exists. Restart with cursor omitted.",
            second.Message);

    }

    [Fact]
    public void Structured_result_trimming_continues_after_the_last_retained_match_identity()
    {

        WorkspaceSearchToolResultEnvelope source = new()
        {
            Matches =
            [
                new("a.txt", 1, 1, "one"),
                new("a.txt", 2, 1, "two"),
                new("a.txt", 3, 1, "three"),
            ],
            NextCursor = "third-checkpoint",
            MatchCursors =
            [
                "first-checkpoint",
                "second-checkpoint",
                "third-checkpoint",
            ],
            Truncated = true,
        };

        WorkspaceSearchToolResultEnvelope trimmed = source.RetainLeadingItems(2);

        Assert.Equal(2, trimmed.Matches.Length);

        Assert.Equal("second-checkpoint", trimmed.NextCursor);

        Assert.Equal(
            "Call search_workspace again with cursor set to nextCursor and the same search arguments.",
            trimmed.ContinuationAction);

    }

    [Fact]
    public async Task Search_streams_files_beyond_the_former_aggregate_byte_cap()
    {

        const int formerAggregateCap = 32 * 1024 * 1024;

        _workspace.WriteFile(
            "large.txt",
            string.Concat(
                Enumerable.Repeat(
                    new string('a', 4_095) + "\n",
                    (formerAggregateCap / 4_096) + 1))
            + "needle");

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true);

        Assert.Equal("ok", result.Status);
        Assert.Single(result.Matches);
        Assert.Equal("large.txt", result.Matches[0].Path);
        Assert.True(result.BytesSearched > formerAggregateCap);

    }

    [Theory]
    [InlineData((int)WorkspaceSearchMode.Literal)]
    [InlineData((int)WorkspaceSearchMode.Regex)]
    public async Task Search_spills_a_giant_logical_line_owner_only_and_cleans_it_up(
        int modeValue)
    {

        WorkspaceSearchMode mode = (WorkspaceSearchMode)modeValue;

        string prefix = new(
            'x',
            WorkspaceSearchLogicalLine.InMemoryCharacterLimit + 37);

        _workspace.WriteFile(
            "giant.txt",
            $"{prefix}needle");

        RecordingLineSpillObserver spillObserver = new();

        WorkspaceSearchEngine engine = new(
            DefaultSettings(),
            progressObserver: null,
            spillObserver);

        WorkspaceSearchToolResultEnvelope result = await engine.SearchAsync(
            _workspace.Root,
            new WorkspaceSearchRequest
            {
                Pattern = "needle",
                Mode = mode,
                CaseSensitive = true,
            },
            CancellationToken.None);

        WorkspaceSearchToolResultItem match = Assert.Single(result.Matches);

        Assert.Equal(prefix.Length + 1, match.Column);

        string spillPath = Assert.Single(spillObserver.Paths);

        Assert.True(spillObserver.OwnerOnlyAtCreation);

        Assert.False(File.Exists(spillPath));

    }

    [Fact]
    public async Task Regex_search_preserves_whole_line_semantics_after_bounded_spill()
    {

        string prefix = new(
            'x',
            WorkspaceSearchLogicalLine.InMemoryCharacterLimit + 19);

        _workspace.WriteFile(
            "giant-regex.txt",
            $"{prefix} magic42 magic42");

        RecordingLineSpillObserver spillObserver = new();

        WorkspaceSearchEngine engine = new(
            DefaultSettings(),
            progressObserver: null,
            spillObserver);

        WorkspaceSearchToolResultEnvelope result = await engine.SearchAsync(
            _workspace.Root,
            new WorkspaceSearchRequest
            {
                Pattern = @"(magic\d+)\s+\1$",
                Mode = WorkspaceSearchMode.Regex,
                CaseSensitive = true,
            },
            CancellationToken.None);

        WorkspaceSearchToolResultItem match = Assert.Single(result.Matches);

        Assert.Equal(prefix.Length + 2, match.Column);

        Assert.Equal("interpreted", result.RegexEngine);

        Assert.Single(spillObserver.Paths);

    }

    [Fact]
    public async Task Search_cancellation_removes_the_current_owner_only_line_spill()
    {

        string content = new(
            'x',
            WorkspaceSearchLogicalLine.InMemoryCharacterLimit * 2);

        _workspace.WriteFile("cancel-giant.txt", content);

        using CancellationTokenSource cancellation = new();

        CancellingLineSpillObserver spillObserver = new(cancellation);

        WorkspaceSearchEngine engine = new(
            DefaultSettings(),
            progressObserver: null,
            spillObserver);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => engine.SearchAsync(
                _workspace.Root,
                new WorkspaceSearchRequest
                {
                    Pattern = "absent",
                    Mode = WorkspaceSearchMode.Literal,
                    CaseSensitive = true,
                },
                cancellation.Token));

        string spillPath = Assert.IsType<string>(spillObserver.Path);

        Assert.False(File.Exists(spillPath));

    }

    [Fact]
    public void Line_context_hash_keeps_the_existing_utf8_identity_without_a_full_byte_array()
    {

        byte[] previous = Enumerable.Range(0, 32)
            .Select(static value => (byte)value)
            .ToArray();

        const string line = "alpha ✨ beta 𐐷";

        byte[] expectedInput = previous
            .Concat(Encoding.UTF8.GetBytes(line))
            .ToArray();

        byte[] expected = SHA256.HashData(expectedInput);

        byte[] actual = WorkspaceSearchContinuationCursor
            .CreateLineContextHash(previous, line.AsSpan());

        Assert.Equal(expected, actual);

    }

    [Fact]
    public async Task Giant_line_cursor_continues_after_the_exact_literal_match_identity()
    {

        string prefix = new(
            'x',
            WorkspaceSearchLogicalLine.InMemoryCharacterLimit + 11);

        _workspace.WriteFile(
            "giant-page.txt",
            prefix + string.Concat(Enumerable.Repeat("needle", 300)));

        WorkspaceSearchToolResultEnvelope first = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true);

        string cursor = Assert.IsType<string>(first.NextCursor);

        WorkspaceSearchToolResultEnvelope second = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true,
            cursor: cursor);

        Assert.Equal(256, first.Matches.Length);

        Assert.Equal(44, second.Matches.Length);

        Assert.Equal(
            prefix.Length + (256 * "needle".Length) + 1,
            second.Matches[0].Column);

    }

    [Fact]
    public async Task Search_visits_files_beyond_the_former_total_file_cap()
    {

        const int formerFileCap = 2_000;

        for (int index = 0; index <= formerFileCap; index++)
        {

            _workspace.WriteFile(
                $"many/file-{index:D4}.txt",
                index == formerFileCap ? "needle" : "haystack");

        }

        WorkspaceSearchToolResultEnvelope result = await SearchAsync(
            "needle",
            WorkspaceSearchMode.Literal,
            caseSensitive: true);

        WorkspaceSearchToolResultItem match = Assert.Single(result.Matches);

        Assert.Equal("many/file-2000.txt", match.Path);

        Assert.True(result.FilesVisited > formerFileCap);

    }

    [Fact]
    public async Task Search_is_not_stopped_by_a_product_owned_elapsed_clock()
    {

        _workspace.WriteFile("a.txt", "needle");

        WorkspaceSearchEngine engine = new(DefaultSettings());

        WorkspaceSearchToolResultEnvelope result = await engine.SearchAsync(
            _workspace.Root,
            new WorkspaceSearchRequest
            {
                Pattern = "needle",
                Mode = WorkspaceSearchMode.Literal,
                CaseSensitive = true,
            },
            CancellationToken.None);

        Assert.Equal("ok", result.Status);
        Assert.Single(result.Matches);

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

    [SkippableFact]
    public async Task Search_skips_directory_symlink_cycles_rejects_escapes_and_deduplicates_file_identity()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

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

    [SkippableFact]
    public async Task Search_rejects_fifo_before_opening_it()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

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

    [SkippableFact]
    public async Task Search_rejects_link_count_change_after_open_and_read()
    {

        Skip.If(
            !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(),
            "This asserts POSIX behaviour and runs on macOS and Linux only.");

        string target = _workspace.WriteFile("link-race.txt", "needle");

        string alias = Path.Combine(_workspace.Root, "link-race-alias.txt");

        HardLinkingSearchObserver observer = new(target, alias);

        WorkspaceSearchToolResultEnvelope result = await new WorkspaceSearchEngine(
            DefaultSettings(),
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
        string? cursor = null,
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
                Cursor = cursor,
            },
            cancellationToken);

    }

    private static WorkspaceSearchSettings DefaultSettings() =>
        new()
        {
            MaxPatternChars = 4_096,
            RegexTimeoutMilliseconds = 250,
            MaxPreviewChars = 512,
        };

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

    private sealed class RecordingLineSpillObserver : IWorkspaceSearchLineSpillObserver
    {

        public List<string> Paths { get; } = [];

        public bool OwnerOnlyAtCreation { get; private set; } = true;

        public void OnCreated(string path)
        {

            Paths.Add(path);

            if (!OperatingSystem.IsWindows())
            {

                UnixFileMode mode = File.GetUnixFileMode(path);

                OwnerOnlyAtCreation &= mode
                    == (UnixFileMode.UserRead | UnixFileMode.UserWrite);

            }

        }

    }

    private sealed class CancellingLineSpillObserver(
        CancellationTokenSource cancellation) : IWorkspaceSearchLineSpillObserver
    {

        public string? Path { get; private set; }

        public void OnCreated(string path)
        {

            Path = path;

            cancellation.Cancel();

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
