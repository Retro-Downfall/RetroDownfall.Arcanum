using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class IncantationStoreTests
{
    [Fact]
    public void ToolCall_creates_pending_by_CallId_Result_updates_same()
    {
        IncantationStore store = new();
        _ = store.UpsertCall("c1", "execute_command", """{"command":"dotnet --version"}""");
        _ = store.UpsertResult("c1", "execute_command", "10.0.0");

        IReadOnlyList<IncantationRecord> snap = store.Snapshot();
        Assert.Single(snap);
        Assert.Equal(IncantationState.Succeeded, snap[0].State);
        Assert.Equal("10.0.0", snap[0].ResultText);
    }

    [Fact]
    public void ToolError_marks_failed_and_stays_in_store()
    {
        IncantationStore store = new();
        _ = store.UpsertCall("c2", "list_directory", """{"path":"."}""");
        _ = store.UpsertError("c2", "list_directory", "permission denied");

        Assert.Equal(IncantationState.Failed, store.Snapshot()[0].State);
        Assert.Equal("permission denied", store.Snapshot()[0].ErrorText);
    }
}

public sealed class IncantationFormatterTests
{
    [Fact]
    public void Heavy_write_file_omits_content_blob()
    {
        IncantationRecord record = new("id", "write_file");
        record.ApplyCall("write_file", """{"path":"/tmp/a.cs","content":"huge body here"}""");
        record.ApplyResult("ok");

        IReadOnlyList<string> lines = IncantationFormatter.FormatBlock(record, 60);
        string joined = string.Join('\n', lines);
        Assert.DoesNotContain("huge body", joined, StringComparison.Ordinal);
        Assert.Contains("path=", joined, StringComparison.OrdinalIgnoreCase);
        Assert.True(lines.Count <= 3);
    }

    [Fact]
    public void Sensitive_key_fail_closed_even_for_unknown_tool()
    {
        IncantationRecord record = new("id", "custom_tool");
        record.ApplyCall("custom_tool", """{"path":"x","oldString":"secret","newString":"other"}""");

        IReadOnlyList<string> lines = IncantationFormatter.FormatBlock(record, 80);
        string joined = string.Join('\n', lines);
        Assert.DoesNotContain("secret", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("other", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_json_is_name_and_state_only()
    {
        IncantationRecord record = new("id", "foo");
        record.ApplyCall("foo", "not-json{{{");
        IReadOnlyList<string> lines = IncantationFormatter.FormatBlock(record, 40);
        string joined = string.Join('\n', lines);
        Assert.Contains("foo", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("not-json", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Cap_excludes_separator_and_ellipsis_fits_line_three()
    {
        IncantationRecord record = new("id", "list_directory");
        record.ApplyCall(
            "list_directory",
            """{"path":"/very/long/path/that/keeps/going/and/going/and/going/forever/with/more/segments"}""");
        record.ApplyResult(new string('x', 500));

        IReadOnlyList<string> lines = IncantationFormatter.FormatBlock(record, 20);
        Assert.True(lines.Count <= 3);
        if (lines.Count == 3)
        {
            Assert.True(ComposerLayout.MeasureCellWidth(lines[2]) <= 20);
        }
    }

    [Fact]
    public void Separator_is_outside_block()
    {
        string sep = IncantationFormatter.SeparatorLine(10);
        Assert.Equal(10, sep.Length);
        Assert.All(sep, c => Assert.Equal('─', c));
    }

    [Fact]
    public void Unparseable_override_never_shows_raw()
    {
        IncantationRecord record = new("id", "unknown");
        record.MarkUnparseable();
        IReadOnlyList<string> lines = IncantationFormatter.FormatBlock(record, 40);
        Assert.Contains("unavailable", string.Join(' ', lines), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CommandCenterFocusCycleTests
{
    [Theory]
    [InlineData(true, CommandCenterFocusRegion.Composer, true, CommandCenterFocusRegion.Sessions)]
    [InlineData(true, CommandCenterFocusRegion.Sessions, true, CommandCenterFocusRegion.Transcript)]
    [InlineData(true, CommandCenterFocusRegion.Transcript, true, CommandCenterFocusRegion.Incantations)]
    [InlineData(true, CommandCenterFocusRegion.Incantations, true, CommandCenterFocusRegion.Composer)]
    [InlineData(false, CommandCenterFocusRegion.Composer, true, CommandCenterFocusRegion.Transcript)]
    [InlineData(false, CommandCenterFocusRegion.Transcript, true, CommandCenterFocusRegion.Incantations)]
    [InlineData(false, CommandCenterFocusRegion.Incantations, true, CommandCenterFocusRegion.Composer)]
    internal void Next_cycles_expected(
        bool sidebar,
        CommandCenterFocusRegion current,
        bool forward,
        CommandCenterFocusRegion expected)
    {
        Assert.Equal(expected, CommandCenterFocusCycle.Next(current, forward, sidebar));
    }

    [Fact]
    public void Overlay_current_starts_cycle()
    {
        Assert.Equal(
            CommandCenterFocusRegion.Composer,
            CommandCenterFocusCycle.Next(CommandCenterFocusRegion.Overlay, forward: true, sidebarVisible: true));
        Assert.Equal(
            CommandCenterFocusRegion.Incantations,
            CommandCenterFocusCycle.Next(CommandCenterFocusRegion.Overlay, forward: false, sidebarVisible: false));
    }
}

public sealed class ThinkingSpinnerTests
{
    [Fact]
    public void Frame_wraps()
    {
        Assert.Equal(ThinkingSpinner.Frames[0], ThinkingSpinner.Frame(0));
        Assert.Equal(ThinkingSpinner.Frames[0], ThinkingSpinner.Frame(ThinkingSpinner.Frames.Length));
        Assert.StartsWith("Thinking ", ThinkingSpinner.Format(3), StringComparison.Ordinal);
    }
}

public sealed class SessionLogBufferTranscriptSplitTests
{
    [Fact]
    public void CopyLinesTo_excludes_Tool_entries()
    {
        SessionLogBuffer log = new();
        _ = log.Append(SessionLogEntryKind.User, "hi");
        _ = log.Append(SessionLogEntryKind.Tool, "should not appear");
        _ = log.Append(SessionLogEntryKind.Assistant, "yo");

        System.Collections.ObjectModel.ObservableCollection<string> lines = new();
        log.CopyLinesTo(lines, wrapWidth: 80);
        Assert.DoesNotContain(lines, static l => l.Contains("should not appear", StringComparison.Ordinal));
        Assert.Contains(lines, static l => l.Contains("hi", StringComparison.Ordinal));
    }
}

public sealed class ComposerLayoutMinBodyTests
{
    [Fact]
    public void MinBodyHeight_is_six()
    {
        Assert.Equal(6, ComposerLayout.MinBodyHeight);
        Assert.Equal(3, ComposerLayout.MinTranscriptHeight);
        Assert.Equal(3, ComposerLayout.MinIncantationsHeight);
    }

    [Fact]
    public void EffectiveMax_shrinks_for_taller_min_body_at_floor()
    {
        // 80x12 floor: header~3, footer 1, min body 6, border 2 → little room for composer
        int max = ComposerLayout.EffectiveMaxContentRows(12, headerHeight: 3, footerHeight: 1);
        Assert.Equal(ComposerLayout.MinContentRows, max);
    }
}
