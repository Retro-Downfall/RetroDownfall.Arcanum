using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Core.Tower;

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
    public void ToolError_then_ToolResult_keeps_failed()
    {
        IncantationStore store = new();
        _ = store.UpsertCall("c3", "execute_command", """{"command":"dotnet --version"}""");
        _ = store.UpsertError("c3", "execute_command", "[Tool error: execute_command failed with an internal error.]");
        _ = store.UpsertResult("c3", "execute_command", "[Tool error: execute_command failed with an internal error.]");

        IncantationRecord record = store.Snapshot()[0];
        Assert.Equal(IncantationState.Failed, record.State);
        Assert.Contains("internal error", record.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ward_note_attaches_to_pending_tool()
    {
        IncantationStore store = new();
        _ = store.UpsertCall("c4", "write_file", """{"path":"/tmp/a"}""");
        _ = store.AppendWardNote("write_file", "Ward pending (abc)", "abc");
        _ = store.AppendWardNote("write_file", "Always allowing write_file for this Command Center session", "abc");

        IncantationRecord record = store.Snapshot()[0];
        Assert.Equal(2, record.WardNotes.Count);
        Assert.Contains("Always allowing", record.WardNotes[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Streamed_payloads_are_clamped_so_the_store_cannot_retain_megabytes()
    {
        // The store keeps up to MaxEntries records for the life of the session, and a tool response
        // may approach the host's ToolOutputCapBytes (~1 MiB), so every retained payload is bounded.
        string huge = new('x', 1_000_000);
        IncantationStore store = new();
        _ = store.UpsertCall("c5", "execute_command", huge);
        _ = store.UpsertResult("c5", "execute_command", huge);
        _ = store.UpsertError("c6", "read_file", huge);

        IncantationRecord succeeded = store.Snapshot()[0];
        IncantationRecord failed = store.Snapshot()[1];
        Assert.True(succeeded.ArgumentsJson!.Length <= IncantationRecord.MaxPayloadChars);
        Assert.True(succeeded.ResultText!.Length <= IncantationRecord.MaxPayloadChars);
        Assert.True(failed.ErrorText!.Length <= IncantationRecord.MaxPayloadChars);
    }

    [Fact]
    public void Resumed_history_payloads_are_clamped_too()
    {
        string huge = new('y', 800_000);
        IncantationStore store = new();
        _ = store.AddFromHistory("c7", "read_file", huge, huge, isError: false, unparseable: false);

        IncantationRecord record = store.Snapshot()[0];
        Assert.True(record.ArgumentsJson!.Length <= IncantationRecord.MaxPayloadChars);
        Assert.True(record.ResultText!.Length <= IncantationRecord.MaxPayloadChars);
    }

    [Fact]
    public void Clamping_oversized_json_arguments_keeps_the_safe_summary_keys()
    {
        // Dropping the bytes must not cost the operator the one thing the pane shows for a heavy
        // tool: the safe summary built from the argument object's non-sensitive scalar keys.
        string body = new('z', 900_000);
        IncantationStore store = new();
        _ = store.UpsertCall("c8", "write_file", $$"""{"path":"/tmp/a.cs","content":"{{body}}"}""");

        IncantationRecord record = store.Snapshot()[0];
        Assert.True(record.ArgumentsJson!.Length <= IncantationRecord.MaxPayloadChars);
        Assert.DoesNotContain("zzzz", record.ArgumentsJson, StringComparison.Ordinal);

        string joined = string.Join('\n', IncantationFormatter.FormatBlock(record, 60));
        Assert.Contains("path=", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zzzz", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_refreshes_of_an_unchanged_store_do_not_reformat_every_record()
    {
        // Every composer keystroke re-runs the layout pass, which re-copies the Incantations lines.
        // Without memoization that re-parses each retained payload as JSON on the UI thread.
        ObservableCollection<string> lines = new();
        List<string?> anchors = new();
        Fill(new IncantationStore(), 20).CopyDisplayLinesTo(lines, anchors, 60);

        IncantationStore store = Fill(new IncantationStore(), 300);
        lines = new ObservableCollection<string>();
        anchors = new List<string?>();

        Stopwatch cold = Stopwatch.StartNew();
        store.CopyDisplayLinesTo(lines, anchors, 60);
        cold.Stop();

        Stopwatch warm = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            store.CopyDisplayLinesTo(lines, anchors, 60);
        }

        warm.Stop();

        Assert.True(
            warm.Elapsed < cold.Elapsed * 5,
            $"20 unchanged refreshes took {warm.ElapsedMilliseconds}ms after a first pass of {cold.ElapsedMilliseconds}ms.");
    }

    /// <summary>
    /// The chat runner mutates these records on a thread-pool thread for the whole of a streamed turn
    /// while the Terminal.Gui main loop copies the pane on every layout pass, so formatting outside the
    /// store gate reads a record mid-mutation: the result text is re-read after its own null check and
    /// the ward-note list is indexed after its own count. The sibling <c>SessionLogBuffer.CopyLinesTo</c>
    /// builds its lines inside the gate for exactly this reason.
    /// </summary>
    [Fact]
    public async Task Copying_display_lines_while_the_chat_thread_mutates_a_record_does_not_throw()
    {
        IncantationStore store = new();
        _ = store.UpsertCall("c-race", "execute_command", """{"command":"dotnet --version"}""");

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        Exception? readerFailure = null;
        Exception? writerFailure = null;

        Task writer = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 100_000 && !stop.IsCancellationRequested; i++)
                {
                    _ = store.UpsertResult("c-race", "execute_command", "ok");
                    _ = store.UpsertResult("c-race", "execute_command", null);
                    _ = store.AppendWardNote("execute_command", "Ward pending (" + i + ")", "w" + i);
                }
            }
            catch (Exception ex)
            {
                writerFailure = ex;
            }
        });

        Task reader = Task.Run(() =>
        {
            ObservableCollection<string> lines = new();
            List<string?> anchors = new();
            try
            {
                while (!writer.IsCompleted)
                {
                    store.CopyDisplayLinesTo(lines, anchors, 60);
                }
            }
            catch (Exception ex)
            {
                readerFailure = ex;
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Null(writerFailure);
        Assert.Null(readerFailure);
    }

    [Fact]
    public void Display_lines_are_memoized_until_the_record_changes()
    {
        IncantationRecord record = new("c10", "read_file");
        record.ApplyCall("read_file", """{"path":"/tmp/a"}""");

        IReadOnlyList<string> first = record.DisplayLines(60);
        Assert.Same(first, record.DisplayLines(60));
        Assert.NotSame(first, record.DisplayLines(40));

        record.ApplyResult("ok");
        Assert.NotSame(first, record.DisplayLines(60));
    }

    private static IncantationStore Fill(IncantationStore store, int records)
    {
        string filler = new('a', 1_500);
        for (int i = 0; i < records; i++)
        {
            string id = "call-" + i.ToString(CultureInfo.InvariantCulture);
            _ = store.UpsertCall(id, "read_file", $$"""{"path":"/tmp/{{i}}","note":"{{filler}}"}""");
            _ = store.UpsertResult(id, "read_file", $$"""{"bytes":{{i}},"body":"{{filler}}"}""");
        }

        return store;
    }

    [Fact]
    public void CompleteLatestPending_pairs_tool_result_without_call_id()
    {
        IncantationStore store = new();
        _ = store.UpsertCall(null, "execute_command", """{"command":"ls"}""");
        _ = store.CompleteLatestPending("execute_command", "ok", isError: false);

        Assert.Single(store.Snapshot());
        Assert.Equal(IncantationState.Succeeded, store.Snapshot()[0].State);
        Assert.Equal("ok", store.Snapshot()[0].ResultText);
    }
}

public sealed class PersistedToolInteractionTests
{
    [Fact]
    public void Parses_grimoire_paren_tool_call_and_result()
    {
        Assert.True(
            PersistedToolInteraction.TryParseToolCall(
                """[ToolCall: write_file({"path":"/a"})]""",
                out string name,
                out string? args));
        Assert.Equal("write_file", name);
        Assert.Equal("""{"path":"/a"}""", args);

        Assert.True(PersistedToolInteraction.TryParseToolResult("[ToolResult: done]", out string result));
        Assert.Equal("done", result);
    }

    [Fact]
    public void Detects_assistant_tool_call_and_system_tool_result_entries()
    {
        EntryDto call = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "assistant",
            "[ToolCall: ls({})]",
            null,
            "ls",
            DateTimeOffset.UtcNow);
        EntryDto result = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "system",
            "[ToolResult: ok]",
            null,
            null,
            DateTimeOffset.UtcNow);

        Assert.True(PersistedToolInteraction.IsToolInteraction(call));
        Assert.True(PersistedToolInteraction.IsToolInteraction(result));
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
    public void Api_key_in_result_json_reaches_formatter_and_is_redacted()
    {
        // Proof: without treating api_key as sensitive, succeeded non-heavy tools append raw
        // ResultText (see FormatBlock success branch). Credential-shaped JSON therefore reaches
        // the Incantations sink — expand SensitiveKeyNames so HasSensitiveOrContentBearingArgs
        // flips the block to heavy and suppresses the secret.
        const string secret = "sk-live-leak-proof-value";
        IncantationRecord record = new("id", "fetch_credentials");
        record.ApplyCall("fetch_credentials", """{"path":"/tmp/cfg"}""");
        record.ApplyResult($$"""{"api_key":"{{secret}}"}""");

        Assert.True(IncantationFormatter.IsSensitiveKey("api_key"));
        Assert.True(IncantationFormatter.IsSensitiveKey("password"));
        Assert.True(IncantationFormatter.IsSensitiveKey("authorization"));
        Assert.True(IncantationFormatter.IsSensitiveKey("token"));
        Assert.True(IncantationFormatter.IsSensitiveKey("secret"));

        IReadOnlyList<string> lines = IncantationFormatter.FormatBlock(record, 120);
        string joined = string.Join('\n', lines);
        Assert.DoesNotContain(secret, joined, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void Password_argument_is_treated_as_sensitive()
    {
        const string secret = "hunter2-proof";
        IncantationRecord record = new("id", "login_helper");
        record.ApplyCall("login_helper", $$"""{"url":"https://example.test","password":"{{secret}}"}""");

        IReadOnlyList<string> lines = IncantationFormatter.FormatBlock(record, 100);
        string joined = string.Join('\n', lines);
        Assert.DoesNotContain(secret, joined, StringComparison.Ordinal);
        Assert.Contains("url=", joined, StringComparison.OrdinalIgnoreCase);
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
    public void Sanitize_expands_tabs_to_the_next_tab_stop()
    {
        string sanitized = IncantationFormatter.Sanitize("ab\tc\td");

        // "ab" → pad 6 to column 8, "c" → pad 7 to column 16.
        Assert.Equal("ab" + new string(' ', 6) + "c" + new string(' ', 7) + "d", sanitized);
        Assert.Equal(17, ComposerLayout.MeasureCellWidth(sanitized));
    }

    [Fact]
    public void Sanitize_of_a_tab_indented_payload_is_not_quadratic()
    {
        // Re-measuring the whole accumulated buffer per tab costs one full copy plus one full scan
        // for every '\t', so a tab-indented stack trace degrades to O(tabs × length) on the UI thread.
        string payload = string.Concat(Enumerable.Repeat("\tat Namespace.Type.Method(arg)", 8_000));

        Stopwatch sw = Stopwatch.StartNew();
        string sanitized = IncantationFormatter.Sanitize(payload);
        sw.Stop();

        Assert.DoesNotContain('\t', sanitized);
        Assert.True(
            sw.ElapsedMilliseconds < 500,
            $"Sanitize of {payload.Length} chars with 8000 tabs took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public void Multi_line_error_is_suppressed_exactly_like_a_multi_line_result()
    {
        // Sanitize flattens newlines to spaces, so testing LooksLikeHugeBlob on the *sanitized* error
        // can never see the newline count the success branch fails closed on.
        const string blob = "boom\nat one\nat two\nat three\nat four\nat five";

        IncantationRecord failed = new("id", "run_tests");
        failed.ApplyCall("run_tests", """{"path":"/tmp/t"}""");
        failed.ApplyError(blob);

        IncantationRecord succeeded = new("id2", "run_tests");
        succeeded.ApplyCall("run_tests", """{"path":"/tmp/t"}""");
        succeeded.ApplyResult(blob);

        string failedText = string.Join('\n', IncantationFormatter.FormatBlock(failed, 200));
        string succeededText = string.Join('\n', IncantationFormatter.FormatBlock(succeeded, 200));

        Assert.DoesNotContain("at three", succeededText, StringComparison.Ordinal);
        Assert.DoesNotContain("at three", failedText, StringComparison.Ordinal);
        Assert.Contains("error", failedText, StringComparison.Ordinal);
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
        Assert.Equal(
            expected,
            CommandCenterFocusCycle.Next(current, forward, sidebar, modelSelectorVisible: false));
    }

    /// <summary>
    /// The model drop-down is a real focus region, so Tab has to reach it — otherwise it is a mouse
    /// affordance in a keyboard-only surface.
    /// </summary>
    [Theory]
    [InlineData(true, CommandCenterFocusRegion.Incantations, true, CommandCenterFocusRegion.Model)]
    [InlineData(true, CommandCenterFocusRegion.Model, true, CommandCenterFocusRegion.Composer)]
    [InlineData(true, CommandCenterFocusRegion.Composer, false, CommandCenterFocusRegion.Model)]
    [InlineData(false, CommandCenterFocusRegion.Incantations, true, CommandCenterFocusRegion.Model)]
    internal void Next_includes_the_model_selector_when_it_is_on_screen(
        bool sidebar,
        CommandCenterFocusRegion current,
        bool forward,
        CommandCenterFocusRegion expected)
    {
        Assert.Equal(
            expected,
            CommandCenterFocusCycle.Next(current, forward, sidebar, modelSelectorVisible: true));
    }

    /// <summary>
    /// Below the width threshold the drop-down is not rendered, and Tab must not strand focus on a
    /// control the operator cannot see.
    /// </summary>
    [Fact]
    internal void Next_skips_the_model_selector_when_the_viewport_is_too_narrow()
    {
        Assert.Equal(
            CommandCenterFocusRegion.Composer,
            CommandCenterFocusCycle.Next(
                CommandCenterFocusRegion.Incantations,
                forward: true,
                sidebarVisible: false,
                modelSelectorVisible: false));
    }

    /// <summary>A region that just went off screen restarts the cycle rather than trapping focus.</summary>
    [Fact]
    internal void A_hidden_model_region_falls_back_to_the_start_of_the_cycle()
    {
        Assert.Equal(
            CommandCenterFocusRegion.Composer,
            CommandCenterFocusCycle.Next(
                CommandCenterFocusRegion.Model,
                forward: true,
                sidebarVisible: true,
                modelSelectorVisible: false));
    }

    [Fact]
    public void Overlay_current_starts_cycle()
    {
        Assert.Equal(
            CommandCenterFocusRegion.Composer,
            CommandCenterFocusCycle.Next(
                CommandCenterFocusRegion.Overlay,
                forward: true,
                sidebarVisible: true,
                modelSelectorVisible: false));
        Assert.Equal(
            CommandCenterFocusRegion.Incantations,
            CommandCenterFocusCycle.Next(
                CommandCenterFocusRegion.Overlay,
                forward: false,
                sidebarVisible: false,
                modelSelectorVisible: false));
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
