using System.Net;

using System.Text;

using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Cli.UX;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

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

    public void Confirmed_data_mutations_route_through_authenticated_api(
        string method,
        string path,
        string commandLine)
    {

        DataRetentionPlan resetPlan = CreatePlan() with
        {
            Request = new DataRetentionRequest(
                DataRetentionOperation.ResetMemory,
                MemoryScope: MemoryResetScope.Entry),
        };

        RecordingHandler handler = new(request =>
            request.Path == "/api/data/memory/reset/plan"
                ? SuccessResponse(
                    resetPlan,
                    ArcanumJsonContext.Default.ApiResponseDataRetentionPlan)
                : ErrorResponse());

        _ = RunCommand(handler, Split(commandLine));

        // Exactly one *mutating* request. `data reset-memory` also reads the plan first, because the
        // destructive-disclosure contract requires an operator to see the receipt-backed
        // possible-attempt count before they answer, and that count can only come from the server
        // (§10.20.2). A read-only preview is not a mutation; what matters here is that there is no
        // second write and that the write lands where it is supposed to.
        RecordedRequest request = Assert.Single(
            handler.Requests,
            static recorded => recorded.Path != "/api/data/memory/reset/plan");

        Assert.Equal(new HttpMethod(method), request.Method);

        Assert.Equal(path, request.Path);

        Assert.All(
            handler.Requests.Where(static recorded => recorded.Path == "/api/data/memory/reset/plan"),
            static preview => Assert.Equal(HttpMethod.Post, preview.Method));

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

            RecordedRequest preview = Assert.Single(
                handler.Requests,
                static recorded => recorded.Path == "/api/data/memory/reset/plan");

            MemoryResetRequest? previewBody = JsonSerializer.Deserialize(
                preview.Body,
                ArcanumJsonContext.Default.MemoryResetRequest);

            Assert.Equal(MemoryResetScope.Entry, previewBody?.Scope);

        }

    }

    [Theory]

    [InlineData("data retention set archived-sessions 30")]

    [InlineData("data delete-session 11111111-1111-1111-1111-111111111111")]

    [InlineData("data delete-attachment 22222222-2222-2222-2222-222222222222")]

    [InlineData("data reset-memory --scope entry")]

    /// <remarks>
    /// Narrowed from "no HTTP at all" to "no mutating HTTP" when issue #117 added the destructive
    /// disclosure. The property that protects an operator is that nothing is *changed* before they
    /// answer; reading the plan is how the count they are being asked to weigh is obtained at all, and
    /// the restore surface has done exactly this since issue #113. The read-only preview is asserted
    /// positively below rather than merely tolerated, so a mutation slipping into the pre-confirmation
    /// window still fails here.
    /// </remarks>
    public void Data_mutations_require_confirmation_before_http(
        string commandLine)
    {

        bool resetMemory = commandLine.Contains(
            "reset-memory",
            StringComparison.Ordinal);

        DataRetentionPlan resetPlan = CreatePlan() with
        {
            Request = new DataRetentionRequest(
                DataRetentionOperation.ResetMemory,
                MemoryScope: MemoryResetScope.Entry),
        };

        RecordingHandler handler = new(request => resetMemory
            && request.Path == "/api/data/memory/reset/plan"
                ? SuccessResponse(
                    resetPlan,
                    ArcanumJsonContext.Default.ApiResponseDataRetentionPlan)
                : ErrorResponse());

        CliTestResult result = RunCommand(handler, Split(commandLine));

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        string expectedPreviewPath = resetMemory
            ? "/api/data/memory/reset/plan"
            : "/api/data/prune/plan";

        Assert.All(
            handler.Requests,
            request => Assert.Equal(expectedPreviewPath, request.Path));

        Assert.Contains("--yes", result.Error, StringComparison.Ordinal);

    }

    [Theory]

    [InlineData(CovenantDisclosureCountKind.Exact, "exactly 2 physical attempts")]

    [InlineData(CovenantDisclosureCountKind.LowerBound, "at least 2 physical attempts")]

    public void Reset_memory_discloses_the_server_inventory_before_a_decline(
        CovenantDisclosureCountKind countKind,
        string expectedCount)
    {

        DataRetentionPlan plan = CreatePlan() with
        {
            Request = new DataRetentionRequest(
                DataRetentionOperation.ResetMemory,
                MemoryScope: MemoryResetScope.Covenant),
            Covenant = new DataRetentionCovenantInventory(
                Rows: 4,
                ManagedFiles: 3,
                LocalArtifacts: 2,
                AffectedSessions: 1,
                PossibleDisclosures: 2,
                DisclosureCountKind: countKind),
        };

        RecordingConfirmationPrompt prompt = new(confirmed: false);

        RecordingHandler handler = new(request => request.Path switch
        {
            "/api/data/memory/reset/plan" => SuccessResponse(
                plan,
                ArcanumJsonContext.Default.ApiResponseDataRetentionPlan),
            _ => ErrorResponse(),
        });

        CliTestResult result = RunCommand(
            handler,
            ["data", "reset-memory", "--scope", "covenant"],
            prompt);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        RecordedRequest preview = Assert.Single(handler.Requests);

        Assert.Equal("/api/data/memory/reset/plan", preview.Path);

        Assert.Contains("Reset the covenant memory scope?", prompt.Question, StringComparison.Ordinal);

        int disclosure = result.Error.IndexOf(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            StringComparison.Ordinal);

        int count = result.Error.IndexOf(expectedCount, StringComparison.Ordinal);

        int help = result.Error.IndexOf(
            "Retention guidance: README.md#covenant-provider-retention-and-deletion",
            StringComparison.Ordinal);

        int cancelled = result.Error.IndexOf("Memory reset cancelled.", StringComparison.Ordinal);

        Assert.True(disclosure >= 0);

        Assert.True(count > disclosure);

        Assert.True(help > count);

        Assert.True(cancelled > help);

    }

    [Fact]
    public void Json_reset_memory_writes_disclosure_to_diagnostics_and_one_apply_document()
    {

        DataRetentionPlan plan = CreatePlan() with
        {
            Request = new DataRetentionRequest(
                DataRetentionOperation.ResetMemory,
                MemoryScope: MemoryResetScope.Covenant),
            Covenant = new DataRetentionCovenantInventory(
                Rows: 4,
                ManagedFiles: 3,
                LocalArtifacts: 2,
                AffectedSessions: 1,
                PossibleDisclosures: 1,
                DisclosureCountKind: CovenantDisclosureCountKind.Exact),
        };

        DataRetentionApplyResult applied = CreateApplyResult(plan.PlanId);

        RecordingHandler handler = new(request => request.Path switch
        {
            "/api/data/memory/reset/plan" => SuccessResponse(
                plan,
                ArcanumJsonContext.Default.ApiResponseDataRetentionPlan),
            "/api/data/memory/reset" => SuccessResponse(
                applied,
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult),
            _ => ErrorResponse(),
        });

        CliTestResult result = RunCommand(
            handler,
            ["--json", "--yes", "data", "reset-memory", "--scope", "covenant"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.Contains(
            CovenantExternalRetentionDisclosure.DestructiveOperationText,
            result.Error,
            StringComparison.Ordinal);

        using JsonDocument document = JsonDocument.Parse(result.Output);

        Assert.Equal(plan.PlanId, document.RootElement.GetProperty("planId").GetString());

        Assert.Equal(
            applied.OperationId,
            document.RootElement.GetProperty("operationId").GetGuid());

        RecordedRequest applyRequest = Assert.Single(
            handler.Requests,
            static request => request.Path == "/api/data/memory/reset");

        using JsonDocument apply = JsonDocument.Parse(applyRequest.Body);

        Assert.Equal(
            plan.PlanId,
            apply.RootElement.GetProperty("expectedPlanId").GetString());

    }

    [Fact]
    public async Task Reset_memory_orders_preview_disclosure_targets_prompt_and_apply()
    {

        DataRetentionPlan plan = CreatePlan() with
        {
            Request = new DataRetentionRequest(
                DataRetentionOperation.ResetMemory,
                MemoryScope: MemoryResetScope.Covenant),
            Covenant = new DataRetentionCovenantInventory(
                Rows: 4,
                ManagedFiles: 3,
                LocalArtifacts: 2,
                AffectedSessions: 1,
                PossibleDisclosures: 1,
                DisclosureCountKind: CovenantDisclosureCountKind.Exact),
        };

        List<string> events = [];

        RecordingHandler handler = new(request => request.Path switch
        {
            "/api/data/memory/reset/plan" => SuccessResponse(
                plan,
                ArcanumJsonContext.Default.ApiResponseDataRetentionPlan),
            "/api/data/memory/reset" => SuccessResponse(
                CreateApplyResult(plan.PlanId),
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult),
            _ => ErrorResponse(),
        }, events);

        OrderedConsoleDispatcher dispatcher = new(events);

        DataRetentionCommands commands = new(
            new ArcanumApiClient(
                new FakeHttpClientFactory(handler),
                new FakeSecretStore("test-key")),
            dispatcher,
            new FixedInvocationContext(),
            new RecordingConfirmationPrompt(confirmed: true, events: events),
            new CovenantExternalRetentionDisclosureWriter(
                dispatcher,
                Options.Create(
                    new ArcanumSettings
                    {
                        Providers =
                        [
                            new ProviderSettings
                            {
                                Name = "Claude Code",
                                Type = AiProviderKind.ClaudeCodeCli,
                            },
                            new ProviderSettings
                            {
                                Name = "House gateway",
                                Type = AiProviderKind.OpenAICompatible,
                                Endpoint = "https://gateway.internal.example/v1",
                            },
                        ],
                    })));

        int exitCode = await commands.ResetMemory(
            "covenant",
            CancellationToken.None);

        Assert.Equal((int)CliExitCode.Success, exitCode);

        Assert.Equal(
            [
                "POST /api/data/memory/reset/plan",
                CovenantExternalRetentionDisclosure.DestructiveOperationText,
                "This installation's own receipts record exactly 1 physical attempt that could have carried protected content out of it. Nothing this reset does can revoke any of them.",
                "  Retention guidance (Claude Code): https://privacy.claude.com/en/collections/10672565-data-handling-retention",
                "  Retention guidance (House gateway): compendium:providers",
                "  Retention guidance: README.md#covenant-provider-retention-and-deletion",
                "<prompt>",
                "POST /api/data/memory/reset",
            ],
            events.Take(8));

        Assert.Contains(
            "\"expectedPlanId\":\"plan-exact-43\"",
            Assert.Single(
                handler.Requests,
                static request => request.Path == "/api/data/memory/reset").Body,
            StringComparison.Ordinal);

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
        bool confirmed,
        List<string>? events = null) : IConfirmationPrompt
    {

        public string Question { get; private set; } = string.Empty;

        public Task<bool> PromptForConfirmationAsync(
            string question,
            CancellationToken cancellationToken)
        {

            Question = question;

            events?.Add("<prompt>");

            return Task.FromResult(confirmed);

        }

    }

    private sealed class RecordingHandler(
        Func<RecordedRequest, HttpResponseMessage>? responder = null,
        List<string>? events = null)
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

            events?.Add($"{recorded.Method} {recorded.Path}");

            return responder is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : responder(recorded);

        }

    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string Body);

    private sealed class OrderedConsoleDispatcher(List<string> events) : IConsoleDispatcher
    {

        public void WritePayload(string value) => events.Add(value);

        public void WriteDiagnostic(string value) => events.Add(value);

        public void WriteVerbose(string value) => events.Add(value);

        public void WriteJson<T>(T value, JsonTypeInfo<T> typeInfo) =>
            events.Add("<json>");

        public void WriteJson(JsonElement value) => events.Add("<json>");

        public void BeginJsonStream() => events.Add("<json-stream>");

    }

    private sealed class FixedInvocationContext : ICliInvocationContext
    {

        public CliInvocationOptions Options { get; } = new(
            Json: false,
            Plain: true,
            Yes: false);

    }

}
