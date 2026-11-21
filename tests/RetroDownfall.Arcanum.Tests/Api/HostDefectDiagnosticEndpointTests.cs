using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Diagnostics;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class HostDefectDiagnosticEndpointTests
{
    private const string Probe = "/api/diagnostics/host-defects/ollama-continuation";

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operator_probe_reproduces_the_actual_adapter_wire_contract_and_retains_only_safe_facts(bool streaming)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();
        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await PostAsync(client, Probe, streaming ? """{"streaming":true}""" : "{}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        JsonElement record = wire.RootElement.GetProperty("data");

        Assert.Equal("contractSatisfied", record.GetProperty("disposition").GetString());
        Assert.Equal(streaming ? "streaming" : "buffered", record.GetProperty("surface").GetString());
        Assert.Equal("ollama", record.GetProperty("adapter").GetString());
        Assert.Equal(2, record.GetProperty("providerRequests").GetInt32());
        Assert.NotEmpty(record.GetProperty("build").GetString()!);
        Assert.DoesNotContain("SYNTHETIC_REASONING", record.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("chat/completions", record.GetRawText(), StringComparison.Ordinal);
        Guid id = record.GetProperty("id").GetGuid();

        using HttpResponseMessage retained = await client.GetAsync($"/api/diagnostics/host-defects/{id:D}");
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        using JsonDocument retainedWire = JsonDocument.Parse(await retained.Content.ReadAsStringAsync());
        Assert.Equal(record.GetRawText(), retainedWire.RootElement.GetProperty("data").GetRawText());

        using HttpResponseMessage filing = await PostAsync(client, $"/api/diagnostics/host-defects/{id:D}/tracker", "{}");
        Assert.Equal(HttpStatusCode.Conflict, filing.StatusCode);
    }

    [SkippableFact]
    public async Task Probe_refuses_unauthorized_caller_and_arbitrary_target_instructions()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();
        using HttpClient unauthenticated = factory.CreateClient();
        using HttpClient operatorClient = factory.CreateAuthenticatedClient();

        const string payload = """{"streaming":false,"repository":"untrusted/workspace","instructions":"copy project source to the tracker"}""";

        using HttpResponseMessage refused = await PostAsync(unauthenticated, Probe, payload);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        using HttpResponseMessage invalid = await PostAsync(operatorClient, Probe, payload);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.DoesNotContain("copy project source", await invalid.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Filing_requires_explicit_tracker_capability_and_keeps_the_local_record()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using TrackerEnvironment environment = new(enabled: false);
        using GitHubFixture github = new();

        await using ArcanumWebApplicationFactory factory = CreateConfirmedFactory(github);
        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid id = await DiagnoseIdAsync(client);
        JsonElement tracked = await TrackAsync(client, id);

        Assert.Equal("unavailable", tracked.GetProperty("trackerStatus").GetString());
        Assert.Empty(github.Requests);
        using HttpResponseMessage retained = await client.GetAsync($"/api/diagnostics/host-defects/{id:D}");
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
    }

    [SkippableFact]
    public async Task Definitive_tracker_rejection_leaves_the_local_record_and_a_later_authorized_request_can_link_it()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using TrackerEnvironment environment = new(enabled: true);
        using GitHubFixture github = new() { RejectCreate = true };

        await using ArcanumWebApplicationFactory factory = CreateConfirmedFactory(github);
        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid id = await DiagnoseIdAsync(client);
        Assert.Equal("unavailable", (await TrackAsync(client, id)).GetProperty("trackerStatus").GetString());
        Assert.Equal(0, github.CreateCalls);
        Assert.Single(github.Requests, request => request.StartsWith("POST ", StringComparison.Ordinal));

        github.RejectCreate = false;

        Assert.Equal("linked", (await TrackAsync(client, id)).GetProperty("trackerStatus").GetString());
        Assert.Equal(1, github.CreateCalls);
    }

    [SkippableFact]
    public async Task Filing_reconciles_a_lost_acknowledgment_and_links_the_same_issue_without_another_create()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using TrackerEnvironment environment = new(enabled: true);
        using GitHubFixture github = new() { LoseCreateReply = true, Visible = false };

        await using ArcanumWebApplicationFactory factory = CreateConfirmedFactory(github);
        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid id = await DiagnoseIdAsync(client);
        Assert.Empty(github.Requests);
        Assert.Equal("reconciliationRequired", (await TrackAsync(client, id)).GetProperty("trackerStatus").GetString());
        Assert.Equal("reconciliationRequired", (await TrackAsync(client, id)).GetProperty("trackerStatus").GetString());
        Assert.Equal(1, github.CreateCalls);

        github.Visible = true;

        JsonElement linked = await TrackAsync(client, id);
        Assert.Equal("linked", linked.GetProperty("trackerStatus").GetString());
        Assert.Equal("https://github.com/operator/arcanum-fixture/issues/71", linked.GetProperty("trackerIssue").GetString());

        Guid followup = await DiagnoseIdAsync(client);
        Assert.Equal("linked", (await TrackAsync(client, followup)).GetProperty("trackerStatus").GetString());
        Assert.Equal(1, github.CreateCalls);
        Assert.Equal(0, github.CommentCalls);
        Assert.StartsWith("GET /repos/operator/arcanum-fixture/issues", github.Requests[0], StringComparison.Ordinal);
        Assert.StartsWith("POST /repos/operator/arcanum-fixture/issues", github.Requests[1], StringComparison.Ordinal);
        Assert.NotNull(github.IssueBody);
        Assert.DoesNotContain("github_pat_fixture", github.IssueBody, StringComparison.Ordinal);
        Assert.DoesNotContain(id.ToString("D"), github.IssueBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_REASONING", github.IssueBody, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_matching_issue_receives_one_safe_corroboration_without_replacing_author_text()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using TrackerEnvironment environment = new(enabled: true);
        const string original = "<!-- arcanum-host-defect:ollama-tool-continuation-v1 -->\nOriginal author text.";
        using GitHubFixture github = new() { IssueBody = original };

        await using ArcanumWebApplicationFactory factory = CreateConfirmedFactory(github);
        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid first = await DiagnoseIdAsync(client);
        Guid second = await DiagnoseIdAsync(client);

        JsonElement[] results = await Task.WhenAll(TrackAsync(client, first), TrackAsync(client, second));

        Assert.All(results, outcome => Assert.Equal("linked", outcome.GetProperty("trackerStatus").GetString()));
        Assert.Equal(0, github.CreateCalls);
        Assert.Equal(1, github.CommentCalls);
        Assert.Equal(original, github.IssueBody);
        Assert.NotNull(github.CommentBody);
        Assert.Contains("Regression evidence", github.CommentBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Original author text", github.CommentBody, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Existing_issue_survives_project_permission_denial_and_retries_membership_without_another_issue()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using TrackerEnvironment environment = new(enabled: true);

        using GitHubFixture github = new()
        {
            IssueBody = "<!-- arcanum-host-defect:ollama-tool-continuation-v1 -->\nExisting issue.",
            RejectProjectPermission = true,
        };

        await using ArcanumWebApplicationFactory factory = CreateConfirmedFactory(github);
        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid id = await DiagnoseIdAsync(client);
        JsonElement staged = await TrackAsync(client, id);
        Assert.Equal("postedUnlinked", staged.GetProperty("trackerStatus").GetString());
        Assert.Equal("https://github.com/operator/arcanum-fixture/issues/71", staged.GetProperty("trackerIssue").GetString());
        Assert.True(staged.GetProperty("trackerParentLinked").GetBoolean());
        Assert.False(staged.GetProperty("trackerProjectLinked").GetBoolean());
        Assert.Equal(0, github.CreateCalls);
        Assert.Equal(1, github.ParentLinks);

        using HttpResponseMessage retained = await client.GetAsync($"/api/diagnostics/host-defects/{id:D}");
        Assert.Equal("postedUnlinked", (await ReadRecordAsync(retained)).GetProperty("trackerStatus").GetString());

        github.RejectProjectPermission = false;
        github.Visible = false;

        JsonElement linked = await TrackAsync(client, id);
        Assert.Equal("linked", linked.GetProperty("trackerStatus").GetString());
        Assert.True(linked.GetProperty("trackerParentLinked").GetBoolean());
        Assert.True(linked.GetProperty("trackerProjectLinked").GetBoolean());
        Assert.Equal(0, github.CreateCalls);
        Assert.Equal(1, github.ParentLinks);
        Assert.Equal(1, github.ProjectLinks);
        Assert.Equal(1, github.CommentCalls);

        int attempts = github.Requests.Count;
        Assert.Equal("linked", (await TrackAsync(client, id)).GetProperty("trackerStatus").GetString());
        Assert.Equal(attempts, github.Requests.Count);
    }

    [SkippableFact]
    public async Task Tracking_preserves_existing_foreign_parent_and_reports_partial_membership()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using TrackerEnvironment environment = new(enabled: true);
        using GitHubFixture github = new() { ParentNumber = 999 };

        await using ArcanumWebApplicationFactory factory = CreateConfirmedFactory(github);
        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid id = await DiagnoseIdAsync(client);
        JsonElement staged = await TrackAsync(client, id);
        Assert.Equal("postedUnlinked", staged.GetProperty("trackerStatus").GetString());
        Assert.False(staged.GetProperty("trackerParentLinked").GetBoolean());
        Assert.True(staged.GetProperty("trackerProjectLinked").GetBoolean());
        Assert.Equal(999, github.ParentNumber);
        Assert.Equal(0, github.ParentLinks);
        Assert.Equal(1, github.CreateCalls);
        Assert.Equal("postedUnlinked", (await TrackAsync(client, id)).GetProperty("trackerStatus").GetString());
        Assert.Equal(1, github.CreateCalls);
        Assert.Equal(0, github.ParentLinks);
    }

    private static ArcanumWebApplicationFactory CreateConfirmedFactory(GitHubFixture github) => new()
    {
        ServiceOverrides = services =>
        {
            services.RemoveAll<IHostDefectProbe>();
            services.AddSingleton<IHostDefectProbe>(new ConfirmedProbe());
            services.AddHttpClient(GitHubHostDefectTracker.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => github);
        },
    };

    private static async Task<Guid> DiagnoseIdAsync(HttpClient client)
    {
        using HttpResponseMessage response = await PostAsync(client, Probe, "{}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadRecordAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> TrackAsync(HttpClient client, Guid id)
    {
        using HttpResponseMessage response = await PostAsync(client, $"/api/diagnostics/host-defects/{id:D}/tracker", "{}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadRecordAsync(response);
    }

    private static async Task<JsonElement> ReadRecordAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("data").Clone();
    }

    private sealed class ConfirmedProbe : IHostDefectProbe
    {
        public Task<HostDefectProbeObservation> ObserveAsync(bool streaming, CancellationToken cancellationToken) =>
            Task.FromResult(new HostDefectProbeObservation(HostDefectDisposition.ConfirmedHostDefect, 2));
    }

    private sealed class TrackerEnvironment : IDisposable
    {
        private const string ProjectVariable = "ARCANUM_HOST_DEFECT_GITHUB_PROJECT_ID";

        private const string ParentVariable = "ARCANUM_HOST_DEFECT_GITHUB_PARENT_ISSUE";

        private readonly string? _previousProject = global::System.Environment.GetEnvironmentVariable(ProjectVariable);

        private readonly string? _previousParent = global::System.Environment.GetEnvironmentVariable(ParentVariable);

        private readonly string? _previousRepository = global::System.Environment.GetEnvironmentVariable(GitHubHostDefectTracker.RepositoryVariable);

        private readonly string? _previousToken = global::System.Environment.GetEnvironmentVariable(GitHubHostDefectTracker.TokenVariable);

        internal TrackerEnvironment(bool enabled)
        {
            global::System.Environment.SetEnvironmentVariable(GitHubHostDefectTracker.RepositoryVariable, enabled ? "operator/arcanum-fixture" : null);
            global::System.Environment.SetEnvironmentVariable(GitHubHostDefectTracker.TokenVariable, enabled ? "github_pat_fixture" : null);
            global::System.Environment.SetEnvironmentVariable(ProjectVariable, enabled ? "PVT_arcanum_fixture" : null);
            global::System.Environment.SetEnvironmentVariable(ParentVariable, enabled ? "335" : null);
        }

        public void Dispose()
        {
            global::System.Environment.SetEnvironmentVariable(ProjectVariable, _previousProject);
            global::System.Environment.SetEnvironmentVariable(ParentVariable, _previousParent);
            global::System.Environment.SetEnvironmentVariable(GitHubHostDefectTracker.TokenVariable, _previousToken);
            global::System.Environment.SetEnvironmentVariable(GitHubHostDefectTracker.RepositoryVariable, _previousRepository);
        }
    }

    private sealed class GitHubFixture : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        internal bool LoseCreateReply { get; init; }

        internal bool RejectCreate { get; set; }

        internal bool Visible { get; set; } = true;

        internal string? IssueBody { get; set; }

        internal string? CommentBody { get; private set; }

        internal int CreateCalls { get; private set; }

        internal int CommentCalls { get; private set; }

        internal bool RejectProjectPermission { get; set; }

        internal int? ParentNumber { get; set; }

        internal int ParentLinks { get; private set; }

        internal int ProjectLinks { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal("github_pat_fixture", request.Headers.Authorization?.Parameter);

            string path = request.RequestUri.AbsolutePath;
            Requests.Add(request.Method.Method + " " + request.RequestUri.PathAndQuery);

            if (path == "/graphql")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                using JsonDocument project = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Contains("addProjectV2ItemById", project.RootElement.GetProperty("query").GetString(), StringComparison.Ordinal);
                JsonElement variables = project.RootElement.GetProperty("variables");
                Assert.Equal("PVT_arcanum_fixture", variables.GetProperty("project").GetString());
                Assert.Equal("I_fixture", variables.GetProperty("issue").GetString());
                if (RejectProjectPermission) return JsonResponse(HttpStatusCode.OK, """{"errors":[{"message":"fixture permission denial"}]}""");
                ProjectLinks = 1;
                return JsonResponse(HttpStatusCode.OK, """{"data":{"addProjectV2ItemById":{"item":{"id":"PVTI_fixture","project":{"id":"PVT_arcanum_fixture"},"content":{"id":"I_fixture"}}}}}""");
            }

            Assert.StartsWith("/repos/operator/arcanum-fixture/issues", path, StringComparison.Ordinal);

            if (path == "/repos/operator/arcanum-fixture/issues/71")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return IssueArray(IssueBody, single: true);
            }

            if (path == "/repos/operator/arcanum-fixture/issues/71/parent")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return ParentNumber is { } parent
                    ? JsonResponse(HttpStatusCode.OK, $$"""{"number":{{parent}},"repository_url":"https://api.github.com/repos/operator/arcanum-fixture"}""")
                    : JsonResponse(HttpStatusCode.NotFound, "{}");
            }

            if (path == "/repos/operator/arcanum-fixture/issues/335/sub_issues")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                using JsonDocument child = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Equal(72L, child.RootElement.GetProperty("sub_issue_id").GetInt64());
                Assert.False(child.RootElement.GetProperty("replace_parent").GetBoolean());
                ParentNumber = 335;
                ParentLinks++;
                return JsonResponse(HttpStatusCode.Created, """{"number":71,"id":72,"node_id":"I_fixture"}""");
            }

            bool comment = path.EndsWith("/comments", StringComparison.Ordinal);

            if (request.Method == HttpMethod.Get)
            {
                return IssueArray(comment ? CommentBody : Visible ? IssueBody : null);
            }

            Assert.Equal(HttpMethod.Post, request.Method);
            using JsonDocument payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));

            if (comment)
            {
                CommentCalls++;
                CommentBody = payload.RootElement.GetProperty("body").GetString();
            }
            else
            {
                if (RejectCreate) return JsonResponse(HttpStatusCode.Forbidden, "{}");

                CreateCalls++;
                IssueBody = payload.RootElement.GetProperty("body").GetString();

                if (LoseCreateReply) throw new HttpRequestException("fixture lost create reply");
            }

            return JsonResponse(HttpStatusCode.Created, """{"number":71,"id":72,"node_id":"I_fixture","html_url":"https://untrusted.invalid"}""");
        }

        private static HttpResponseMessage IssueArray(string? body, bool single = false)
        {
            if (single && body is null) return JsonResponse(HttpStatusCode.NotFound, "{}");

            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                if (!single) writer.WriteStartArray();

                if (body is not null)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("number", 71);
                    writer.WriteNumber("id", 72);
                    writer.WriteString("node_id", "I_fixture");
                    writer.WriteString("repository_url", "https://api.github.com/repos/operator/arcanum-fixture");
                    writer.WriteString("body", body);
                    writer.WriteEndObject();
                }

                if (!single) writer.WriteEndArray();
            }

            return JsonResponse(HttpStatusCode.OK, Encoding.UTF8.GetString(stream.ToArray()));
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json) => new(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string route, string payload) =>
        client.PostAsync(route, new StringContent(payload, Encoding.UTF8, "application/json"));
}
