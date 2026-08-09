using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Core.TheForge;

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
