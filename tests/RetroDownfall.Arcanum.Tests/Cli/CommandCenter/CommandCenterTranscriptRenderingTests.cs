using System.Collections.ObjectModel;
using System.Collections.Specialized;

using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// Guards the Command Center transcript refresh contract: a streaming flush must cost work
/// proportional to the appended text, not to the whole transcript, and every wrap/truncate boundary
/// must be measured in terminal cells rather than UTF-16 code units.
/// </summary>
public sealed class CommandCenterTranscriptRenderingTests
{
    private static SessionLogBuffer BuildTranscript(int exchanges, out SessionLogEntry streaming)
    {
        SessionLogBuffer log = new();
        for (int i = 0; i < exchanges; i++)
        {
            log.Append(SessionLogEntryKind.User, $"question {i} " + new string('q', 120));
            log.Append(SessionLogEntryKind.Assistant, $"answer {i} " + new string('a', 400));
        }

        streaming = log.Append(SessionLogEntryKind.Assistant, "streaming answer", streaming: true);

        return log;
    }

    private static int CountChanges(ObservableCollection<string> lines, Action mutate)
    {
        int changes = 0;
        void Handler(object? sender, NotifyCollectionChangedEventArgs args) => changes++;

        lines.CollectionChanged += Handler;
        try
        {
            mutate();
        }
        finally
        {
            lines.CollectionChanged -= Handler;
        }

        return changes;
    }

