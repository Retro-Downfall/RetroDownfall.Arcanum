using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Commands.Guild;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]
public sealed class GuildCommandTests
{
    private const string Id = "33333333-3333-3333-3333-333333333333";

    [Theory]
    [InlineData("guild status")]
    [InlineData("guild agent list --source declaredGlobal --skill review --page-size 2")]
    [InlineData("guild agent show agent --card-revision 2")]
    [InlineData("guild agent refresh --cursor next")]
    [InlineData("guild task create --goal goal --grant-file grant.json")]
    [InlineData("guild task delegate parent --goal goal --expected-revision 1")]
    [InlineData("guild task list --root root --parent parent --state working --cursor next --page-size 2")]
    [InlineData("guild task show task --show-content")]
    [InlineData("guild task tree task --cursor next --page-size 2")]
    [InlineData("guild task message task --text hello --expected-revision 1 --continue")]
    [InlineData("guild task cancel task --expected-revision 1")]
    [InlineData("guild task spend task")]
    [InlineData("guild signet revoke signet --expected-revision 1")]
    [InlineData("guild content show content --revision 1")]
    [InlineData("guild watch tree task --ndjson --after next")]
    [InlineData("guild watch cohort cohort --json")]
    [InlineData("guild cohort create --issuer task --name team --policy quorum --quorum 2 --result-type application/json")]
    [InlineData("guild cohort list --root root --state open --cursor next --page-size 2")]
    [InlineData("guild cohort show cohort")]
    [InlineData("guild cohort admit cohort --goal goal --expected-revision 1")]
    [InlineData("guild cohort members cohort --cursor next --page-size 2")]
    [InlineData("guild cohort remove cohort member --expected-revision 1")]
    [InlineData("guild cohort close cohort --expected-revision 1")]
    [InlineData("guild cohort cancel cohort --expected-revision 1")]
    [InlineData("guild cohort contributions cohort --selected --cursor next --page-size 2")]
    [InlineData("guild cohort convergence cohort")]
    [InlineData("guild cohort member-work cohort member --cursor next --page-size 2")]
    [InlineData("guild offer post cohort --skill review --goal goal --result-type text/plain --expected-cohort-revision 1")]
    [InlineData("guild offer list cohort --state open --cursor next --page-size 2")]
    [InlineData("guild offer show offer")]
    [InlineData("guild offer withdraw offer --expected-revision 1")]
    [InlineData("guild offer award offer --bid bid --expected-revision 1 --expected-cohort-revision 1")]
    [InlineData("guild bid list offer --cursor next --page-size 2")]
    [InlineData("guild note append cohort --kind correction --text correction --corrects note --expected-revision 1")]
    [InlineData("guild note list cohort --cursor next --page-size 2")]
    [InlineData("guild lease acquire --workspace workspace --scope src:subtree --scope readme.md:file")]
    [InlineData("guild lease list --workspace workspace --quest task --state held --cursor next --page-size 2")]
    [InlineData("guild lease renew lease --epoch 1 --expected-revision 1")]
    [InlineData("guild lease release lease --epoch 1 --expected-revision 1")]
    public void Guild_operator_commands_accept_the_documented_surface(string command)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        using ServiceProvider provider = services.BuildServiceProvider();

        RootCommand root = CliCommandTree.Build(provider, out _);

