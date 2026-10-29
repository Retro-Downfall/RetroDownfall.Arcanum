using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.DataLifecycle;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed partial class DataRetentionCommandTests
{
    [Fact]
    public void Prune_preview_uses_exact_diagnostic_totals_and_names_omitted_details()
    {
        DataRetentionPlan plan = CreatePlan() with
        {
            Blockers = [.. Enumerable.Range(0, 20).Select(static index => new DataRetentionBlocker(
                RetentionDataClass.Entries, index.ToString(), "Data.SessionHold", "An operator hold protects this entry."))],
            Diagnostics = new(4_000, 0, [new(RetentionDataClass.Entries, "Data.SessionHold", 4_000)], [],
                1, [new(Guid.NewGuid(), 4_000, 0)], "snapshot", false),
        };

        RecordingHandler handler = new(_ => SuccessResponse(plan, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan));

        CliTestResult result = RunCommand(handler, ["data", "prune", "--dry-run"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("Blockers: 4,000", result.Output, StringComparison.Ordinal);

        Assert.Contains("3,980", result.Output, StringComparison.Ordinal);

        Assert.Contains("prune-details", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Prune_legacy_preview_points_to_complete_json_without_offering_unavailable_snapshot_pages()
    {
        DataRetentionPlan plan = CreatePlan() with
        {
            Blockers = [.. Enumerable.Range(0, 40).Select(static index => new DataRetentionBlocker(
                RetentionDataClass.Entries, index.ToString(), "Data.SessionHold", "Protected by a hold."))],
        };

        RecordingHandler handler = new(_ => SuccessResponse(plan, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan));

        CliTestResult result = RunCommand(handler, ["data", "prune", "--dry-run"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("--json", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("prune-details", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Prune_human_apply_bounds_detail_lines_without_truncating_json()
    {
        DataRetentionPlan plan = CreatePlan();

        DataRetentionApplyResult applied = CreateApplyResult(plan.PlanId) with
        {
            Conflicts = [.. Enumerable.Range(0, 1_000).Select(static index => new DataRetentionConflict(
                "Data.PlanChanged", index.ToString(), "The candidate changed and was preserved."))],
        };

        RecordingHandler handler = new(request => request.Path switch
        {
            "/api/data/prune/plan" => SuccessResponse(plan, ArcanumJsonContext.Default.ApiResponseDataRetentionPlan),
            "/api/data/prune" => SuccessResponse(applied, ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult),
            _ => ErrorResponse(),
        });

        CliTestResult human = RunCommand(handler, ["data", "prune", "--apply", "--yes"]);

        Assert.Equal(0, human.ExitCode);

        Assert.Contains("conflicts: 1,000", human.Output, StringComparison.Ordinal);

        Assert.InRange(human.Output.Split('\n').Length, 1, 80);

        CliTestResult machine = RunCommand(handler, ["data", "prune", "--apply", "--yes", "--json"]);

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(machine.Output);

        Assert.Equal(1_000, document.RootElement.GetProperty("conflicts").GetArrayLength());
    }

    [Fact]
    public void Prune_details_sends_the_exact_snapshot_and_writes_one_typed_page()
    {
        string planId = new('A', 64);

        DataRetentionDiagnosticPage page = new(planId, DateTimeOffset.Parse("2026-10-28T00:00:00Z"),
            2, 2, 0, [new(true, RetentionDataClass.Entries, "entry-id", "Data.SessionHold", "Protected by an operator hold.", Guid.NewGuid())], "next-page");

        RecordingHandler handler = new(_ => SuccessResponse(page,
            ArcanumJsonContext.Default.ApiResponseDataRetentionDiagnosticPage));

        CliTestResult result = RunCommand(handler, ["data", "prune-details", "--plan-id", planId,
            "--snapshot-id", new string('B', 64), "--observed-at", "2026-10-28T00:00:00Z", "--limit", "11", "--continuation", "previous-page", "--json"]);

        Assert.Equal(0, result.ExitCode);

        RecordedRequest request = Assert.Single(handler.Requests);

        Assert.Equal("/api/data/prune/diagnostics", request.Path);

        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(request.Body!);

        Assert.Equal(planId, body.RootElement.GetProperty("expectedPlanId").GetString());

        Assert.Equal(new string('B', 64), body.RootElement.GetProperty("expectedSnapshotId").GetString());

        Assert.Equal(11, body.RootElement.GetProperty("limit").GetInt32());

        Assert.Equal("previous-page", body.RootElement.GetProperty("continuation").GetString());

        using System.Text.Json.JsonDocument output = System.Text.Json.JsonDocument.Parse(result.Output);

        Assert.Equal("next-page", output.RootElement.GetProperty("nextContinuation").GetString());
    }
}
