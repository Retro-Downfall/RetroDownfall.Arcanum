using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class ArcanumToolNameCatalogTests
{
    [Theory]
    [InlineData("cast_sending", "create_agent_task")]
    [InlineData("dispatch_sending", "create_agent_task")]
    [InlineData("continue_sending", "send_agent_message")]
    [InlineData("read_file_chunk", "read_file")]
    [InlineData("replace_text_block", "edit_file")]
    [InlineData("scribe_lexicon", "save_memory")]
    [InlineData("delete_lexicon", "delete_memory")]
    [InlineData("read_saga", "search_memory")]
    [InlineData("search_archives", "search_history")]
    [InlineData("adjust_initiative", "set_task_priority")]
    [InlineData("send_commlink_alert", "send_notification")]
    [InlineData("petition_dungeon_master", "request_intervention")]
    [InlineData("run_spell_script", "run_script")]
    public void Unfinished_plan_diagnosis_names_replacement_without_inspecting_completed_history(string removed, string replacement)
    {
        var error = ApprenticeLegacyToolPolicy.Diagnose([
            new() { Description = "Historical read_file_chunk", Status = "completed", Result = "read_saga" },
            new() { Description = $"Use `{removed.ToUpperInvariant()}` here." },
        ], 0);

        Assert.NotNull(error);

        Assert.Contains(removed.ToUpperInvariant(), error.Value.Message);

        Assert.Contains(replacement, error.Value.Message);
    }

    [Theory]
    [InlineData("my_read_file_chunk_adapter")]
    [InlineData("read_file_chunk2")]
    [InlineData("read_file")]
    [InlineData("external_custom_tool")]
    public void Completed_history_and_identifier_substrings_do_not_block_unfinished_work(string description)
    {
        Assert.Null(ApprenticeLegacyToolPolicy.Diagnose([
            new() { Description = "read_file_chunk", Status = "completed", Result = "dispatch_sending" },
            new() { Description = description },
        ], 1));
    }

    [Fact]
    public void Tool_diagnostic_wire_kind_is_a_closed_string()
    {
        ToolNameDiagnostic diagnostic = Assert.Single(ToolNameDiagnostics.Removed(["read_file_chunk"]));

        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(diagnostic,
            RetroDownfall.Arcanum.Core.Serialization.GuildJsonContext.Default.ToolNameDiagnostic));

        Assert.Equal(System.Text.Json.JsonValueKind.String, json.RootElement.GetProperty("kind").ValueKind);

        Assert.Equal("removed", json.RootElement.GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData(ToolNameDiagnosticKind.Removed, "removed")]
    [InlineData(ToolNameDiagnosticKind.Unknown, "unknown")]
    [InlineData(ToolNameDiagnosticKind.Unavailable, "unavailable")]
    public void Tool_diagnostic_kinds_round_trip_through_generated_metadata(ToolNameDiagnosticKind kind, string wire)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(kind,
            RetroDownfall.Arcanum.Core.Serialization.GuildJsonContext.Default.ToolNameDiagnosticKind);

        Assert.Equal($"\"{wire}\"", json);

        Assert.Equal(kind, System.Text.Json.JsonSerializer.Deserialize(json,
            RetroDownfall.Arcanum.Core.Serialization.GuildJsonContext.Default.ToolNameDiagnosticKind));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("\"future\"")]
    public void Tool_diagnostic_kinds_refuse_numeric_and_unknown_values(string json)
    {
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize(json,
            RetroDownfall.Arcanum.Core.Serialization.GuildJsonContext.Default.ToolNameDiagnosticKind));
    }

    [Fact]
    public void Static_declaration_diagnosis_preserves_unknown_external_names_until_runtime_discovery()
    {
        Assert.Empty(ToolNameDiagnostics.Removed(["external_tool", "create_agent_task"]));

        Assert.Empty(ToolNameDiagnostics.Diagnose(["external_tool"], ["external_tool"]));

        Assert.Equal(ToolNameDiagnosticKind.Unknown, Assert.Single(ToolNameDiagnostics.Diagnose(["external_tool"], [])).Kind);

        Assert.Equal(ToolNameDiagnosticKind.Unavailable, Assert.Single(ToolNameDiagnostics.Diagnose(["create_agent_task"], [])).Kind);
    }
}