        Assert.Empty(root.Parse(command + " --plain --yes --no-context").Errors);
    }

    [Fact]
    public void Guild_status_json_is_one_typed_document()
    {
        RecordingHandler handler = new(_ => JsonResponse("""{"data":{"enabled":false,"available":true,"reasonCodes":[],"schemaVersion":12,"legacyPendingCount":3},"isSuccess":true,"error":null}"""));

        CliTestResult result = Run(handler, ["guild", "status", "--json"]);

        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Error} {result.Output}");

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(3, document.RootElement.GetProperty("legacyPendingCount").GetInt64());

        Assert.Equal("/api/guild/status", Assert.Single(handler.Requests).Path);
    }

    [Theory]
    [InlineData("guild watch tree", "--json", "--after", "next")]
    [InlineData("guild watch cohort", "--json", "--ndjson", "")]
    [InlineData("guild task tree", "--page-size", "101", "")]
    [InlineData("guild task cancel", "", "", "")]
    public void Invalid_Guild_input_is_refused_before_any_request(string prefix, string a, string b, string c)
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("Invalid input reached HTTP."));

        string[] args = [.. prefix.Split(' '), Id, .. new[] { a, b, c }.Where(static item => item.Length > 0)];

        CliTestResult result = Run(handler, args);

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Guild_cancel_preserves_revision_and_idempotency_and_accepts_pending_work()
    {
        RecordingHandler handler = new(_ => JsonResponse("""{"data":{"questId":"33333333-3333-3333-3333-333333333333","rootQuestId":"33333333-3333-3333-3333-333333333333","revision":2,"cancellationRequested":true,"cancellationConverged":false,"pendingExecutionCount":1,"unresolvedExternalEffectCount":0,"accountingState":"pending","eventWatermark":3,"receipt":null},"isSuccess":true,"error":null}""", HttpStatusCode.Accepted));

        CliTestResult result = Run(handler, ["guild", "task", "cancel", Id, "--expected-revision", "1", "--idempotency-key", Id, "--json"]);

        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Error} {result.Output}");

        using JsonDocument output = JsonDocument.Parse(result.Output);

        Assert.False(output.RootElement.GetProperty("cancellationConverged").GetBoolean());

        RecordedRequest request = Assert.Single(handler.Requests);

        Assert.Equal($"/api/guild/quests/{Id}/cancel", request.Path);

        using JsonDocument body = JsonDocument.Parse(request.Body!);

        Assert.Equal(Id, body.RootElement.GetProperty("idempotencyKey").GetString());

        Assert.Equal(1, body.RootElement.GetProperty("expectedRevision").GetInt64());

        Assert.Equal("operatorRequested", body.RootElement.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public void Guild_ndjson_preserves_event_and_cursor_then_reports_resync_failure()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("event: resync\nid: opaque-cursor\ndata: {\"reasonCode\":\"historyGap\",\"snapshotRoute\":\"/api/guild/quests/33333333-3333-3333-3333-333333333333/events\",\"resumeCursor\":\"new-cursor\"}\n\n", Encoding.UTF8, "text/event-stream"),
        });

        CliTestResult result = Run(handler, ["guild", "watch", "tree", Id, "--ndjson", "--after", "old-cursor"]);

        Assert.Equal(1, result.ExitCode);

        using JsonDocument output = JsonDocument.Parse(result.Output.Trim());

        Assert.Equal("resync", output.RootElement.GetProperty("event").GetString());

        Assert.Equal("opaque-cursor", output.RootElement.GetProperty("id").GetString());

        Assert.Equal("historyGap", output.RootElement.GetProperty("data").GetProperty("reasonCode").GetString());

        Assert.Contains("snapshot", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("?after=old-cursor", Assert.Single(handler.Requests).Query);
    }

    [Fact]
    public void Guild_watch_json_consumes_only_the_first_snapshot_and_disposes_the_stream()
    {
        string snapshot = "{\"summary\":" + QuestJson + ",\"members\":{\"items\":[],\"nextCursor\":\"member-page\",\"snapshotSequence\":7,\"stateConsistency\":\"livePerPage\"},\"watermark\":7,\"resumeCursor\":\"resume\"}";

        TrackedContent? content = null;

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content = new TrackedContent("event: snapshot\nid: resume\ndata: " + snapshot + "\n\nevent: change\ndata: {\"mustNotBeRead\":true}\n\n"),
        });

        CliTestResult result = Run(handler, ["guild", "watch", "tree", Id, "--json"]);

        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Error} {result.Output}");

        using JsonDocument output = JsonDocument.Parse(result.Output);

        Assert.Equal("resume", output.RootElement.GetProperty("resumeCursor").GetString());

        Assert.Equal("member-page", output.RootElement.GetProperty("members").GetProperty("nextCursor").GetString());

        Assert.DoesNotContain("mustNotBeRead", result.Output, StringComparison.Ordinal);

        Assert.True(content!.Disposed);

        Assert.Equal(string.Empty, Assert.Single(handler.Requests).Query);
    }

    [Fact]
    public void Guild_task_list_preserves_one_page_and_cursor_without_collecting_more()
    {
        RecordingHandler handler = new(_ => JsonResponse("{\"data\":{\"items\":[" + QuestJson + "],\"nextCursor\":\"next\",\"snapshotSequence\":7,\"stateConsistency\":\"livePerPage\"},\"isSuccess\":true,\"error\":null}"));

        CliTestResult result = Run(handler, ["guild", "task", "list", "--parent", Id, "--page-size", "2", "--json"]);

        Assert.True(result.ExitCode == 0, $"Exit {result.ExitCode}: {result.Error} {result.Output}");

        using JsonDocument output = JsonDocument.Parse(result.Output);

        Assert.Equal("next", output.RootElement.GetProperty("nextCursor").GetString());

        RecordedRequest request = Assert.Single(handler.Requests);

        Assert.Contains("parentId=" + Id, request.Query, StringComparison.Ordinal);

        Assert.Contains("pageSize=2", request.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cohort create --issuer ID --name team --policy all --result-type text/plain", "/api/guild/cohorts", "issuerQuestId", "ID")]
    [InlineData("cohort admit ID --goal delegated --expected-revision 2", "/api/guild/cohorts/ID/members", "expectedRevision", "2")]
    [InlineData("cohort remove ID ID --expected-revision 2", "/api/guild/cohorts/ID/members/ID/remove", "expectedRevision", "2")]
    [InlineData("cohort close ID --expected-revision 2", "/api/guild/cohorts/ID/close", "expectedRevision", "2")]
    [InlineData("cohort cancel ID --expected-revision 2", "/api/guild/cohorts/ID/cancel", "expectedRevision", "2")]
    [InlineData("offer post ID --skill review --goal work --result-type application/json --expected-cohort-revision 3", "/api/guild/cohorts/ID/offers", "expectedCohortRevision", "3")]
    [InlineData("offer withdraw ID --expected-revision 2", "/api/guild/offers/ID/withdraw", "expectedRevision", "2")]
    [InlineData("offer award ID --bid ID --expected-revision 2 --expected-cohort-revision 3", "/api/guild/offers/ID/award", "bidId", "ID")]
    [InlineData("note append ID --kind observation --text note --expected-revision 2", "/api/guild/cohorts/ID/notes", "body", "note")]
    [InlineData("lease renew ID --epoch 4 --expected-revision 2", "/api/guild/workspace-leases/ID/renew", "epoch", "4")]
    [InlineData("lease release ID --epoch 4 --expected-revision 2", "/api/guild/workspace-leases/ID/release", "epoch", "4")]
    [InlineData("signet revoke ID --expected-revision 2", "/api/guild/signets/ID/revoke", "reasonCode", "operatorRequested")]
    [InlineData("task delegate ID --goal child --expected-revision 2", "/api/guild/quests/ID/children", "goal", "child")]
    [InlineData("task message ID --text answer --continue --expected-revision 2", "/api/guild/quests/ID/messages", "kind", "continuation")]
    [InlineData("agent refresh", "/api/guild/agents/refresh", "agentId", "null")]
    public void Guild_mutations_send_typed_operator_data_and_preserve_domain_refusal(string command, string expectedPath, string property, string expectedValue)
    {
        RecordingHandler handler = new(_ => DomainRefusal());

        string[] args = ["guild", .. command.Replace("ID", Id, StringComparison.Ordinal).Split(' '), "--idempotency-key", Id, "--json"];

        CliTestResult result = Run(handler, args);

        Assert.Equal(1, result.ExitCode);

        Assert.Contains("current authorized revision 4", result.Error, StringComparison.Ordinal);

        RecordedRequest request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal(expectedPath.Replace("ID", Id, StringComparison.Ordinal), request.Path);

        using JsonDocument body = JsonDocument.Parse(request.Body!);

        Assert.Equal(Id, body.RootElement.GetProperty("idempotencyKey").GetString());

        JsonElement value = body.RootElement.GetProperty(property);

        Assert.Equal(expectedValue.Replace("ID", Id, StringComparison.Ordinal), value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());

        Assert.False(body.RootElement.TryGetProperty("authority", out _));

        Assert.False(body.RootElement.TryGetProperty("authorMemberId", out _));

        Assert.False(body.RootElement.TryGetProperty("operatorPrincipalId", out _));
    }

    [Theory]
    [InlineData("cohort list --root ID --state open", "/api/guild/cohorts", "rootId=ID")]
    [InlineData("cohort members ID", "/api/guild/cohorts/ID/members", "pageSize=100")]
    [InlineData("cohort contributions ID --selected", "/api/guild/cohorts/ID/contributions", "selected=true")]
    [InlineData("cohort member-work ID ID", "/api/guild/cohorts/ID/members/ID/quests", "pageSize=100")]
    [InlineData("offer list ID --state open", "/api/guild/cohorts/ID/offers", "state=open")]
    [InlineData("bid list ID", "/api/guild/offers/ID/bids", "pageSize=100")]
    [InlineData("note list ID", "/api/guild/cohorts/ID/notes", "pageSize=100")]
    [InlineData("lease list --quest ID --state held", "/api/guild/workspace-leases", "questId=ID")]
    [InlineData("content show ID --revision 5", "/api/guild/content/ID", "revision=5")]
    [InlineData("task tree ID --page-size 2 --cursor opaque", "/api/guild/quests/ID/tree", "cursor=opaque")]
    public void Guild_reads_use_exact_scoped_routes_and_filters(string command, string expectedPath, string expectedQuery)
    {
        RecordingHandler handler = new(_ => DomainRefusal());

        CliTestResult result = Run(handler, ["guild", .. command.Replace("ID", Id, StringComparison.Ordinal).Split(' '), "--json"]);

        Assert.Equal(1, result.ExitCode);

        RecordedRequest request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);

        Assert.Equal(expectedPath.Replace("ID", Id, StringComparison.Ordinal), request.Path);

        Assert.Contains(expectedQuery.Replace("ID", Id, StringComparison.Ordinal), request.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Guild_root_reads_a_complete_grant_and_utf8_goal_from_stdin()
    {
        string grantPath = Path.Combine(Path.GetTempPath(), "arcanum-guild-grant-" + Guid.NewGuid().ToString("N") + ".json");

        File.WriteAllText(grantPath, GrantJson, new UTF8Encoding(false));

        try
        {
            RecordingHandler handler = new(_ => JsonResponse("{\"data\":" + QuestJson + ",\"isSuccess\":true,\"error\":null}", HttpStatusCode.Accepted));

            CliTestResult result = Run(handler, ["guild", "task", "create", "--goal-file", "-", "--grant-file", grantPath, "--json"], "Review 🧙\nsecond line");

            Assert.True(result.ExitCode == 0, result.Error);

            using JsonDocument body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);

            Assert.Equal("Review 🧙\nsecond line", body.RootElement.GetProperty("goal").GetString());

            Assert.NotEqual(Guid.Empty, body.RootElement.GetProperty("idempotencyKey").GetGuid());

            Assert.Equal("unlimited", body.RootElement.GetProperty("grant").GetProperty("budget").GetProperty("tokens").GetProperty("mode").GetString());

            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("agentId").ValueKind);
        }
        finally
        {
            File.Delete(grantPath);
        }
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("campaign")]
    [InlineData("session")]
    public void Guild_owner_selectors_resolve_a_unique_uuid_prefix(string kind)
    {
        string grantPath = Path.Combine(Path.GetTempPath(), "arcanum-guild-owner-" + Guid.NewGuid().ToString("N") + ".json");

        File.WriteAllText(grantPath, GrantJson, new UTF8Encoding(false));

        try
        {
            string item = "{\"id\":\"" + Id + "\",\"name\":\"Owner\",\"title\":\"Owner\",\"path\":\"/registered/root\",\"type\":\"custom\",\"settings\":{},\"status\":\"active\",\"entryCount\":0,\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-01T00:00:00Z\",\"registeredAt\":\"2026-01-01T00:00:00Z\",\"persisted\":true}";

            string data = kind switch
            {
                "workspace" => "[" + item + "]",

                "campaign" => "{\"items\":[" + item + "],\"hasMore\":false}",

                _ => "{\"summaries\":[" + item + "],\"hasMore\":false}",
            };

            RecordingHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/guild/quests"
                ? DomainRefusal()
                : JsonResponse("{\"data\":" + data + ",\"isSuccess\":true,\"error\":null}"));

            CliTestResult result = Run(handler, ["guild", "task", "create", "--goal", "goal", "--grant-file", grantPath, "--" + kind, "33333333", "--json"]);

            Assert.True(result.ExitCode == 1, $"Exit {result.ExitCode}: {result.Error} {result.Output}");

            RecordedRequest request = Assert.Single(handler.Requests, static value => value.Path == "/api/guild/quests");

            using JsonDocument body = JsonDocument.Parse(request.Body!);

            Assert.Equal(Id, body.RootElement.GetProperty(kind + "Id").GetString());
        }
        finally
        {
            File.Delete(grantPath);
        }
    }

    [Fact]
    public void Guild_content_uuid_prefix_uses_authorized_metadata_resolution_for_message_content()
    {
        RecordingHandler handler = new(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/guild/content/resolve" => JsonResponse("{\"data\":{\"candidates\":[" + ContentReferenceJson(Id) + "]},\"isSuccess\":true,\"error\":null}"),

            "/api/guild/quests" => JsonResponse("{\"data\":{\"items\":[],\"nextCursor\":null,\"snapshotSequence\":0},\"isSuccess\":true,\"error\":null}"),

            _ => DomainRefusal(),
        });

        CliTestResult result = Run(handler, ["guild", "content", "show", "33333333", "--revision", "5", "--json"]);

        Assert.True(result.ExitCode == 1, $"Exit {result.ExitCode}: {result.Error} {result.Output}");

        RecordedRequest[] resolutions = [.. handler.Requests.Where(static request => request.Method == HttpMethod.Post)];

        Assert.NotEmpty(resolutions);

        Assert.All(resolutions, static resolution =>
        {
            Assert.Equal("/api/guild/content/resolve", resolution.Path);

            using JsonDocument body = JsonDocument.Parse(resolution.Body!);

            Assert.Equal("33333333", body.RootElement.GetProperty("prefix").GetString());
        });

        Assert.Contains(handler.Requests, static request => request.Path == "/api/guild/content/" + Id && request.Query?.Contains("revision=5", StringComparison.Ordinal) == true);

        Assert.DoesNotContain(handler.Requests, static request => request.Path == "/api/guild/quests");
    }

    [Fact]
    public void Guild_content_prefix_ambiguity_reports_only_visible_metadata_candidates()
    {
        const string other = "33333333-4444-4444-4444-444444444444";

        RecordingHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/guild/content/resolve"
            ? JsonResponse("{\"data\":{\"candidates\":[" + ContentReferenceJson(Id) + "," + ContentReferenceJson(other) + "]},\"isSuccess\":true,\"error\":null}")
            : JsonResponse("{\"data\":{\"items\":[],\"nextCursor\":null,\"snapshotSequence\":0},\"isSuccess\":true,\"error\":null}"));

        CliTestResult result = Run(handler, ["guild", "content", "show", "33333333", "--revision", "5", "--json"]);

        Assert.Equal(2, result.ExitCode);

        Assert.Contains(Id, result.Error, StringComparison.Ordinal);

        Assert.Contains(other, result.Error, StringComparison.Ordinal);

        Assert.All(handler.Requests, static request => Assert.Equal("/api/guild/content/resolve", request.Path));
    }

    [Fact]
    public void Guild_content_selection_requires_a_value_without_enumerating_protected_resources()
    {
        RecordingHandler handler = new(_ => JsonResponse("{\"data\":{\"items\":[],\"nextCursor\":null,\"snapshotSequence\":0},\"isSuccess\":true,\"error\":null}"));

        CliTestResult result = Run(handler, ["guild", "content", "show", "--revision", "5", "--json"]);

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Requests);
    }

    private static string ContentReferenceJson(string id) => "{\"contentId\":\"" + id + "\",\"kind\":\"circleBody\",\"revision\":5,\"digest\":\"aa\",\"mimeType\":\"text/plain\",\"byteLength\":4,\"contentErased\":false}";

    [Theory]
    [InlineData(Id)]
    [InlineData("/registered/root")]
    public void Guild_lease_acquire_resolves_registered_workspace_and_sends_all_scopes(string workspace)
    {
        RecordingHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/workspaces"
            ? JsonResponse("{\"data\":[{\"id\":\"" + Id + "\",\"name\":\"Owner\",\"path\":\"/registered/root\",\"type\":\"custom\",\"registeredAt\":\"2026-01-01T00:00:00Z\",\"persisted\":true}],\"isSuccess\":true,\"error\":null}")
            : DomainRefusal());

        CliTestResult result = Run(handler, ["guild", "lease", "acquire", "--workspace", workspace, "--scope", "src:subtree", "--scope", "readme.md:file", "--idempotency-key", Id, "--json"]);

        Assert.Equal(1, result.ExitCode);

        RecordedRequest request = Assert.Single(handler.Requests, static item => item.Method == HttpMethod.Post);

        Assert.Equal("/api/guild/workspace-leases", request.Path);

        using JsonDocument body = JsonDocument.Parse(request.Body!);

        Assert.Equal(Id, body.RootElement.GetProperty("workspaceId").GetString());

        JsonElement scopes = body.RootElement.GetProperty("scopes");

        Assert.Equal(2, scopes.GetArrayLength());

        Assert.Equal("src", scopes[0].GetProperty("relativePath").GetString());

        Assert.Equal("subtree", scopes[0].GetProperty("kind").GetString());

        Assert.Equal("readme.md", scopes[1].GetProperty("relativePath").GetString());

        Assert.Equal("file", scopes[1].GetProperty("kind").GetString());
    }

    [Fact]
    public void Guild_unknown_workspace_id_cannot_become_a_client_invented_lease_authority()
    {
        RecordingHandler handler = new(_ => JsonResponse("{\"data\":[],\"isSuccess\":true,\"error\":null}"));

        CliTestResult result = Run(handler, ["guild", "lease", "acquire", "--workspace", Id, "--scope", "src:subtree", "--json"]);

        Assert.Equal(2, result.ExitCode);

        Assert.Equal("/api/workspaces", Assert.Single(handler.Requests).Path);
    }

    [Fact]
    public void Guild_oversized_utf8_input_refuses_before_selecting_or_sending()
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("Oversized input reached HTTP."));

        CliTestResult result = Run(handler, ["guild", "task", "message", "ambiguous", "--text-file", "-", "--expected-revision", "1", "--json"], new string('é', 32_769));

        Assert.Equal(2, result.ExitCode);

        Assert.Contains("65536", result.Error, StringComparison.Ordinal);

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("goal", "--goal", "inline", "--goal-file", "-")]
    [InlineData("text", "--text", "inline", "--text-file", "-")]
    public void Guild_inline_and_file_inputs_are_mutually_exclusive(string inputName, string a, string b, string c, string d)
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("Conflicting input reached HTTP."));

        string[] args = inputName == "goal"
            ? ["guild", "task", "delegate", Id, a, b, c, d, "--expected-revision", "1"]
            : ["guild", "task", "message", Id, a, b, c, d, "--expected-revision", "1"];

        CliTestResult result = Run(handler, args, "stdin");

        Assert.Equal(2, result.ExitCode);

        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage DomainRefusal() => JsonResponse("""{"data":null,"isSuccess":false,"error":{"code":"Guild.RevisionConflict","message":"current authorized revision 4"}}""", HttpStatusCode.Conflict);

    [Theory]
    [InlineData("--json")]
    [InlineData("--print")]
    public void Guild_ambiguous_agent_names_refuse_with_visible_candidates_without_a_mutation(string headless)
    {
        string otherId = "44444444-4444-4444-4444-444444444444";

        RecordingHandler handler = new(_ => JsonResponse("{\"data\":{\"items\":[" + AgentJson(Id) + "," + AgentJson(otherId) + "],\"nextCursor\":null,\"snapshotSequence\":1,\"stateConsistency\":\"livePerPage\"},\"isSuccess\":true,\"error\":null}"));

        CliTestResult result = Run(handler, ["guild", "agent", "refresh", "Reviewer", headless]);

        Assert.Equal(2, result.ExitCode);

        Assert.Contains("ambiguous", result.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(Id, result.Error, StringComparison.Ordinal);

        Assert.Contains(otherId, result.Error, StringComparison.Ordinal);

        Assert.All(handler.Requests, static request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public void Guild_agent_uuid_prefix_selects_the_exact_card_instead_of_a_name_prefix()
    {
        RecordingHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/guild/agents"
            ? JsonResponse("{\"data\":{\"items\":[" + AgentJson(Id) + "],\"nextCursor\":null,\"snapshotSequence\":1,\"stateConsistency\":\"livePerPage\"},\"isSuccess\":true,\"error\":null}")
            : DomainRefusal());

        CliTestResult result = Run(handler, ["guild", "agent", "show", "33333333", "--card-revision", "7", "--json"]);

        Assert.Equal(1, result.ExitCode);

        RecordedRequest request = handler.Requests.Last();

        Assert.Equal("/api/guild/agents/" + Id, request.Path);

        Assert.Equal("?cardRevision=7", request.Query);
    }

    [Fact]
    public void Guild_agent_source_prefix_is_sent_as_a_source_filter()
    {
        RecordingHandler handler = new(request => request.RequestUri!.AbsolutePath == "/api/guild/agents"
            ? JsonResponse("{\"data\":{\"items\":[" + AgentJson(Id) + "],\"nextCursor\":null,\"snapshotSequence\":1,\"stateConsistency\":\"livePerPage\"},\"isSuccess\":true,\"error\":null}")
            : DomainRefusal());

        CliTestResult result = Run(handler, ["guild", "agent", "show", "global:Reviewer", "--json"]);

        Assert.Equal(1, result.ExitCode);

        Assert.All(handler.Requests.Where(static request => request.Path == "/api/guild/agents"),
            static request => Assert.Contains("source=declaredGlobal", request.Query, StringComparison.Ordinal));

        Assert.Equal("/api/guild/agents/" + Id, handler.Requests.Last().Path);
    }

    [Fact]
    public void Guild_unknown_stream_event_refuses_as_protocol_failure_without_publishing_data()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("event: invented\nid: cursor\ndata: {\"untrusted\":true}\n\n", Encoding.UTF8, "text/event-stream"),
        });

        CliTestResult result = Run(handler, ["guild", "watch", "tree", Id, "--ndjson"]);

        Assert.Equal(1, result.ExitCode);

        Assert.True(string.IsNullOrWhiteSpace(result.Output));

        Assert.Contains("event", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static string AgentJson(string id) => "{\"agentId\":\"" + id + "\",\"cardId\":\"" + id + "\",\"revision\":7,\"name\":\"Reviewer\",\"description\":\"Review tasks\",\"version\":\"1\",\"skills\":[],\"defaultInputModes\":[\"text/plain\"],\"defaultOutputModes\":[\"text/plain\"],\"capabilities\":{\"streaming\":true,\"pushNotifications\":false,\"stateTransitionHistory\":true},\"source\":{\"kind\":\"declaredGlobal\",\"workspaceId\":null,\"peerId\":null,\"available\":true,\"reasonCode\":null}}";

    [Fact]
    public async Task Guild_watch_cancellation_exits_130_and_disposes_the_active_subscription()
    {
        using ArcanumTestHomeScope home = new("guild-watch-cancellation");

        CancellationReadStream stream = new();

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });

        ServiceCollection services = Services(handler);

        StringWriter stdout = new();

        StringWriter stderr = new();

        services.AddSingleton<IConsoleDispatcher>(new ConsoleDispatcher(stdout, stderr, new(false, true, false)));

        await using ServiceProvider provider = services.BuildServiceProvider();

        using IDisposable invocation = CliInvocationContext.Push(new(false, true, false));

        using CancellationTokenSource cancellation = new();

        GuildCommands commands = provider.GetRequiredService<GuildCommands>();

        Task<int> running = commands.ExecuteAsync("watch tree", new([Id], new Dictionary<string, object?> { ["ndjson"] = true }), cancellation.Token);

        await stream.Reading.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();

        Assert.Equal(130, await running.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(stream.Disposed);

        Assert.True(string.IsNullOrWhiteSpace(stdout.ToString()));
    }

    private const string GrantJson = """{"workspaces":[],"tools":[],"memory":[],"budget":{"usd":{"mode":"unlimited"},"tokens":{"mode":"unlimited"}},"delegation":[],"expiresAtUtc":null}""";

    private static readonly string QuestJson = """{"id":"33333333-3333-3333-3333-333333333333","name":"Quest","operationId":"33333333-3333-3333-3333-333333333333","agentId":"33333333-3333-3333-3333-333333333333","cardId":"33333333-3333-3333-3333-333333333333","cardRevision":1,"runId":"33333333-3333-3333-3333-333333333333","parentQuestId":null,"rootQuestId":"33333333-3333-3333-3333-333333333333","sessionId":null,"campaignId":null,"workspaceId":null,"signetId":"33333333-3333-3333-3333-333333333333","signetRevision":1,"revision":1,"issuedSequence":1,"state":"working","waitReason":null,"cancellationRequested":false,"cancellationConverged":false,"reconciliationRequired":false,"deliveryState":"none","accountingState":"settled","goalRef":{"contentId":"33333333-3333-3333-3333-333333333333","kind":"questGoal","revision":1,"digest":"digest","mimeType":"text/plain","byteLength":4,"contentErased":false},"resultRef":null,"resultReceiptId":null,"resolvedGrant":ROLE_GRANT,"createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z","eventWatermark":7,"receipt":null}""".Replace("ROLE_GRANT", GrantJson, StringComparison.Ordinal);

    private static CliTestResult Run(RecordingHandler handler, string[] args, string? input = null)
    {
        return CliTestHarness.RunAsync(Services(handler), args, input).GetAwaiter().GetResult();
    }

    private static ServiceCollection Services(RecordingHandler handler)
    {
        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(new Factory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(new FakeSecretStore());

        CliTestHarness.AddKeyedArcanumResponder(services, "test-key");

        return services;
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? Query, string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            return respond(request);
        }
    }

    private sealed class TrackedContent(string text) : StringContent(text, Encoding.UTF8, "text/event-stream")
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;

            base.Dispose(disposing);
        }
    }

    private sealed class CancellationReadStream : Stream
    {
        public TaskCompletionSource<bool> Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult(true);

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return 0;
        }

        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult<string?>("test-key");

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() => Task.FromResult(SecretStoreReadResult.Ok("test-key"));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;
    }

    private sealed class Factory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:5001/") };
    }
}
