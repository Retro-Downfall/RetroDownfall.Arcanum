using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.AspNetCore.Http;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Serialization;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Configuration;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]

public sealed class DataRetentionEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public DataRetentionEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableTheory]

    [InlineData("GET", "/api/data/status")]

    [InlineData("GET", "/api/data/retention")]

    [InlineData("PUT", "/api/data/retention")]

    [InlineData("POST", "/api/data/prune/plan")]

    [InlineData("POST", "/api/data/prune")]

    [InlineData("DELETE", "/api/data/sessions/11111111-1111-1111-1111-111111111111")]

    [InlineData("DELETE", "/api/data/attachments/22222222-2222-2222-2222-222222222222")]

    [InlineData("POST", "/api/data/memory/reset")]

    [InlineData("POST", "/api/data/factory-reset")]

    [InlineData("POST", "/api/data/factory-reset/plan")]

    public async Task Data_lifecycle_routes_require_api_key(
        string method,
        string path)
    {

        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        using HttpRequestMessage request = new(new HttpMethod(method), path);

        if (method is "POST" or "PUT")
        {

            string json = path switch
            {

                "/api/data/retention" =>
                    "{\"dataClass\":\"archived-sessions\",\"enabled\":true,\"days\":30}",

                "/api/data/prune/plan" =>
                    "{\"operation\":\"Prune\"}",

                "/api/data/prune" =>
                    "{\"request\":{\"operation\":\"Prune\"}}",

                "/api/data/memory/reset" =>
                    "{\"scope\":\"Entry\"}",

                "/api/data/factory-reset" =>
                    "{\"confirmation\":\"factory-reset\"}",

                "/api/data/factory-reset/plan" =>
                    "{\"scope\":\"Global\"}",

                _ => "{}",

            };

            request.Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json");

        }

        HttpResponseMessage response = await _factory
            .CreateClient()
            .SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    [SkippableFact]

    public async Task Authenticated_status_plan_and_apply_use_the_data_lifecycle_service()
    {

        RequireSqlCipher();

        FakeDataRetentionService service = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(service);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage statusResponse = await client.GetAsync(
            "/api/data/status");

        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        ApiResponse<DataRetentionStatus> status = await ReadAsync(
            statusResponse,
            ArcanumJsonContext.Default.ApiResponseDataRetentionStatus);

        Assert.True(status.IsSuccess);

        Assert.Equal(service.Status.Rows, status.Data?.Rows);

        Assert.Equal(service.Status.Files, status.Data?.Files);

        Assert.Equal(
            service.Status.EstimatedBytes,
            status.Data?.EstimatedBytes);

        DataRetentionRequest request = new(DataRetentionOperation.Prune);

        HttpResponseMessage planResponse = await client.PostAsync(
            "/api/data/prune/plan",
            JsonContent(
                request,
                ArcanumJsonContext.Default.DataRetentionRequest));

        Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);

        ApiResponse<DataRetentionPlan> plan = await ReadAsync(
            planResponse,
            ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

        Assert.True(plan.IsSuccess);

        Assert.Equal(service.Plan.PlanId, plan.Data?.PlanId);

        Assert.Equal(service.Plan.Rows, plan.Data?.Rows);

        Assert.Equal(service.Plan.Files, plan.Data?.Files);

        Assert.Equal(request, service.LastPlanRequest);

        DataRetentionApplyRequest applyRequest = new(
            request,
            service.Plan.PlanId);

        HttpResponseMessage applyResponse = await client.PostAsync(
            "/api/data/prune",
            JsonContent(
                applyRequest,
                ArcanumJsonContext.Default.DataRetentionApplyRequest));

        Assert.Equal(HttpStatusCode.OK, applyResponse.StatusCode);

        ApiResponse<DataRetentionApplyResult> applied = await ReadAsync(
            applyResponse,
            ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

        Assert.True(applied.IsSuccess);

        Assert.Equal(service.Applied.OperationId, applied.Data?.OperationId);

        Assert.Equal(service.Applied.PlanId, applied.Data?.PlanId);

        Assert.Equal(
            service.Applied.RowsDeleted,
            applied.Data?.RowsDeleted);

        Assert.Equal(applyRequest, service.LastApplyRequest);

    }

    [SkippableFact]

    public async Task Factory_reset_requires_the_exact_confirmation_before_apply()
    {

        RequireSqlCipher();

        FakeDataRetentionService service = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(service);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage rejected = await client.PostAsync(
            "/api/data/factory-reset",
            JsonContent(
                new FactoryResetRequest("yes"),
                ArcanumJsonContext.Default.FactoryResetRequest));

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        ApiResponse<DataRetentionApplyResult> error = await ReadAsync(
            rejected,
            ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

        Assert.False(error.IsSuccess);

        Assert.Equal(ErrorCodes.Data.ConfirmationRequired, error.Error?.Code);

        Assert.Equal(0, service.ApplyCallCount);

        HttpResponseMessage accepted = await client.PostAsync(
            "/api/data/factory-reset",
            JsonContent(
                new FactoryResetRequest("factory-reset"),
                ArcanumJsonContext.Default.FactoryResetRequest));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        Assert.Equal(1, service.ApplyCallCount);

        Assert.Equal(
            DataRetentionOperation.FactoryReset,
            service.LastApplyRequest?.Request.Operation);

    }

    [SkippableTheory]

    [InlineData(InstallationResetDataScope.Global, DataRetentionOperation.FactoryReset)]

    [InlineData(InstallationResetDataScope.Workspace, DataRetentionOperation.ResetWorkspace)]

    public async Task Factory_reset_planning_maps_the_requested_data_scope(
        InstallationResetDataScope scope,
        DataRetentionOperation expectedOperation)
    {

        RequireSqlCipher();

        FakeDataRetentionService service = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(service);

        using HttpClient client = CreateLoopbackAuthenticatedClient(factory);

        DataRetentionWorkspaceBinding? workspace = scope == InstallationResetDataScope.Workspace
            ? new DataRetentionWorkspaceBinding(
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                "/workspace")
            : null;

        InstallationResetDataPlanRequest request = new(scope, workspace);

        HttpResponseMessage response = await client.PostAsync(
            "/api/data/factory-reset/plan",
            JsonContent(
                request,
                ArcanumJsonContext.Default.InstallationResetDataPlanRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<DataRetentionPlan> body = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseDataRetentionPlan);

        Assert.True(body.IsSuccess);

        Assert.Equal(expectedOperation, service.LastPlanRequest?.Operation);

        Assert.Equal(workspace, service.LastPlanRequest?.Workspace);

    }

    [SkippableTheory]

    [InlineData("null")]

    [InlineData("{\"scope\":\"Workspace\"}")]

    [InlineData("{\"scope\":\"Global\",\"workspace\":{\"campaignId\":\"44444444-4444-4444-4444-444444444444\",\"workspaceRoot\":\"/workspace\"}}")]

    public async Task Factory_reset_planning_rejects_invalid_scope_bindings_before_planning(
        string json)
    {

        RequireSqlCipher();

        FakeDataRetentionService service = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(service);

        using HttpClient client = CreateLoopbackAuthenticatedClient(factory);

        using StringContent content = new(
            json,
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = await client.PostAsync(
            "/api/data/factory-reset/plan",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Null(service.LastPlanRequest);

    }

    [SkippableFact]

    public async Task Factory_reset_planning_rejects_a_non_loopback_peer_before_planning()
    {

        RequireSqlCipher();

        FakeDataRetentionService service = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(service);

        _ = factory.CreateClient();

        byte[] body = Encoding.UTF8.GetBytes("{\"scope\":\"Global\"}");

        HttpContext context = await factory.Server.SendAsync(requestContext =>
        {

            requestContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");

            requestContext.Request.Method = HttpMethod.Post.Method;

            requestContext.Request.Path = "/api/data/factory-reset/plan";

            requestContext.Request.Headers[ArcanumApiHeaders.ApiKey] =
                ArcanumWebApplicationFactory.TestApiKey;

            requestContext.Request.ContentType = "application/json";

            requestContext.Request.ContentLength = body.Length;

            requestContext.Request.Body = new MemoryStream(body);

        });

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

        Assert.Null(service.LastPlanRequest);

    }

    [SkippableTheory]

    [InlineData("/api/data/retention", "{\"dataClass\":\"archived-sessions\",\"days\":30}")]

    [InlineData("/api/data/retention", "{\"dataClass\":\"archived-sessions\",\"enabled\":true}")]

    [InlineData("/api/data/retention", "{\"enabled\":true,\"days\":30}")]

    [InlineData("/api/data/memory/reset", "{}")]

    [InlineData("/api/data/prune", "{}")]

    public async Task Destructive_mutation_payloads_reject_omitted_fields(
        string path,
        string json)
    {

        RequireSqlCipher();

        FakeDataRetentionService service = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(service);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using StringContent content = new(
            json,
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = path.EndsWith(
                "/retention",
                StringComparison.Ordinal)
            ? await client.PutAsync(path, content)
            : await client.PostAsync(path, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(0, service.ApplyCallCount);

    }

    [Fact]

    public void Data_retention_json_contracts_require_every_mutation_selector()
    {

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{}",
            ArcanumJsonContext.Default.DataRetentionRequest));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{}",
            ArcanumJsonContext.Default.DataRetentionApplyRequest));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{}",
            ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{}",
            ArcanumJsonContext.Default.MemoryResetRequest));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{}",
            ArcanumJsonContext.Default.FactoryResetRequest));

    }

    [SkippableTheory]

    [InlineData("/api/data/prune", "{\"request\":{}}")]

    [InlineData("/api/data/prune", "{\"request\":{\"operation\":0}}")]

    [InlineData("/api/data/memory/reset", "{\"scope\":0}")]

    public async Task Destructive_mutation_payloads_reject_missing_or_numeric_choices_before_apply(
        string path,
        string json)
    {

        RequireSqlCipher();

        FakeDataRetentionService service = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(service);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using StringContent content = new(
            json,
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = await client.PostAsync(path, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(0, service.ApplyCallCount);

    }

    [SkippableFact]

    public async Task Retention_update_rejects_numeric_data_class_names()
    {

        RequireSqlCipher();

        FakeDataRetentionService service = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(service);

        using HttpClient client = factory.CreateAuthenticatedClient();

        using StringContent content = new(
            "{\"dataClass\":\"0\",\"enabled\":true,\"days\":30}",
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = await client.PutAsync(
            "/api/data/retention",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    [SkippableFact]

    public async Task Apply_failures_map_to_coherent_http_status_codes()
    {

        RequireSqlCipher();

        FakeDataRetentionService service = new()
        {

            ApplyHandler = static request =>
            {

                string code = request.ExpectedPlanId!;

                return Result<DataRetentionApplyResult>.Failure(
                    new Error(code, "Expected test failure."));

            },

        };

        await using ArcanumWebApplicationFactory factory = CreateFactory(service);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage malformed = await client.PostAsync(
            "/api/data/prune",
            new StringContent(
                """{"request":null}""",
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        ApiResponse<DataRetentionApplyResult> malformedBody = await ReadAsync(
            malformed,
            ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

        Assert.Equal(
            ErrorCodes.Data.InvalidRequest,
            malformedBody.Error?.Code);

        Assert.Equal(0, service.ApplyCallCount);

        (string Code, HttpStatusCode Status)[] cases =
        [
            (ErrorCodes.Data.PlanChanged, HttpStatusCode.Conflict),
            (ErrorCodes.Data.Blocked, HttpStatusCode.Conflict),
            (ErrorCodes.Data.Conflict, HttpStatusCode.Conflict),
            (ErrorCodes.Data.InvalidRequest, HttpStatusCode.BadRequest),
            (ErrorCodes.Data.ReconciliationFailed, HttpStatusCode.InternalServerError),
        ];

        foreach ((string code, HttpStatusCode expectedStatus) in cases)
        {

            DataRetentionApplyRequest request = new(
                new DataRetentionRequest(DataRetentionOperation.Prune),
                code);

            HttpResponseMessage response = await client.PostAsync(
                "/api/data/prune",
                JsonContent(
                    request,
                    ArcanumJsonContext.Default.DataRetentionApplyRequest));

            Assert.Equal(expectedStatus, response.StatusCode);

            ApiResponse<DataRetentionApplyResult> body = await ReadAsync(
                response,
                ArcanumJsonContext.Default.ApiResponseDataRetentionApplyResult);

            Assert.False(body.IsSuccess);

            Assert.Equal(code, body.Error?.Code);

        }

    }

    [SkippableFact]

    public async Task Targeted_routes_build_explicit_delete_and_memory_requests()
    {

        RequireSqlCipher();

        FakeDataRetentionService service = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(service);

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = Guid.Parse(
            "11111111-1111-1111-1111-111111111111");

        HttpResponseMessage sessionResponse = await client.DeleteAsync(
            $"/api/data/sessions/{sessionId:D}");

        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);

        Assert.Equal(
            new DataRetentionRequest(
                DataRetentionOperation.DeleteSession,
                sessionId),
            service.LastApplyRequest?.Request);

        Guid attachmentId = Guid.Parse(
            "22222222-2222-2222-2222-222222222222");

        HttpResponseMessage attachmentResponse = await client.DeleteAsync(
            $"/api/data/attachments/{attachmentId:D}");

        Assert.Equal(HttpStatusCode.OK, attachmentResponse.StatusCode);

        Assert.Equal(
            new DataRetentionRequest(
                DataRetentionOperation.DeleteAttachment,
                attachmentId),
            service.LastApplyRequest?.Request);

        HttpResponseMessage memoryResponse = await client.PostAsync(
            "/api/data/memory/reset",
            JsonContent(
                new MemoryResetRequest(MemoryResetScope.Entry),
                ArcanumJsonContext.Default.MemoryResetRequest));

        Assert.Equal(HttpStatusCode.OK, memoryResponse.StatusCode);

        Assert.Equal(
            new DataRetentionRequest(
                DataRetentionOperation.ResetMemory,
                TargetId: null,
                MemoryScope: MemoryResetScope.Entry),
            service.LastApplyRequest?.Request);

    }

    [SkippableFact]

    public async Task Sequential_retention_updates_accumulate_for_get_and_planning()
    {

        RequireSqlCipher();

        FakeDataRetentionService service = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(service);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage initialGet = await client.GetAsync(
            "/api/data/retention");

        Assert.Equal(HttpStatusCode.OK, initialGet.StatusCode);

        IOptionsMonitor<ArcanumSettings> options = factory.Services
            .GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

        ConfigurationWriter writer = factory.Services
            .GetRequiredService<ConfigurationWriter>();

        Result externalWrite = await writer.WriteAsync(
            options.CurrentValue with
            {

                DefaultModel = "preserve-after-retention-update",

                Retention = options.CurrentValue.Retention with
                {

                    ActiveSessions = new RetentionRuleSettings
                    {

                        Enabled = true,

                        Days = 60,

                    },

                },

            },
            CancellationToken.None);

        Assert.True(externalWrite.IsSuccess, externalWrite.Error.Message);

        HttpResponseMessage refreshedGet = await client.GetAsync(
            "/api/data/retention");

        ApiResponse<RetentionSettings> refreshed = await ReadAsync(
            refreshedGet,
            ArcanumJsonContext.Default.ApiResponseRetentionSettings);

        Assert.True(refreshed.Data?.ActiveSessions.Enabled);

        Assert.Equal(60, refreshed.Data?.ActiveSessions.Days);

        RetentionRuleUpdateRequest request = new(
            "archived-sessions",
            Enabled: true,
            Days: 9_999);

        HttpResponseMessage response = await client.PutAsync(
            "/api/data/retention",
            JsonContent(
                request,
                ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<RetentionSettings> first = await ReadAsync(
            response,
            ArcanumJsonContext.Default.ApiResponseRetentionSettings);

        Assert.True(first.IsSuccess);

        Assert.True(first.Data?.ArchivedSessions.Enabled);

        Assert.Equal(3_650, first.Data?.ArchivedSessions.Days);

        RetentionRuleUpdateRequest secondRequest = new(
            "uploaded-files",
            Enabled: true,
            Days: 14);

        HttpResponseMessage secondResponse = await client.PutAsync(
            "/api/data/retention",
            JsonContent(
                secondRequest,
                ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        HttpResponseMessage getResponse = await client.GetAsync(
            "/api/data/retention");

        ApiResponse<RetentionSettings> current = await ReadAsync(
            getResponse,
            ArcanumJsonContext.Default.ApiResponseRetentionSettings);

        Assert.True(current.Data?.ArchivedSessions.Enabled);

        Assert.True(current.Data?.ActiveSessions.Enabled);

        Assert.Equal(60, current.Data?.ActiveSessions.Days);

        Assert.Equal(3_650, current.Data?.ArchivedSessions.Days);

        Assert.True(current.Data?.UploadedFiles.Enabled);

        Assert.Equal(14, current.Data?.UploadedFiles.Days);

        HttpResponseMessage plan = await client.PostAsync(
            "/api/data/prune/plan",
            JsonContent(
                new DataRetentionRequest(DataRetentionOperation.Prune),
                ArcanumJsonContext.Default.DataRetentionRequest));

        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);

        Assert.True(service.ObservedRetention?.ArchivedSessions.Enabled);

        Assert.True(service.ObservedRetention?.ActiveSessions.Enabled);

        Assert.Equal(60, service.ObservedRetention?.ActiveSessions.Days);

        Assert.Equal(3_650, service.ObservedRetention?.ArchivedSessions.Days);

        Assert.True(service.ObservedRetention?.UploadedFiles.Enabled);

        Assert.Equal(14, service.ObservedRetention?.UploadedFiles.Days);

        string persistedJson = await File.ReadAllTextAsync(
            Path.Combine(
                ArcanumPaths.GrimoireDirectory,
                "arcanum.json"));

        ArcanumConfigurationFile? persisted = JsonSerializer.Deserialize(
            persistedJson,
            ConfigurationJsonContext.Default.ArcanumConfigurationFile);

        Assert.Equal(
            "preserve-after-retention-update",
            persisted?.Arcanum.DefaultModel);

        HttpResponseMessage invalid = await client.PutAsync(
            "/api/data/retention",
            JsonContent(
                request with { DataClass = "not-a-retention-class" },
                ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

    }

    [SkippableFact]

    public async Task Disabling_a_retention_rule_without_days_preserves_its_prior_days()
    {

        RequireSqlCipher();

        FakeDataRetentionService service = new();

        await using ArcanumWebApplicationFactory factory = CreateFactory(service);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage enabled = await client.PutAsync(
            "/api/data/retention",
            JsonContent(
                new RetentionRuleUpdateRequest(
                    "archived-sessions",
                    Enabled: true,
                    Days: 91),
                ArcanumJsonContext.Default.RetentionRuleUpdateRequest));

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);

        using StringContent disableRequest = new(
            "{\"dataClass\":\"archived-sessions\",\"enabled\":false}",
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage disabled = await client.PutAsync(
            "/api/data/retention",
            disableRequest);

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        ApiResponse<RetentionSettings> response = await ReadAsync(
            disabled,
            ArcanumJsonContext.Default.ApiResponseRetentionSettings);

        Assert.False(response.Data?.ArchivedSessions.Enabled);

        Assert.Equal(91, response.Data?.ArchivedSessions.Days);

    }

    private static ArcanumWebApplicationFactory CreateFactory(
        FakeDataRetentionService service)
    {

        ArcanumWebApplicationFactory factory = new();

        factory.ServiceOverrides = services =>
        {

            services.RemoveAll<IDataRetentionService>();

            services.AddSingleton<IDataRetentionService>(serviceProvider =>
            {

                service.RetentionSnapshot = () => serviceProvider
                    .GetRequiredService<IDataRetentionPolicyStore>()
                    .Current;

                return service;

            });

        };

        return factory;

    }

    private static HttpClient CreateLoopbackAuthenticatedClient(
        ArcanumWebApplicationFactory factory)
    {

        HttpClient client = new(
            factory.Server.CreateHandler(context =>
                context.Connection.RemoteIpAddress = IPAddress.Loopback))
        {

            BaseAddress = new Uri("http://localhost"),

        };

        client.DefaultRequestHeaders.Add(
            ArcanumApiHeaders.ApiKey,
            ArcanumWebApplicationFactory.TestApiKey);

        return client;

    }

    private static StringContent JsonContent<T>(
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        new(
            JsonSerializer.Serialize(value, typeInfo),
            Encoding.UTF8,
            "application/json");

    private static async Task<ApiResponse<T>> ReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo)
    {

        string json = await response.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize(json, typeInfo)!;

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

    private sealed class FakeDataRetentionService : IDataRetentionService
    {

        public DataRetentionStatus Status { get; } = new(
            new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero),
            [],
            Rows: 12,
            Files: 3,
            EstimatedBytes: 4_096,
            PreservedOutsideSelectedRoot: ["backups"]);

        public DataRetentionPlan Plan { get; } = new(
            "plan-test",
            new DataRetentionRequest(DataRetentionOperation.Prune),
            new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero),
            [],
            [],
            [],
            Rows: 2,
            Files: 1,
            EstimatedBytes: 512,
            DerivedRecords: 3,
            CandidateIds: ["candidate"],
            RequiresConfirmation: true);

        public DataRetentionApplyResult Applied { get; } = new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "plan-test",
            RowsDeleted: 2,
            FilesDeleted: 1,
            EstimatedBytesDeleted: 512,
            DerivedRecordsDeleted: 3,
            Reconciled: true,
            Blockers: [],
            Conflicts: []);

        public DataRetentionRequest? LastPlanRequest { get; private set; }

        public DataRetentionApplyRequest? LastApplyRequest { get; private set; }

        public int ApplyCallCount { get; private set; }

        public Func<RetentionSettings>? RetentionSnapshot { get; set; }

        public RetentionSettings? ObservedRetention { get; private set; }

        public Func<DataRetentionApplyRequest, Result<DataRetentionApplyResult>>? ApplyHandler
        {

            get;

            init;

        }

        public Task<DataRetentionStatus> GetStatusAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task<DataRetentionPlan> PlanAsync(
            DataRetentionRequest request,
            CancellationToken cancellationToken = default)
        {

            LastPlanRequest = request;

            ObservedRetention = RetentionSnapshot?.Invoke();

            return Task.FromResult(Plan with { Request = request });

        }

        public Task<Result<DataRetentionApplyResult>> ApplyAsync(
            DataRetentionApplyRequest request,
            CancellationToken cancellationToken = default)
        {

            ApplyCallCount++;

            LastApplyRequest = request;

            return Task.FromResult(
                ApplyHandler?.Invoke(request)
                ?? Result<DataRetentionApplyResult>.Success(Applied));

        }

    }

}