    [Fact]
    public void Streaming_flush_touches_only_the_tail_lines()
    {
        SessionLogBuffer log = BuildTranscript(exchanges: 30, out SessionLogEntry streaming);
        ObservableCollection<string> lines = [];
        List<Guid?> anchors = [];
        log.CopyLinesTo(lines, anchors, wrapWidth: 76);

        int baseline = lines.Count;
        Assert.True(baseline > 200, $"expected a long transcript, saw {baseline} lines");

        int changes = CountChanges(
            lines,
            () =>
            {
                log.UpdateStreaming(streaming, "streaming answer plus one more token");
                log.CopyLinesTo(lines, anchors, wrapWidth: 76);
            });

        // The old Clear()-then-re-Add rebuild raised 1 + line-count notifications on the bound
        // ListView; only the streaming entry's tail may move now.
        Assert.True(changes <= 4, $"expected a tail edit, saw {changes} collection changes");
        Assert.Equal(lines.Count, anchors.Count);
        Assert.Contains("plus one more token", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_with_no_transcript_change_raises_no_collection_changes()
    {
        SessionLogBuffer log = BuildTranscript(exchanges: 8, out _);
        ObservableCollection<string> lines = [];
        log.CopyLinesTo(lines, lineAnchors: null, wrapWidth: 76);

        int changes = CountChanges(lines, () => log.CopyLinesTo(lines, lineAnchors: null, wrapWidth: 76));

        Assert.Equal(0, changes);
    }

    [Fact]
    public void Unchanged_entries_reuse_cached_wrapped_lines()
    {
        SessionLogBuffer log = BuildTranscript(exchanges: 4, out SessionLogEntry streaming);
        ObservableCollection<string> first = [];
        log.CopyLinesTo(first, lineAnchors: null, wrapWidth: 40);

        log.UpdateStreaming(streaming, "streaming answer grows");
        ObservableCollection<string> second = [];
        log.CopyLinesTo(second, lineAnchors: null, wrapWidth: 40);

        // Wrapping is memoized per entry, so a finalized entry hands back the same string instances.
        Assert.Same(first[0], second[0]);
        Assert.Same(first[1], second[1]);
    }

    [Fact]
    public void Changing_wrap_width_invalidates_the_cache()
    {
        SessionLogBuffer log = new();
        log.Append(SessionLogEntryKind.Assistant, "one two three four five six seven eight");

        ObservableCollection<string> narrow = [];
        log.CopyLinesTo(narrow, lineAnchors: null, wrapWidth: 14);
        ObservableCollection<string> wide = [];
        log.CopyLinesTo(wide, lineAnchors: null, wrapWidth: 80);

        Assert.True(narrow.Count > wide.Count);
        Assert.All(narrow, line => Assert.True(line.Length <= 14, line));
        Assert.Single(wide);
    }

    [Fact]
    public void Incremental_output_matches_a_from_scratch_copy()
    {
        SessionLogBuffer log = BuildTranscript(exchanges: 5, out SessionLogEntry streaming);
        ObservableCollection<string> incremental = [];
        List<Guid?> incrementalAnchors = [];
        log.CopyLinesTo(incremental, incrementalAnchors, wrapWidth: 52);

        log.UpdateStreaming(streaming, "streaming answer\n\n\nwith blank runs and a tail");
        log.Append(SessionLogEntryKind.Status, "status line");
        log.CopyLinesTo(incremental, incrementalAnchors, wrapWidth: 52);

        ObservableCollection<string> fresh = [];
        List<Guid?> freshAnchors = [];
        log.CopyLinesTo(fresh, freshAnchors, wrapWidth: 52);

        Assert.Equal(fresh, incremental);
        Assert.Equal(freshAnchors, incrementalAnchors);
    }

    [Fact]
    public void Trimming_and_clearing_do_not_leak_wrap_cache_entries()
    {
        SessionLogBuffer log = new(maxEntries: 4);
        for (int i = 0; i < 40; i++)
        {
            log.Append(SessionLogEntryKind.Assistant, $"entry {i}");
            ObservableCollection<string> lines = [];
            log.CopyLinesTo(lines, lineAnchors: null, wrapWidth: 40);
        }

        Assert.Equal(4, log.Count);

        log.Clear();
        log.Append(SessionLogEntryKind.Assistant, "fresh");
        ObservableCollection<string> after = [];
        log.CopyLinesTo(after, lineAnchors: null, wrapWidth: 40);

        Assert.Single(after);
        Assert.Equal("Mage: fresh", after[0]);
    }

    [Fact]
    public void WrapLine_measures_display_cells_for_cjk()
    {
        // Eight fullwidth ideographs = 16 cells; a width-8 budget must yield two rows of four.
        string cjk = "你好世界你好世界";
        string[] wrapped = SessionLogBuffer.WrapLine(cjk, 8).ToArray();

        Assert.Equal(2, wrapped.Length);
        Assert.All(wrapped, row => Assert.Equal(8, TerminalCellMetrics.MeasureWidth(row)));
    }

    [Fact]
    public void WrapLine_never_splits_a_surrogate_pair()
    {
        string emoji = string.Concat(Enumerable.Repeat("🜁", 12));
        string[] wrapped = SessionLogBuffer.WrapLine(emoji, 6).ToArray();

        Assert.All(
            wrapped,
            row =>
            {
                Assert.False(char.IsLowSurrogate(row[0]), "row began with an orphaned low surrogate");
                Assert.False(char.IsHighSurrogate(row[^1]), "row ended with an orphaned high surrogate");
            });
        Assert.Equal(emoji, string.Concat(wrapped));
    }

    [Fact]
    public void CopyLinesTo_keeps_cjk_entries_inside_the_pane_width()
    {
        SessionLogBuffer log = new();
        log.Append(SessionLogEntryKind.Assistant, string.Concat(Enumerable.Repeat("宽", 60)));

        ObservableCollection<string> lines = [];
        log.CopyLinesTo(lines, lineAnchors: null, wrapWidth: 20);

        Assert.All(
            lines,
            line => Assert.True(
                TerminalCellMetrics.MeasureWidth(line) <= 20,
                $"line overflowed the pane: {TerminalCellMetrics.MeasureWidth(line)} cells"));
    }

    [Fact]
    public void OverlayLayout_WrapLines_measures_cells_and_keeps_graphemes_whole()
    {
        string cjk = string.Concat(Enumerable.Repeat("字", 40));
        List<string> wrapped = OverlayLayout.WrapLines([cjk], innerWidth: 20);

        Assert.All(
            wrapped,
            row => Assert.True(
                TerminalCellMetrics.MeasureWidth(row) <= 20,
                $"overlay row overflowed: {TerminalCellMetrics.MeasureWidth(row)} cells"));
        Assert.All(wrapped, row => Assert.False(row.Contains('�'), "wrap produced a replacement glyph"));
    }

    [Fact]
    public void TruncateToCells_clips_by_display_width_not_code_units()
    {
        // Header truncation: ten ideographs are 20 cells, so a 10-cell budget keeps five.
        string cjk = string.Concat(Enumerable.Repeat("章", 10));
        string clipped = TerminalCellMetrics.TruncateToCells(cjk, 10);

        Assert.Equal(5, clipped.Length);
        Assert.Equal(10, TerminalCellMetrics.MeasureWidth(clipped));

        // Emoji: a code-unit budget would leave a lone high surrogate.
        string emoji = string.Concat(Enumerable.Repeat("🜁", 6));
        string emojiClipped = TerminalCellMetrics.TruncateToCells(emoji, 5);

        Assert.Equal(4, emojiClipped.Length);
        Assert.False(char.IsHighSurrogate(emojiClipped[^1]));
    }

    [Fact]
    public void MeasureWidth_counts_cells_for_wide_and_combining_text()
    {
        Assert.Equal(8, TerminalCellMetrics.MeasureWidth("你好世界"));
        Assert.Equal(4, TerminalCellMetrics.MeasureWidth("你好世界".Substring(0, 2)));
        // A combining acute occupies no cell of its own.
        Assert.Equal(1, TerminalCellMetrics.MeasureWidth("é"));
        Assert.Equal(5, TerminalCellMetrics.MeasureWidth("plain"));
    }

    [Fact]
    public void Pump_collapses_a_burst_of_identical_refreshes_into_one_apply()
    {
        List<CommandCenterUiUpdateKind> applied = CommandCenterUiUpdatePump.Coalesce(
            [
                CommandCenterUiUpdateKind.RefreshLog,
                CommandCenterUiUpdateKind.RefreshLog,
                CommandCenterUiUpdateKind.RefreshLog,
                CommandCenterUiUpdateKind.RefreshLog,
            ]);

        Assert.Equal([CommandCenterUiUpdateKind.RefreshLog], applied);
    }

    [Fact]
    public void Pump_widens_mixed_refreshes_to_RefreshAll_and_preserves_focus_order()
    {
        List<CommandCenterUiUpdateKind> applied = CommandCenterUiUpdatePump.Coalesce(
            [
                CommandCenterUiUpdateKind.RefreshLog,
                CommandCenterUiUpdateKind.RefreshIncantations,
                CommandCenterUiUpdateKind.FocusInput,
                CommandCenterUiUpdateKind.RefreshLog,
                CommandCenterUiUpdateKind.RefreshLog,
            ]);

        Assert.Equal(
            [
                CommandCenterUiUpdateKind.RefreshAll,
                CommandCenterUiUpdateKind.FocusInput,
                CommandCenterUiUpdateKind.RefreshLog,
            ],
            applied);
    }

    [Fact]
    public void Pump_never_drops_a_focus_update()
    {
        List<CommandCenterUiUpdateKind> applied = CommandCenterUiUpdatePump.Coalesce(
            [
                CommandCenterUiUpdateKind.FocusSessions,
                CommandCenterUiUpdateKind.FocusTranscript,
                CommandCenterUiUpdateKind.FocusInput,
            ]);

        Assert.Equal(3, applied.Count);
    }

    [Fact]
    public void ApplyTailEdit_replaces_only_the_differing_suffix()
    {
        ObservableCollection<string> target = ["a", "b", "c", "d"];

        int mutations = CommandCenterLineSync.ApplyTailEdit(target, ["a", "b", "z"]);

        Assert.Equal(["a", "b", "z"], target);
        Assert.Equal(3, mutations); // remove d, remove c, add z
    }
}
