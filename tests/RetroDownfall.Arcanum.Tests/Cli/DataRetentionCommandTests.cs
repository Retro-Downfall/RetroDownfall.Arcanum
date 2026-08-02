using System.Net;

using System.Text;

using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class DataRetentionCommandTests
{

    [Fact]

    public void Data_help_exposes_retention_and_explicit_deletion_commands()
    {

        RecordingHandler handler = new();

        CliTestResult data = RunCommand(handler, ["data", "--help"]);

        CliTestResult retention = RunCommand(
            handler,
            ["data", "retention", "--help"]);

        CliTestResult prune = RunCommand(
            handler,
            ["data", "prune", "--help"]);

        Assert.Equal(0, data.ExitCode);

        Assert.Contains("encryption", data.Output, StringComparison.Ordinal);

        Assert.Contains("status", data.Output, StringComparison.Ordinal);

        Assert.Contains("retention", data.Output, StringComparison.Ordinal);

        Assert.Contains("prune", data.Output, StringComparison.Ordinal);

        Assert.Contains("delete-session", data.Output, StringComparison.Ordinal);

        Assert.Contains("delete-attachment", data.Output, StringComparison.Ordinal);

        Assert.Contains("reset-memory", data.Output, StringComparison.Ordinal);

        Assert.Contains("factory-reset", data.Output, StringComparison.Ordinal);

        Assert.Equal(0, retention.ExitCode);

        Assert.Contains("show", retention.Output, StringComparison.Ordinal);

        Assert.Contains("set", retention.Output, StringComparison.Ordinal);

        Assert.Equal(0, prune.ExitCode);

        Assert.Contains("--dry-run", prune.Output, StringComparison.Ordinal);

        Assert.Contains("--apply", prune.Output, StringComparison.Ordinal);

    }

    [Theory]

    [InlineData("GET", "/api/data/status", "data status")]

    [InlineData("GET", "/api/data/retention", "data retention show")]

    [InlineData("POST", "/api/data/prune/plan", "data prune --dry-run")]

    public void Read_only_data_commands_route_through_authenticated_api_without_confirmation(
        string method,
        string path,
        string commandLine)
    {

        RecordingHandler handler = new(_ => ErrorResponse());

        _ = RunCommand(handler, Split(commandLine));

        RecordedRequest request = Assert.Single(handler.Requests);

        Assert.Equal(new HttpMethod(method), request.Method);

        Assert.Equal(path, request.Path);

    }

    [Fact]

    public void Prune_apply_previews_the_exact_plan_then_binds_its_id_to_apply()
    {

        DataRetentionPlan plan = CreatePlan();

        DataRetentionApplyResult applied = CreateApplyResult(plan.PlanId);

        RecordingConfirmationPrompt prompt = new(confirmed: true);

        RecordingHandler handler = new(request => request.Path switch
        {

            "/api/data/prune/plan" => SuccessResponse(
                plan,
                ArcanumJsonContext.Default.ApiResponseDataRetentionPlan),

            "/api/data/prune" => SuccessResponse(
                applied,
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult),

            _ => ErrorResponse(),

        });

        CliTestResult result = RunCommand(
            handler,
            ["data", "prune", "--apply"],
            prompt);

        Assert.Equal(0, result.ExitCode);

        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("/api/data/prune/plan", request.Path),
            request =>
            {

                Assert.Equal("/api/data/prune", request.Path);

                DataRetentionApplyRequest? body = JsonSerializer.Deserialize(
                    request.Body,
                    ArcanumJsonContext.Default.DataRetentionApplyRequest);

                Assert.Equal(plan.PlanId, body?.ExpectedPlanId);

            });

        Assert.Contains(plan.PlanId, prompt.Question, StringComparison.Ordinal);

        Assert.Contains("12 rows", prompt.Question, StringComparison.Ordinal);

        Assert.Contains("3 files", prompt.Question, StringComparison.Ordinal);

        Assert.Contains("4,096 bytes", prompt.Question, StringComparison.Ordinal);

        Assert.Contains("Prune plan", result.Output, StringComparison.Ordinal);

        Assert.Contains("Apply complete", result.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Json_prune_apply_emits_only_the_exact_apply_result()
    {

        DataRetentionPlan plan = CreatePlan();

        DataRetentionApplyResult applied = CreateApplyResult(plan.PlanId);

        RecordingHandler handler = new(request => request.Path switch
        {

            "/api/data/prune/plan" => SuccessResponse(
                plan,
                ArcanumJsonContext.Default.ApiResponseDataRetentionPlan),

            "/api/data/prune" => SuccessResponse(
                applied,
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult),

            _ => ErrorResponse(),

        });

        CliTestResult result = RunCommand(
            handler,
            ["--json", "--yes", "data", "prune", "--apply"]);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(2, handler.Requests.Count);

        DataRetentionApplyRequest? body = JsonSerializer.Deserialize(
            handler.Requests[1].Body,
            ArcanumJsonContext.Default.DataRetentionApplyRequest);

        Assert.Equal(plan.PlanId, body?.ExpectedPlanId);

        Assert.Equal(
            JsonSerializer.Serialize(
                applied,
                ArcanumJsonContext.Default.DataRetentionApplyResult),
            result.Output.Trim());

    }

    [Fact]

    public void Human_read_commands_render_operator_summaries_instead_of_raw_json()
    {

        DataRetentionStatus status = CreateStatus();

        RetentionSettings settings = CreateSettings();

        DataRetentionPlan plan = CreatePlan();

        RecordingHandler handler = new(request => request.Path switch
        {

            "/api/data/status" => SuccessResponse(
                status,
                ArcanumJsonContext.Default.ApiResponseDataRetentionStatus),

            "/api/data/retention" => SuccessResponse(
                settings,
                ArcanumJsonContext.Default.ApiResponseRetentionSettings),

            "/api/data/prune/plan" => SuccessResponse(
                plan,
                ArcanumJsonContext.Default.ApiResponseDataRetentionPlan),

            _ => ErrorResponse(),

        });

        CliTestResult statusResult = RunCommand(
            handler,
            ["data", "status"]);

        CliTestResult settingsResult = RunCommand(
            handler,
            ["data", "retention", "show"]);

        CliTestResult planResult = RunCommand(
            handler,
            ["data", "prune", "--dry-run"]);

        Assert.Contains(
            "Data retention status",
            statusResult.Output,
            StringComparison.Ordinal);

        Assert.Contains(
            "12 rows, 3 files, 4,096 bytes",
            statusResult.Output,
            StringComparison.Ordinal);

        Assert.DoesNotContain("{", statusResult.Output, StringComparison.Ordinal);

        Assert.Contains(
            "Retention settings",
            settingsResult.Output,
            StringComparison.Ordinal);

        Assert.Contains(
            "archived-sessions: enabled, 180 days",
            settingsResult.Output,
            StringComparison.Ordinal);

        Assert.DoesNotContain("{", settingsResult.Output, StringComparison.Ordinal);

        Assert.Contains("Prune plan", planResult.Output, StringComparison.Ordinal);

        Assert.Contains(plan.PlanId, planResult.Output, StringComparison.Ordinal);

        Assert.Contains(
            "12 rows, 3 files, 4,096 bytes, 7 derived records",
            planResult.Output,
            StringComparison.Ordinal);

        Assert.DoesNotContain("{", planResult.Output, StringComparison.Ordinal);

    }

    [Fact]

    public void Json_read_commands_emit_the_exact_api_payloads()
    {

        DataRetentionStatus status = CreateStatus();

        RetentionSettings settings = CreateSettings();

        DataRetentionPlan plan = CreatePlan();

        RecordingHandler handler = new(request => request.Path switch
        {

            "/api/data/status" => SuccessResponse(
                status,
                ArcanumJsonContext.Default.ApiResponseDataRetentionStatus),

            "/api/data/retention" => SuccessResponse(
                settings,
                ArcanumJsonContext.Default.ApiResponseRetentionSettings),

            "/api/data/prune/plan" => SuccessResponse(
                plan,
                ArcanumJsonContext.Default.ApiResponseDataRetentionPlan),

            _ => ErrorResponse(),

        });

        Assert.Equal(
            JsonSerializer.Serialize(
                status,
                ArcanumJsonContext.Default.DataRetentionStatus),
            RunCommand(handler, ["--json", "data", "status"]).Output.Trim());

        Assert.Equal(
            JsonSerializer.Serialize(
                settings,
                ArcanumJsonContext.Default.RetentionSettings),
            RunCommand(
                handler,
                ["--json", "data", "retention", "show"])
                .Output
                .Trim());

        Assert.Equal(
            JsonSerializer.Serialize(
                plan,
                ArcanumJsonContext.Default.DataRetentionPlan),
            RunCommand(
                handler,
                ["--json", "data", "prune", "--dry-run"])
                .Output
                .Trim());

    }

    [Fact]

    public void Factory_reset_confirmation_names_preserved_configuration_and_key_material()
    {

        RecordingHandler handler = new(_ => ErrorResponse());

        RecordingConfirmationPrompt prompt = new(confirmed: false);

        CliTestResult result = RunCommand(
            handler,
            ["data", "factory-reset"],
            prompt);

        Assert.Equal(0, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("backups", prompt.Question, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("outside", prompt.Question, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("arcanum.json", prompt.Question, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("security", prompt.Question, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("key material", prompt.Question, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]

    public void Retention_disable_omits_days_so_the_server_preserves_the_prior_value()
    {

        RecordingHandler handler = new(_ => ErrorResponse());

        _ = RunCommand(
            handler,
            ["--yes", "data", "retention", "set", "archived-sessions", "disabled"]);

        RecordedRequest request = Assert.Single(handler.Requests);

        using JsonDocument json = JsonDocument.Parse(request.Body);

        Assert.Equal(
            "archived-sessions",
            json.RootElement.GetProperty("dataClass").GetString());

        Assert.False(json.RootElement.GetProperty("enabled").GetBoolean());

        Assert.False(json.RootElement.TryGetProperty("days", out _));

    }

    [Theory]

    [InlineData("PUT", "/api/data/retention", "--yes data retention set archived-sessions 30")]

    [InlineData("DELETE", "/api/data/sessions/11111111-1111-1111-1111-111111111111", "--yes data delete-session 11111111-1111-1111-1111-111111111111")]

    [InlineData("DELETE", "/api/data/attachments/22222222-2222-2222-2222-222222222222", "--yes data delete-attachment 22222222-2222-2222-2222-222222222222")]

    [InlineData("POST", "/api/data/memory/reset", "--yes data reset-memory --scope entry")]

    [InlineData("POST", "/api/data/factory-reset", "--yes data factory-reset")]

    public void Confirmed_data_mutations_route_through_authenticated_api(
        string method,
        string path,
        string commandLine)
    {

        RecordingHandler handler = new(_ => ErrorResponse());

        _ = RunCommand(handler, Split(commandLine));

        RecordedRequest request = Assert.Single(handler.Requests);

        Assert.Equal(new HttpMethod(method), request.Method);

        Assert.Equal(path, request.Path);

        if (path == "/api/data/retention")
        {

            Assert.Contains(
                "archived-sessions",
                request.Body,
                StringComparison.OrdinalIgnoreCase);

            Assert.Contains("\"enabled\":true", request.Body, StringComparison.Ordinal);

            Assert.Contains("\"days\":30", request.Body, StringComparison.Ordinal);

        }

        if (path == "/api/data/memory/reset")
        {

            Assert.Contains("entry", request.Body, StringComparison.OrdinalIgnoreCase);

        }

        if (path == "/api/data/factory-reset")
        {

            Assert.Contains(
                "factory-reset",
                request.Body,
                StringComparison.Ordinal);

        }

    }

    [Theory]

    [InlineData("data retention set archived-sessions 30")]

    [InlineData("data delete-session 11111111-1111-1111-1111-111111111111")]

    [InlineData("data delete-attachment 22222222-2222-2222-2222-222222222222")]

    [InlineData("data reset-memory --scope entry")]

    [InlineData("data factory-reset")]

    public void Data_mutations_require_confirmation_before_http(
        string commandLine)
    {

        RecordingHandler handler = new(_ => ErrorResponse());

        CliTestResult result = RunCommand(handler, Split(commandLine));

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains("--yes", result.Error, StringComparison.Ordinal);

    }

    [Fact]

    public void Reset_memory_requires_an_explicit_scope_before_confirmation_or_http()
    {

        RecordingHandler handler = new(_ => ErrorResponse());

        CliTestResult result = RunCommand(
            handler,
            ["--yes", "data", "reset-memory"]);

        Assert.NotEqual(0, result.ExitCode);

        Assert.Empty(handler.Requests);

        Assert.Contains(
            "scope",
            result.Output + result.Error,
            StringComparison.OrdinalIgnoreCase);

    }

    [Theory]

    [InlineData("--yes data reset-memory --scope 0")]

    [InlineData("--yes data reset-memory --scope 999")]

    [InlineData("--yes data retention set 0 30")]

    public void Destructive_named_choices_reject_numeric_spellings_before_http(
        string commandLine)
    {

        RecordingHandler handler = new(_ => ErrorResponse());

        CliTestResult result = RunCommand(
            handler,
            Split(commandLine));

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Empty(handler.Requests);

    }

    [Theory]

    [InlineData("active-sessions", RetentionDataClass.ActiveSessions)]

    [InlineData("archived_sessions", RetentionDataClass.ArchivedSessions)]

    [InlineData("attachments", RetentionDataClass.AttachmentVersions)]

    [InlineData("batch input files", RetentionDataClass.BatchInputFiles)]

    [InlineData("workspace-indexes", RetentionDataClass.WorkspaceChunks)]

    [InlineData("accounting", RetentionDataClass.InferenceRuns)]

    [InlineData("daemon-history", RetentionDataClass.DaemonExecutions)]

    public void Retention_class_parser_preserves_documented_named_aliases(
        string value,
        RetentionDataClass expected)
    {

        Assert.True(DataRetentionDataClassParser.TryParse(value, out RetentionDataClass parsed));

        Assert.Equal(expected, parsed);

    }

    private static string[] Split(string commandLine) =>
        commandLine.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static CliTestResult RunCommand(
        RecordingHandler handler,
        string[] args,
        IConfirmationPrompt? confirmationPrompt = null)
    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(
            services,
            new ConfigurationManager());

        services.RemoveAll<IHttpClientFactory>();

        services.AddSingleton<IHttpClientFactory>(
            new FakeHttpClientFactory(handler));

        services.RemoveAll<ISecretStore>();

        services.AddSingleton<ISecretStore>(
            new FakeSecretStore("test-key"));

        if (confirmationPrompt is not null)
        {

            services.RemoveAll<IConfirmationPrompt>();

            services.AddSingleton(confirmationPrompt);

        }

        return CliTestHarness.Run(services, args);

    }

    private static HttpResponseMessage SuccessResponse<T>(
        T value,
        JsonTypeInfo<ApiResponse<T>> typeInfo)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            ApiResponse<T>.FromResult(Result<T>.Success(value)),
            typeInfo);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {

            Content = new ByteArrayContent(json),

        };

    }

    private static DataRetentionStatus CreateStatus() =>
        new(
            new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero),
            [
                new DataRetentionStatusItem(
                    RetentionDataClass.ArchivedSessions,
                    Rows: 12,
                    Files: 3,
                    EstimatedBytes: 4_096,
                    PolicyEnabled: true,
                    RetentionDays: 180,
                    Store: "grimoire",
                    Provenance: "configured-root"),
            ],
            Rows: 12,
            Files: 3,
            EstimatedBytes: 4_096,
            PreservedOutsideSelectedRoot: ["external-backups"]);

    private static RetentionSettings CreateSettings() =>
        new()
        {

            AutomaticSweepsEnabled = true,

            ArchivedSessions = new RetentionRuleSettings
            {

                Enabled = true,

                Days = 180,

            },

        };

    private static DataRetentionPlan CreatePlan() =>
        new(
            "plan-exact-43",
            new DataRetentionRequest(DataRetentionOperation.Prune),
            new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero),
            [
                new DataRetentionPlanItem(
                    RetentionDataClass.ArchivedSessions,
                    Rows: 12,
                    Files: 3,
                    EstimatedBytes: 4_096,
                    DerivedRecords: 7),
            ],
            [],
            [],
            Rows: 12,
            Files: 3,
            EstimatedBytes: 4_096,
            DerivedRecords: 7,
            CandidateIds: ["session-43"],
            RequiresConfirmation: true);

    private static DataRetentionApplyResult CreateApplyResult(string planId) =>
        new(
            Guid.Parse("43434343-4343-4343-4343-434343434343"),
            planId,
            RowsDeleted: 12,
            FilesDeleted: 3,
            EstimatedBytesDeleted: 4_096,
            DerivedRecordsDeleted: 7,
            Reconciled: true,
            Blockers: [],
            Conflicts: []);

    private static HttpResponseMessage ErrorResponse() =>
        new(HttpStatusCode.Conflict)
        {

            Content = new StringContent(
                """
                {"data":null,"isSuccess":false,"error":{"code":"Test.Stop","message":"Test response."}}
                """,
                Encoding.UTF8,
                "application/json"),

        };

    private sealed class FakeSecretStore(string apiKey) : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() =>
            Task.FromResult<string?>(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) =>
            Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

    private sealed class FakeHttpClientFactory(
        RecordingHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {

                BaseAddress = new Uri("http://localhost:5001/"),

            };

    }

    private sealed class RecordingConfirmationPrompt(
        bool confirmed) : IConfirmationPrompt
    {

        public string Question { get; private set; } = string.Empty;

        public Task<bool> PromptForConfirmationAsync(
            string question,
            CancellationToken cancellationToken)
        {

            Question = question;

            return Task.FromResult(confirmed);

        }

    }

    private sealed class RecordingHandler(
        Func<RecordedRequest, HttpResponseMessage>? responder = null)
        : HttpMessageHandler
    {

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            string body = request.Content is null
                ? string.Empty
                : await request.Content
                    .ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);

            RecordedRequest recorded = new(
                request.Method,
                request.RequestUri!.AbsolutePath,
                body);

            Requests.Add(recorded);

            return responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(recorded);

        }

    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string Body);

}
