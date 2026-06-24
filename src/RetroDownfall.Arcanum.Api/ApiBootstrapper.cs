using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Middleware;
using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Api.Configuration;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.Tools;
using RetroDownfall.Arcanum.Api.ProvingGrounds;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Spells;
using RetroDownfall.Arcanum.Api.LlamaCpp;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Logging;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Configuration;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Daemons;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells;
using RetroDownfall.Arcanum.Infrastructure.LlamaCpp;
using RetroDownfall.Arcanum.Infrastructure.Logging;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Workspace;
using RetroDownfall.Arcanum.Infrastructure.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using Scalar.AspNetCore;

namespace RetroDownfall.Arcanum.Api;

public static class ApiBootstrapper
{
    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    private static readonly byte[] SseDataPrefix = "data: "u8.ToArray();

    private static readonly byte[] SseLineBreak = "\n\n"u8.ToArray();

    private static readonly byte[] SseDone = "data: [DONE]\n\n"u8.ToArray();

    private static readonly byte[] SseLogsConnected = "data: {\"connected\":true}\n\n"u8.ToArray();

    private const string ArcanumCorsPolicyName = "ArcanumCors";

    internal const string ArcanumRateLimiterPolicyName = "ArcanumRateLimit";

    private static readonly string[] DefaultCorsAllowedOrigins = new HostSettings().CorsAllowedOrigins;

    private static void RegisterRateLimiter(IServiceCollection services, IConfiguration configuration)
    {
        if (!IsRateLimitEnabled(configuration))
        {
            return;
        }

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(ArcanumRateLimiterPolicyName, ctx =>
            {
                IOptionsMonitor<ArcanumSettings> monitor = ctx.RequestServices
                    .GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

                HostRateLimitSettings rl = monitor.CurrentValue.Host?.RateLimit ?? new HostRateLimitSettings();

                int permitLimit = ArcanumSettingClamps.RateLimitPermitLimit(rl.PermitLimit);

                int windowSeconds = ArcanumSettingClamps.RateLimitWindowSeconds(rl.WindowSeconds);

                int queueLimit = ArcanumSettingClamps.RateLimitQueueLimit(rl.QueueLimit);

                string partitionKey = ResolveRateLimitPartitionKey(ctx);

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromSeconds(windowSeconds),
                        QueueLimit = queueLimit,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true,
                    });
            });
        });
    }

    private static string ResolveRateLimitPartitionKey(HttpContext context)
    {
        System.Net.IPAddress? ip = context.Connection.RemoteIpAddress;

        return "ip:" + (ip?.ToString() ?? "unknown");
    }

    private static bool ReadConfiguredListenAny(IConfiguration configuration)
    {
        string? configured = configuration["Arcanum:Host:ListenAny"];

        if (string.IsNullOrWhiteSpace(configured))
        {
            return new HostSettings().ListenAny;
        }

        return bool.TryParse(configured.Trim(), out bool parsed) && parsed;
    }

    private static bool IsRateLimitEnabled(IConfiguration configuration)
    {
        bool explicitlyEnabled = string.Equals(
            configuration["Arcanum:Host:RateLimit:Enabled"]?.Trim(),
            bool.TrueString,
            StringComparison.OrdinalIgnoreCase);

        return ArcanumEnvironment.IsRateLimitEnabled(explicitlyEnabled, ReadConfiguredListenAny(configuration));
    }

    private static string[] ReadCorsAllowedOriginsFromConfiguration(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection("Arcanum:Host:CorsAllowedOrigins");

        if (!section.Exists())
        {
            return DefaultCorsAllowedOrigins;
        }

        List<string> values = [];

        foreach (IConfigurationSection child in section.GetChildren())
        {
            string? value = child.Value;

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            values.Add(value.Trim().TrimEnd('/'));
        }

        return values.ToArray();
    }

    public static IServiceCollection AddArcanumApiServices(this IServiceCollection services, IConfiguration configuration)
    {

        services.AddExceptionHandler<ArcanumExceptionHandler>();

        services.AddProblemDetails();

        services.AddSingleton<ArcanumHealthChecker>();

        services.AddScoped<GrimoireStatsService>();

        services.AddSingleton<IHumanPromptRegistry, HumanPromptRegistry>();

        services.AddArcanumInfrastructure(configuration);

        services.AddArcanumDaemonServices(configuration);

        services.AddSingleton<ApiKeyEndpointFilter>();

        services.AddCors(options =>
        {
            options.AddPolicy(
                ArcanumCorsPolicyName,
                policy =>
                {
                    string[] origins = ReadCorsAllowedOriginsFromConfiguration(configuration);

                    bool wildcard = origins.Any(static o => string.Equals(o, "*", StringComparison.Ordinal));

                    bool listenAny = ArcanumEnvironment.IsHostAnyEnabled(
                        configuration.GetValue<bool?>("Arcanum:Host:ListenAny") ?? false);

                    if (wildcard && listenAny)
                    {

                        origins = DefaultCorsAllowedOrigins;

                        wildcard = false;

                    }

                    if (wildcard)
                    {
                        policy.AllowAnyOrigin();
                    }
                    else if (origins.Length == 0)
                    {
                        policy.WithOrigins(DefaultCorsAllowedOrigins);
                    }
                    else
                    {
                        policy.WithOrigins(origins);
                    }

                    policy.AllowAnyHeader();

                    policy.AllowAnyMethod();
                });
        });

        services.AddOpenApi();

        RegisterRateLimiter(services, configuration);

        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

        services.AddHttpClient(
            "OpenAiCompatibleProvider",
            static (sp, client) =>
            {
                _ = sp;

                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        services.AddSingleton<IChatClientFactory, ChatClientFactory>();

        services.AddSingleton<InferenceTokenizerResolver>();

        services.AddSingleton<ManaPreflight>();

        services.AddSingleton<IManaMeter, TheForge.ManaMeter>();

        services.AddSingleton<PromptRenderer>();

        services.AddScoped<IArcanumIntelligenceProvider, WizardIntelligenceProvider>();

        services.AddScoped<IProvingGroundsArbiter, ProvingGroundsArbiter>();

        services.AddScoped<ProvingGroundsRunner>();

        services.AddSingleton<SpellWorkspaceResolver>();

        return services;
    }

    /// <summary>
    /// Activates centralized exception handling for all Arcanum API hosts.
    /// </summary>
    public static void UseArcanumExceptionHandler(this WebApplication app)
    {

        app.UseExceptionHandler();

    }

    /// <summary>
    /// Applies the configurable Arcanum CORS policy from <c>Arcanum:Host:CorsAllowedOrigins</c>.
    /// Default is localhost loopback; use <c>["*"]</c> for permissive (browser-callable from any origin).
    /// </summary>
    public static void UseArcanumCors(this WebApplication app)
    {
        app.UseCors(ArcanumCorsPolicyName);
    }

    /// <summary>
    /// Activates the Arcanum rate-limiter middleware when rate limiting is effective
    /// (<c>Arcanum:Host:RateLimit:Enabled</c> or all-interfaces bind via ListenAny / <c>ARCANUM_HOST_ANY</c>);
    /// otherwise no-op (zero overhead).
    /// </summary>
    public static void UseArcanumRateLimiter(this WebApplication app)
    {
        if (IsRateLimitEnabled(app.Configuration))
        {
            app.UseRateLimiter();
        }
    }

    public static void MapArcanumEndpoints(this WebApplication app)
    {
        bool rateLimitEnabled = IsRateLimitEnabled(app.Configuration);

        RouteGroupBuilder openAiV1 = app.MapGroup("/v1").AddEndpointFilter<ApiKeyEndpointFilter>();

        if (rateLimitEnabled)
        {
            openAiV1 = openAiV1.RequireRateLimiting(ArcanumRateLimiterPolicyName);
        }

        openAiV1.MapOpenAiV1ChatCompletions();

        openAiV1.MapOpenAiV1Models();

        var apiGroup = app.MapGroup("/api").AddEndpointFilter<ApiKeyEndpointFilter>();

        if (rateLimitEnabled)
        {
            apiGroup = apiGroup.RequireRateLimiting(ArcanumRateLimiterPolicyName);
        }

        apiGroup.MapOpenApi();

        bool enableScalar = string.Equals(
            app.Configuration["Arcanum:Host:EnableScalarUi"]?.Trim(),
            bool.TrueString,
            StringComparison.OrdinalIgnoreCase);

        if (enableScalar)
        {
            RouteGroupBuilder scalarGroup = apiGroup
                .MapGroup(string.Empty)
                .AddEndpointFilter(static async (EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
                {
                    object? result = await next(context).ConfigureAwait(false);

                    HttpResponse response = context.HttpContext.Response;

                    if (!response.Headers.ContainsKey("Content-Security-Policy"))
                    {
                        response.Headers.Append(
                            "Content-Security-Policy",
                            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'");
                    }

                    if (!response.Headers.ContainsKey("X-Content-Type-Options"))
                    {
                        response.Headers.Append("X-Content-Type-Options", "nosniff");
                    }

                    return result;
                });

            scalarGroup.MapScalarApiReference();
        }

        apiGroup.MapGet("/health", async (ArcanumHealthChecker healthChecker, HttpContext httpContext, CancellationToken cancellationToken) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            HealthReportDto report = await healthChecker
                .BuildReportAsync(cancellationToken)
                .ConfigureAwait(false);

            Result<HealthReportDto> healthResult = Result<HealthReportDto>.Success(report);

            ApiResponse<HealthReportDto> response = ApiResponse<HealthReportDto>.FromResult(healthResult, traceId);

            int statusCode = report.Status == HealthStatus.Unhealthy
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK;

            return Results.Json(response, ArcanumJsonContext.Default.ApiResponseHealthReportDto, statusCode: statusCode);

        })
        .WithName("GetHealth");

        apiGroup.MapGet("/grimoire/stats", async (GrimoireStatsService statsService, HttpContext httpContext, CancellationToken cancellationToken) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            GrimoireStatsDto stats = await statsService.GetStatsAsync(cancellationToken).ConfigureAwait(false);

            Result<GrimoireStatsDto> statsResult = Result<GrimoireStatsDto>.Success(stats);

            ApiResponse<GrimoireStatsDto> response = ApiResponse<GrimoireStatsDto>.FromResult(statsResult, traceId);

            return Results.Ok(response);

        })
        .WithName("GetGrimoireStats");

        apiGroup.MapGet("/meta", (IOptionsSnapshot<ArcanumSettings> settings, ILlamaServerManager llamaServerManager, HttpContext httpContext) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            using Process process = Process.GetCurrentProcess();

            DateTimeOffset startTime = new(process.StartTime.ToUniversalTime(), TimeSpan.Zero);

            InstanceMetadataDto metadata = new(
                Version: GetInformationalVersion(),
                OsDescription: RuntimeInformation.OSDescription,
                RuntimeIdentifier: RuntimeInformation.RuntimeIdentifier,
                ProcessId: Environment.ProcessId,
                StartTime: startTime,
                Uptime: DateTimeOffset.UtcNow - startTime,
                NativeAot: !RuntimeFeature.IsDynamicCodeSupported,
                GrimoireDirectory: ArcanumPaths.GrimoireDirectory,
                ConfigPath: Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json"),
                Port: ArcanumSettingClamps.HostPort(settings.Value.Host.Port),
                ListenAny: ArcanumEnvironment.IsHostAnyEnabled(settings.Value.Host.ListenAny),
                LoreSystemEnabled: settings.Value.Intelligence.EnableLoreSystem,
                ArchiveSearchEnabled: settings.Value.Intelligence.EnableArchiveSearch,
                ContextCompressionEnabled: settings.Value.Intelligence.EnableContextCompression,
                TokenTrackingEnabled: settings.Value.Intelligence.EnableTokenTracking,
                LlamaCppEnabled: llamaServerManager.IsLlamaServerAvailable());

            Result<InstanceMetadataDto> metadataResult = metadata;

            ApiResponse<InstanceMetadataDto> response = ApiResponse<InstanceMetadataDto>.FromResult(metadataResult, traceId);

            return Results.Ok(response);
        })
        .WithName("GetInstanceMetadata")
        ;

        apiGroup.MapLlamaEndpoints();

        apiGroup.MapGet("/config", (IOptionsSnapshot<ArcanumSettings> settings, HttpContext httpContext) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            ArcanumSettings redacted = ConfigurationRedactor.Redact(settings.Value);

            Result<ArcanumSettings> settingsResult = redacted;

            ApiResponse<ArcanumSettings> response = ApiResponse<ArcanumSettings>.FromResult(settingsResult, traceId);

            return Results.Ok(response);
        })
        .WithName("GetConfiguration")
        ;

        apiGroup.MapPut("/config", async (
            ConfigurationWriter writer,
            ConfigurationValidator validator,
            IOptionsSnapshot<ArcanumSettings> currentSettings,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            ArcanumSettings? request;

            IResult? jsonError;

            (request, jsonError) = await ApiRequestJson.ReadAsync(
                httpContext,
                ArcanumJsonContext.Default.ArcanumSettings,
                static ctx => ApiRequestJson.InvalidBodyResult(
                    ctx,
                    "Request body must be a valid ArcanumSettings JSON object."),
                cancellationToken).ConfigureAwait(false);

            if (jsonError is not null)
            {
                return jsonError;
            }

            if (request is null)
            {
                Result<bool> invalid = Result<bool>.Failure(
                    new Error("Validation.InvalidBody", "Request body must be a valid ArcanumSettings JSON object."));

                return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
            }

            ArcanumSettings merged = ConfigurationRedactor.MergeApiKeys(request, currentSettings.Value);

            Result outbound = await OutboundUrlGuard.ValidateArcanumSettingsAsync(merged, cancellationToken).ConfigureAwait(false);

            if (outbound.IsFailure)
            {
                Result<bool> invalid = Result<bool>.Failure(outbound.Error);

                return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
            }

            Result validation = validator.Validate(merged);

            if (validation.IsFailure)
            {
                Result<bool> invalid = Result<bool>.Failure(validation.Error);

                return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
            }

            Result writeResult = await writer.WriteAsync(merged, httpContext.RequestAborted).ConfigureAwait(false);

            if (writeResult.IsFailure)
            {
                return Results.Json(
                    ApiResponse<bool>.FromResult(Result<bool>.Failure(writeResult.Error), traceId),
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId));
        })
        .WithName("UpdateConfiguration")
        ;

        apiGroup.MapPost("/config/validate", async (
            ConfigurationValidator validator,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            ArcanumSettings? request;

            IResult? jsonError;

            (request, jsonError) = await ApiRequestJson.ReadAsync(
                httpContext,
                ArcanumJsonContext.Default.ArcanumSettings,
                static ctx => ApiRequestJson.InvalidBodyResult(
                    ctx,
                    "Request body must be a valid ArcanumSettings JSON object."),
                cancellationToken).ConfigureAwait(false);

            if (jsonError is not null)
            {
                return jsonError;
            }

            if (request is null)
            {
                Result<bool> invalid = Result<bool>.Failure(
                    new Error("Validation.InvalidBody", "Request body must be a valid ArcanumSettings JSON object."));

                return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
            }

            Result outbound = await OutboundUrlGuard.ValidateArcanumSettingsAsync(request, cancellationToken).ConfigureAwait(false);

            if (outbound.IsFailure)
            {
                Result<bool> invalid = Result<bool>.Failure(outbound.Error);

                return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
            }

            Result validation = validator.Validate(request);

            Result<bool> result = validation.IsSuccess
                ? Result<bool>.Success(true)
                : Result<bool>.Failure(validation.Error);

            return Results.Ok(ApiResponse<bool>.FromResult(result, traceId));
        })
        .WithName("ValidateConfiguration")
        ;

        apiGroup.MapPost("/intelligence/ping", async (PingRequest? body, IArcanumIntelligenceProvider intelligence, ICampaignRepository campaignRepository, IOptionsSnapshot<ArcanumSettings> settings, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            if (body is null
                || (string.IsNullOrWhiteSpace(body.Prompt) && body.StatelessMessages is not { Count: > 0 }))
            {
                Result<PromptResponseDto> invalid = Result<PromptResponseDto>.Failure(new Error("Validation.InvalidPrompt", "Prompt is required unless StatelessMessages is provided."));

                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                return Results.Json(
                    ApiResponse<PromptResponseDto>.FromResult(invalid, badTraceId),
                    ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            Result pingBounds = PingRequestBoundsValidator.Validate(body, settings.Value);

            if (pingBounds.IsFailure)
            {
                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                return Results.Json(
                    ApiResponse<PromptResponseDto>.FromResult(
                        Result<PromptResponseDto>.Failure(pingBounds.Error),
                        badTraceId),
                    ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            Result<PingRequest> resolvedRequest = await PingRequestResolver
                .ResolveCampaignAsync(body, campaignRepository, cancellationToken)
                .ConfigureAwait(false);

            if (resolvedRequest.IsFailure)
            {
                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                return Results.Json(
                    ApiResponse<PromptResponseDto>.FromResult(
                        Result<PromptResponseDto>.Failure(resolvedRequest.Error),
                        badTraceId),
                    ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            Result<PromptTurnResult> turn = await intelligence.ExecutePromptAsync(resolvedRequest.Value, cancellationToken).ConfigureAwait(false);

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result<PromptResponseDto> envelopeResult = turn.IsFailure
                ? Result<PromptResponseDto>.Failure(turn.Error)
                : Result<PromptResponseDto>.Success(new PromptResponseDto(
                    turn.Value.Text,
                    turn.Value.Usage,
                    turn.Value.ToolCalls,
                    turn.Value.FinishReason));

            ApiResponse<PromptResponseDto> response = ApiResponse<PromptResponseDto>.FromResult(envelopeResult, traceId);

            return turn.IsSuccess
                ? Results.Ok(response)
                : Results.Json(response, ArcanumJsonContext.Default.ApiResponsePromptResponseDto, statusCode: StatusCodes.Status500InternalServerError);
        })
        .WithName("PostIntelligencePing");

        apiGroup.MapPost(
            "/intelligence/human-response",
            async (HttpContext httpContext, IHumanPromptRegistry registry, CancellationToken cancellationToken) =>
            {
                SubmitHumanResponseRequest? body;

                IResult? jsonError;

                (body, jsonError) = await ApiRequestJson.ReadAsync(
                    httpContext,
                    ArcanumJsonContext.Default.SubmitHumanResponseRequest,
                    static ctx => ApiRequestJson.InvalidBodyResult(ctx, ApiRequestJson.MalformedJsonMessage),
                    cancellationToken).ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (body is null)
                {

                    Result<bool> invalid = Result<bool>.Failure(
                        new Error("Validation.InvalidBody", ApiRequestJson.DefaultInvalidBodyMessage));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));

                }

                if (string.IsNullOrWhiteSpace(body.PromptId)
                    || string.IsNullOrWhiteSpace(body.Answer))
                {
                    Result<bool> invalid = Result<bool>.Failure(
                        new Error("Validation.InvalidHumanResponse", "promptId and answer are required."));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
                }

                bool accepted = registry.TrySubmitResponse(body.PromptId.Trim(), body.Answer);

                if (!accepted)
                {
                    Result<bool> notFound = Result<bool>.Failure(
                        new Error(
                            "Intelligence.HumanPromptNotFound",
                            "No active ask_human prompt matches that promptId (unknown, expired, or already answered)."));

                    return Results.NotFound(ApiResponse<bool>.FromResult(notFound, traceId));
                }

                Result<bool> ok = Result<bool>.Success(true);

                return Results.Ok(ApiResponse<bool>.FromResult(ok, traceId));
            })
        .WithName("PostIntelligenceHumanResponse");

        apiGroup.MapPost("/intelligence/ping-stream", async (HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(
                httpContext.RequestAborted,
                cancellationToken);

            CancellationToken ct = streamCts.Token;

            PingRequest? body;

            try
            {

                body = await httpContext.Request
                    .ReadFromJsonAsync(ArcanumJsonContext.Default.PingRequest, ct)
                    .ConfigureAwait(false);

            }
            catch (JsonException)
            {

                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

                await httpContext.Response.WriteAsJsonAsync(
                    ApiResponse<string>.FromResult(
                        Result<string>.Failure(new Error("Validation.InvalidBody", ApiRequestJson.MalformedJsonMessage)),
                        badTraceId),
                    ArcanumJsonContext.Default.ApiResponseString,
                    cancellationToken: ct).ConfigureAwait(false);

                return;

            }

            if (body is null
                || (string.IsNullOrWhiteSpace(body.Prompt) && body.StatelessMessages is not { Count: > 0 }))
            {
                Result<string> invalid = Result<string>.Failure(new Error("Validation.InvalidPrompt", "Prompt is required unless StatelessMessages is provided."));

                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

                await httpContext.Response.WriteAsJsonAsync(ApiResponse<string>.FromResult(invalid, badTraceId), ArcanumJsonContext.Default.ApiResponseString, cancellationToken: ct).ConfigureAwait(false);

                return;
            }

            ArcanumSettings arcSettings = httpContext.RequestServices.GetRequiredService<IOptionsSnapshot<ArcanumSettings>>().Value;

            Result streamPingBounds = PingRequestBoundsValidator.Validate(body, arcSettings);

            if (streamPingBounds.IsFailure)
            {
                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

                await httpContext.Response.WriteAsJsonAsync(
                    ApiResponse<string>.FromResult(Result<string>.Failure(streamPingBounds.Error), badTraceId),
                    ArcanumJsonContext.Default.ApiResponseString,
                    cancellationToken: ct).ConfigureAwait(false);

                return;
            }

            ICampaignRepository campaignRepository = httpContext.RequestServices.GetRequiredService<ICampaignRepository>();

            Result<PingRequest> resolvedRequest = await PingRequestResolver
                .ResolveCampaignAsync(body, campaignRepository, ct)
                .ConfigureAwait(false);

            if (resolvedRequest.IsFailure)
            {
                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

                await httpContext.Response.WriteAsJsonAsync(
                    ApiResponse<string>.FromResult(Result<string>.Failure(resolvedRequest.Error), badTraceId),
                    ArcanumJsonContext.Default.ApiResponseString,
                    cancellationToken: ct).ConfigureAwait(false);

                return;
            }

            IArcanumIntelligenceProvider intelligence = httpContext.RequestServices.GetRequiredService<IArcanumIntelligenceProvider>();

            await InferenceExecuteWriter
                .WriteStreamAsync(httpContext, intelligence, resolvedRequest.Value, ct)
                .ConfigureAwait(false);

        })
        .WithName("PostIntelligencePingStream");

        apiGroup.MapPost("/mcp/reload", async (OptionalWorkspaceRequest? body, IMcpConnectionManager mcp, HttpContext httpContext, CancellationToken ct) =>
        {
            string workingDirectory = body?.WorkingDirectory ?? string.Empty;

            await mcp.ReloadAsync(workingDirectory, ct).ConfigureAwait(false);

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result<string> ok = Result<string>.Success("MCP partitions cleared; global re-bootstrapped.");

            return Results.Ok(ApiResponse<string>.FromResult(ok, traceId));
        })
        .WithName("PostMcpReload");

        apiGroup.MapGet("/mcp", async (IMcpConnectionManager manager, HttpContext httpContext, CancellationToken ct) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            McpServerInfo[] servers = await manager.GetAllStatusesAsync(ct).ConfigureAwait(false);

            Result<McpServerInfo[]> ok = Result<McpServerInfo[]>.Success(servers);

            return Results.Ok(ApiResponse<McpServerInfo[]>.FromResult(ok, traceId));
        })
        .WithName("GetMcpServers")
        ;

        apiGroup.MapGet("/mcp/{name}", async (string name, string? workingDirectory, IMcpConnectionManager manager, HttpContext httpContext, CancellationToken ct) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                McpServerInfo[] all = await manager.GetAllStatusesAsync(ct).ConfigureAwait(false);

                List<McpServerInfo> matches = all.Where(s => string.Equals(s.Name, name, StringComparison.Ordinal)).ToList();

                if (matches.Count > 1)
                {
                    Result<McpServerInfo> ambiguous = Result<McpServerInfo>.Failure(
                        new Error("Mcp.AmbiguousServer", $"Multiple MCP servers named '{name}' exist; specify workingDirectory."));

                    return Results.BadRequest(ApiResponse<McpServerInfo>.FromResult(ambiguous, traceId));
                }

                if (matches.Count == 0)
                {
                    Result<McpServerInfo> notFound = Result<McpServerInfo>.Failure(
                        new Error("Mcp.ServerNotFound", $"No MCP server named '{name}' was found."));

                    return Results.NotFound(ApiResponse<McpServerInfo>.FromResult(notFound, traceId));
                }

                Result<McpServerInfo> found = Result<McpServerInfo>.Success(matches[0]);

                return Results.Ok(ApiResponse<McpServerInfo>.FromResult(found, traceId));
            }

            McpServerInfo? server = await manager.GetStatusAsync(name, workingDirectory, ct).ConfigureAwait(false);

            if (server is null)
            {
                Result<McpServerInfo> notFound = Result<McpServerInfo>.Failure(
                    new Error("Mcp.ServerNotFound", $"No MCP server named '{name}' was found."));

                return Results.NotFound(ApiResponse<McpServerInfo>.FromResult(notFound, traceId));
            }

            Result<McpServerInfo> statusOk = Result<McpServerInfo>.Success(server);

            return Results.Ok(ApiResponse<McpServerInfo>.FromResult(statusOk, traceId));
        })
        .WithName("GetMcpServer")
        ;

        apiGroup.MapPost("/mcp/{name}/start", async (string name, string? workingDirectory, IMcpConnectionManager manager, HttpContext httpContext, CancellationToken ct) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result result = await manager.StartAsync(name, workingDirectory, httpContext.RequestAborted).ConfigureAwait(false);

            return result.IsSuccess
                ? Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId))
                : Results.BadRequest(ApiResponse<bool>.FromResult(Result<bool>.Failure(result.Error), traceId));
        })
        .WithName("StartMcpServer")
        ;

        apiGroup.MapPost("/mcp/{name}/stop", async (string name, string? workingDirectory, IMcpConnectionManager manager, HttpContext httpContext, CancellationToken ct) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result result = await manager.StopAsync(name, workingDirectory, httpContext.RequestAborted).ConfigureAwait(false);

            return result.IsSuccess
                ? Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId))
                : Results.BadRequest(ApiResponse<bool>.FromResult(Result<bool>.Failure(result.Error), traceId));
        })
        .WithName("StopMcpServer")
        ;

        apiGroup.MapPost("/mcp/{name}/restart", async (string name, string? workingDirectory, IMcpConnectionManager manager, HttpContext httpContext, CancellationToken ct) =>
        {
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result result = await manager.RestartAsync(name, workingDirectory, httpContext.RequestAborted).ConfigureAwait(false);

            return result.IsSuccess
                ? Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId))
                : Results.BadRequest(ApiResponse<bool>.FromResult(Result<bool>.Failure(result.Error), traceId));
        })
        .WithName("RestartMcpServer")
        ;

        apiGroup.MapPost("/mcp/trust-workspace", async (OptionalWorkspaceRequest? body, IMcpConnectionManager manager, HttpContext httpContext, CancellationToken ct) =>
        {

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            if (body is null || string.IsNullOrWhiteSpace(body.WorkingDirectory))
            {

                return Results.BadRequest(
                    ApiResponse<bool>.FromResult(
                        Result<bool>.Failure(new Error("Mcp.MissingWorkspace", "workingDirectory is required.")),
                        traceId));

            }

            Result result = await manager.TrustWorkspaceAsync(body.WorkingDirectory!, httpContext.RequestAborted).ConfigureAwait(false);

            return result.IsSuccess
                ? Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId))
                : Results.BadRequest(ApiResponse<bool>.FromResult(Result<bool>.Failure(result.Error), traceId));

        })
        .WithName("TrustMcpWorkspace")
        ;

        apiGroup.MapPost("/intelligence/arsenal", async (OptionalWorkspaceRequest? body, IMcpConnectionManager mcp, IOptionsSnapshot<ArcanumSettings> settings, HttpContext httpContext, CancellationToken ct) =>
        {
            string workingDirectory = body?.WorkingDirectory ?? string.Empty;

            string? spellRoot = ToolHelpers.TryNormalizeWorkspace(workingDirectory, out string? root, out _)
                ? root
                : null;

            long maxSpellFileSizeBytes = ArcanumSettingClamps.EffectiveSpellMaxFileSizeBytes(settings.Value);

            IReadOnlyList<Core.Intelligence.Spells.SpellSummary> spellSummaries = await SpellScanner
                .ScanSummariesAsync(spellRoot, ct, maxSpellFileSizeBytes)
                .ConfigureAwait(false);

            List<string> spellNames = spellSummaries.Select(static s => s.Name).ToList();

            List<string> nativeTools = [ArcanumLocalTimeTool.ToolName, ArcanumSystemInfoTool.ToolName];

            List<McpServerStatusDto> servers = await mcp.GetServerStatusesAsync(workingDirectory, ct).ConfigureAwait(false);

            WorkspaceArsenalDto dto = new(spellNames, nativeTools, servers, spellSummaries.ToList());

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result<WorkspaceArsenalDto> arsenalOk = Result<WorkspaceArsenalDto>.Success(dto);

            return Results.Ok(ApiResponse<WorkspaceArsenalDto>.FromResult(arsenalOk, traceId));
        })
        .WithName("PostIntelligenceArsenal");

        apiGroup.MapGet(
            "/perception/look",
            async (
                string? directory,
                IEyeOfTheWorld eye,
                IOptionsSnapshot<ArcanumSettings> settings,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                string path = string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory;

                string resolved;

                try
                {
                    resolved = Path.GetFullPath(path);
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<PatternSnapshot> invalid = Result<PatternSnapshot>.Failure(
                        new Error("Perception.InvalidPath", "The specified directory could not be resolved."));

                    return Results.BadRequest(ApiResponse<PatternSnapshot>.FromResult(invalid, badTraceId));
                }

                if (!Directory.Exists(resolved))
                {
                    string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<PatternSnapshot> invalid = Result<PatternSnapshot>.Failure(
                        new Error("Perception.InvalidPath", "The specified directory does not exist or is inaccessible."));

                    return Results.BadRequest(ApiResponse<PatternSnapshot>.FromResult(invalid, badTraceId));
                }

                string[] allowedRoots = settings.Value.Perception.AllowedWorkspaceRoots ?? [];

                Result<string> allowed = WorkspaceRootPolicy.EnforceAllowedRoots(
                    resolved,
                    allowedRoots,
                    "Perception.PathNotAllowed",
                    "The specified directory is outside the configured Perception.AllowedWorkspaceRoots.");

                if (allowed.IsFailure)
                {
                    string deniedTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    return Results.Json(
                        ApiResponse<PatternSnapshot>.FromResult(
                            Result<PatternSnapshot>.Failure(allowed.Error),
                            deniedTraceId),
                        ArcanumJsonContext.Default.ApiResponsePatternSnapshot,
                        statusCode: StatusCodes.Status403Forbidden);
                }

                PatternSnapshot snapshot = await eye.PerceivePatternAsync(resolved, cancellationToken).ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<PatternSnapshot> ok = Result<PatternSnapshot>.Success(snapshot);

                return Results.Ok(ApiResponse<PatternSnapshot>.FromResult(ok, traceId));
            })
        .WithName("GetPerceptionLook");

        apiGroup.MapGet(
            "/lore",
            async (
                int? limit,
                int? offset,
                IGrimoireRepository grimoire,
                HttpContext httpContext,
                CancellationToken cancellationToken) =>
            {
                ListPageResult<LoreDto> page = await grimoire
                    .ListLoreAsync(limit, offset ?? 0, cancellationToken)
                    .ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<ListPageResult<LoreDto>> ok = Result<ListPageResult<LoreDto>>.Success(page);

                return Results.Ok(ApiResponse<ListPageResult<LoreDto>>.FromResult(ok, traceId));
            })
        .WithName("GetLore");

        apiGroup.MapGet(
            "/lore/{key}",
            async (string key, IGrimoireRepository grimoire, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                string normalizedKey = key.Trim();

                if (normalizedKey.Length == 0 || normalizedKey.Length > 256)
                {
                    string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<LoreDto> invalid = Result<LoreDto>.Failure(
                        new Error("Validation.InvalidKey", "Key must be between 1 and 256 characters."));

                    return Results.BadRequest(ApiResponse<LoreDto>.FromResult(invalid, badTraceId));
                }

                LoreDto? lore = await grimoire.GetLoreAsync(normalizedKey, cancellationToken).ConfigureAwait(false);

                if (lore is null)
                {
                    string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<LoreDto> notFound = Result<LoreDto>.Failure(
                        new Error("Grimoire.LoreNotFound", "No lore exists with that key."));

                    return Results.NotFound(ApiResponse<LoreDto>.FromResult(notFound, traceId));
                }

                string okTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<LoreDto> ok = Result<LoreDto>.Success(lore);

                return Results.Ok(ApiResponse<LoreDto>.FromResult(ok, okTraceId));
            })
        .WithName("GetLoreByKey");

        apiGroup.MapPost(
            "/lore",
            async (HttpContext httpContext, IGrimoireRepository grimoire, CancellationToken cancellationToken) =>
            {
                UpsertLoreRequest? body;

                IResult? jsonError;

                (body, jsonError) = await ApiRequestJson.ReadAsync(
                    httpContext,
                    ArcanumJsonContext.Default.UpsertLoreRequest,
                    static ctx => ApiRequestJson.InvalidBodyResult<LoreDto>(
                        ctx,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseLoreDto),
                    cancellationToken).ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (body is null)
                {

                    Result<LoreDto> invalid = Result<LoreDto>.Failure(
                        new Error("Validation.InvalidBody", ApiRequestJson.DefaultInvalidBodyMessage));

                    return Results.BadRequest(ApiResponse<LoreDto>.FromResult(invalid, traceId));

                }

                if (string.IsNullOrWhiteSpace(body.Key)
                    || string.IsNullOrWhiteSpace(body.Value))
                {
                    Result<LoreDto> invalid = Result<LoreDto>.Failure(
                        new Error("Validation.InvalidLore", "Key and value are required."));

                    return Results.BadRequest(ApiResponse<LoreDto>.FromResult(invalid, traceId));
                }

                string trimmedKey = body.Key.Trim();

                if (trimmedKey.Length > 256)
                {
                    Result<LoreDto> invalid = Result<LoreDto>.Failure(
                        new Error("Validation.InvalidKey", "Key must not exceed 256 characters."));

                    return Results.BadRequest(ApiResponse<LoreDto>.FromResult(invalid, traceId));
                }

                LoreDto saved = await grimoire
                    .ScribeLoreAsync(trimmedKey, body.Value, cancellationToken)
                    .ConfigureAwait(false);

                Result<LoreDto> ok = Result<LoreDto>.Success(saved);

                return Results.Ok(ApiResponse<LoreDto>.FromResult(ok, traceId));
            })
        .WithName("UpsertLore");

        apiGroup.MapDelete(
            "/lore/{key}",
            async (string key, IGrimoireRepository grimoire, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                string normalizedKey = key.Trim();

                if (normalizedKey.Length == 0 || normalizedKey.Length > 256)
                {
                    string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<bool> invalid = Result<bool>.Failure(
                        new Error("Validation.InvalidKey", "Key must be between 1 and 256 characters."));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, badTraceId));
                }

                bool removed = await grimoire.DeleteLoreAsync(normalizedKey, cancellationToken).ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                if (!removed)
                {
                    Result<bool> notFound = Result<bool>.Failure(
                        new Error("Grimoire.LoreNotFound", "No lore exists with that key."));

                    return Results.NotFound(ApiResponse<bool>.FromResult(notFound, traceId));
                }

                Result<bool> ok = Result<bool>.Success(true);

                return Results.Ok(ApiResponse<bool>.FromResult(ok, traceId));
            })
        .WithName("DeleteLore");

        apiGroup.MapGet(
            "/spells",
            async (
                string? workspace,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string?> workspaceResult = workspaceResolver.Resolve(workspace);

                IResult? workspaceFailure = SpellApiResults.MapOptionalWorkspaceFailure<SpellSummary[]>(
                    workspaceResult,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellSummaryArray,
                    out string? resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                SpellSummary[] spells = await repo.ListAsync(resolvedWorkspace, ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(ApiResponse<SpellSummary[]>.FromResult(Result<SpellSummary[]>.Success(spells), traceId));
            })
        .WithName("ListSpells")
        ;

        apiGroup.MapGet(
            "/spells/{name}",
            async (
                string name,
                string? workspace,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string?> workspaceResult = workspaceResolver.Resolve(workspace);

                IResult? workspaceFailure = SpellApiResults.MapOptionalWorkspaceFailure<SpellDetail>(
                    workspaceResult,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseSpellDetail,
                    out string? resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                SpellDetail? spell = await repo.GetAsync(name, resolvedWorkspace, ctx.RequestAborted).ConfigureAwait(false);

                if (spell is null)
                {
                    return Results.Json(
                        ApiResponse<SpellDetail>.FromResult(
                            Result<SpellDetail>.Failure(new Error("Spell.NotFound", "No spell exists with that name.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseSpellDetail,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Ok(ApiResponse<SpellDetail>.FromResult(Result<SpellDetail>.Success(spell), traceId));
            })
        .WithName("GetSpell")
        ;

        apiGroup.MapPost(
            "/spells",
            async (
                string? workspace,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                CreateSpellRequest? request;

                IResult? jsonError;

                (request, jsonError) = await ApiRequestJson.ReadAsync(
                    ctx,
                    ArcanumJsonContext.Default.CreateSpellRequest,
                    static httpContext => ApiRequestJson.InvalidBodyResult(
                        httpContext,
                        ApiRequestJson.MalformedJsonMessage),
                    ctx.RequestAborted).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error("Validation.InvalidBody", "Request body is required.")),
                            traceId));
                }

                Result<string> workspaceRequired = workspaceResolver.ResolveRequired(workspace);

                IResult? workspaceFailure = SpellApiResults.MapRequiredWorkspaceFailure<bool>(
                    workspaceRequired,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    out string resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                Result result = await repo
                    .CreateAsync(resolvedWorkspace, request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId))
                    : Results.BadRequest(ApiResponse<bool>.FromResult(Result<bool>.Failure(result.Error), traceId));
            })
        .WithName("CreateSpell")
        ;

        apiGroup.MapPut(
            "/spells/{name}",
            async (
                string name,
                string? workspace,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                UpdateSpellRequest? request;

                IResult? jsonError;

                (request, jsonError) = await ApiRequestJson.ReadAsync(
                    ctx,
                    ArcanumJsonContext.Default.UpdateSpellRequest,
                    static httpContext => ApiRequestJson.InvalidBodyResult(
                        httpContext,
                        ApiRequestJson.MalformedJsonMessage),
                    ctx.RequestAborted).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(new Error("Validation.InvalidBody", "Request body is required.")),
                            traceId));
                }

                Result<string> workspaceRequired = workspaceResolver.ResolveRequired(workspace);

                IResult? workspaceFailure = SpellApiResults.MapRequiredWorkspaceFailure<bool>(
                    workspaceRequired,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    out string resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                Result result = await repo
                    .UpdateAsync(name, resolvedWorkspace, request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<bool>.FromResult(Result<bool>.Success(true), traceId))
                    : Results.BadRequest(ApiResponse<bool>.FromResult(Result<bool>.Failure(result.Error), traceId));
            })
        .WithName("UpdateSpell")
        ;

        apiGroup.MapDelete(
            "/spells/{name}",
            async (
                string name,
                string? workspace,
                ISpellRepository repo,
                SpellWorkspaceResolver workspaceResolver,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<string> workspaceRequired = workspaceResolver.ResolveRequired(workspace);

                IResult? workspaceFailure = SpellApiResults.MapRequiredWorkspaceFailure<bool>(
                    workspaceRequired,
                    traceId,
                    ArcanumJsonContext.Default.ApiResponseBoolean,
                    out string resolvedWorkspace);

                if (workspaceFailure is not null)
                {
                    return workspaceFailure;
                }

                Result result = await repo
                    .DeleteAsync(name, resolvedWorkspace, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(result.Error),
                            Activity.Current?.Id ?? ctx.TraceIdentifier));
            })
        .WithName("DeleteSpell")
        ;

        apiGroup.MapSpellForgeEndpoints();

        apiGroup.MapSpellExecutionEndpoints();

        apiGroup.MapCampaignEndpoints();

        apiGroup.MapSessionEndpoints();

        apiGroup.MapSanctumEndpoints();

        apiGroup.MapWardEndpoints();

        apiGroup.MapPromptEndpoints();

        apiGroup.MapApprenticeEndpoints();

        apiGroup.MapCodexEndpoints();

        apiGroup.MapProviderTestEndpoints();

        apiGroup.MapProvingGroundsEndpoints();

        apiGroup.MapGet(
            "/workspaces",
            async (IWorkspaceRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                WorkspaceInfo[] workspaces = await registry
                    .GetAllAsync(ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<WorkspaceInfo[]>.FromResult(
                        Result<WorkspaceInfo[]>.Success(workspaces),
                        traceId));
            })
        .WithName("ListWorkspaces")
        ;

        apiGroup.MapGet(
            "/workspaces/{id}",
            async (string id, IWorkspaceRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                WorkspaceInfo? workspace = await registry
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return workspace is null
                    ? Results.Json(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Failure(new Error("Workspace.NotFound", "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo,
                        statusCode: StatusCodes.Status404NotFound)
                    : Results.Ok(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Success(workspace),
                            traceId));
            })
        .WithName("GetWorkspace")
        ;

        apiGroup.MapPost(
            "/workspaces",
            async (IWorkspaceRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                CreateWorkspaceRequest? request;

                IResult? jsonError;

                (request, jsonError) = await ApiRequestJson.ReadAsync(
                    ctx,
                    ArcanumJsonContext.Default.CreateWorkspaceRequest,
                    static httpContext => ApiRequestJson.InvalidBodyResult<WorkspaceInfo>(
                        httpContext,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo),
                    ctx.RequestAborted).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (request is null || string.IsNullOrWhiteSpace(request.Name))
                {
                    return Results.BadRequest(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Failure(
                                new Error("Workspace.NameEmpty", "Workspace name cannot be empty.")),
                            traceId));
                }

                Result<WorkspaceInfo> result = await registry
                    .RegisterAsync(request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    return Results.Created(
                        $"/api/workspaces/{result.Value.Id}",
                        ApiResponse<WorkspaceInfo>.FromResult(result, traceId));
                }

                if (string.Equals(result.Error.Code, "Workspace.PathNotAllowed", StringComparison.Ordinal))
                {
                    return Results.Json(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Failure(result.Error),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo,
                        statusCode: StatusCodes.Status403Forbidden);
                }

                return Results.BadRequest(
                    ApiResponse<WorkspaceInfo>.FromResult(
                        Result<WorkspaceInfo>.Failure(result.Error),
                        traceId));
            })
        .WithName("RegisterWorkspace")
        ;

        apiGroup.MapPut(
            "/workspaces/{id}",
            async (string id, IWorkspaceRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                UpdateWorkspaceRequest? request;

                IResult? jsonError;

                (request, jsonError) = await ApiRequestJson.ReadAsync(
                    ctx,
                    ArcanumJsonContext.Default.UpdateWorkspaceRequest,
                    static httpContext => ApiRequestJson.InvalidBodyResult<WorkspaceInfo>(
                        httpContext,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo),
                    ctx.RequestAborted).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (request is null)
                {
                    return Results.BadRequest(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Failure(
                                new Error("Validation.InvalidBody", "Request body is required.")),
                            traceId));
                }

                Result<WorkspaceInfo> result = await registry
                    .UpdateAsync(id, request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure && result.Error.Code == "Workspace.NotFound")
                {
                    return Results.Json(
                        ApiResponse<WorkspaceInfo>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseWorkspaceInfo,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<WorkspaceInfo>.FromResult(result, traceId))
                    : Results.BadRequest(
                        ApiResponse<WorkspaceInfo>.FromResult(
                            Result<WorkspaceInfo>.Failure(result.Error),
                            traceId));
            })
        .WithName("UpdateWorkspace")
        ;

        apiGroup.MapDelete(
            "/workspaces/{id}",
            async (string id, IWorkspaceRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<bool> result = await registry
                    .UnregisterAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (result.IsFailure && result.Error.Code == "Workspace.NotFound")
                {
                    return Results.Json(
                        ApiResponse<bool>.FromResult(result, traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status404NotFound);
                }

                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.BadRequest(
                        ApiResponse<bool>.FromResult(
                            Result<bool>.Failure(result.Error),
                            traceId));
            })
        .WithName("UnregisterWorkspace")
        ;

        apiGroup.MapGet(
            "/workspaces/{id}/files",
            async (
                string id,
                string? relativePath,
                bool recursive,
                string? searchPattern,
                IWorkspaceRegistry registry,
                IFileSystemBrowser browser,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                WorkspaceInfo? workspace = await registry
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (workspace is null)
                {
                    return Results.Json(
                        ApiResponse<FileListResult>.FromResult(
                            Result<FileListResult>.Failure(new Error("Workspace.NotFound", "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileListResult,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Result<FileListResult> result = await browser
                    .ListAsync(workspace, relativePath, recursive, searchPattern, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<FileListResult>.FromResult(result, traceId))
                    : Results.BadRequest(
                        ApiResponse<FileListResult>.FromResult(
                            Result<FileListResult>.Failure(result.Error),
                            traceId));
            })
        .WithName("ListWorkspaceFiles")
        ;

        apiGroup.MapGet(
            "/workspaces/{id}/files/info",
            async (
                string id,
                string? relativePath,
                IWorkspaceRegistry registry,
                IFileSystemBrowser browser,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                WorkspaceInfo? workspace = await registry
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (workspace is null)
                {
                    return Results.Json(
                        ApiResponse<FileEntry>.FromResult(
                            Result<FileEntry>.Failure(new Error("Workspace.NotFound", "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileEntry,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Result<FileEntry> result = await browser
                    .GetInfoAsync(workspace, relativePath, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<FileEntry>.FromResult(result, traceId))
                    : Results.BadRequest(
                        ApiResponse<FileEntry>.FromResult(
                            Result<FileEntry>.Failure(result.Error),
                            traceId));
            })
        .WithName("GetWorkspaceFileInfo")
        ;

        apiGroup.MapGet(
            "/workspaces/{id}/files/contents",
            async (
                string id,
                string relativePath,
                IWorkspaceRegistry registry,
                IFileSystemBrowser browser,
                HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                WorkspaceInfo? workspace = await registry
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                if (workspace is null)
                {
                    return Results.Json(
                        ApiResponse<FileReadResult>.FromResult(
                            Result<FileReadResult>.Failure(new Error("Workspace.NotFound", "No workspace exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseFileReadResult,
                        statusCode: StatusCodes.Status404NotFound);
                }

                Result<FileReadResult> result = await browser
                    .ReadAsync(workspace, relativePath, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<FileReadResult>.FromResult(result, traceId))
                    : Results.BadRequest(
                        ApiResponse<FileReadResult>.FromResult(
                            Result<FileReadResult>.Failure(result.Error),
                            traceId));
            })
        .WithName("ReadWorkspaceFileContents")
        ;

        apiGroup.MapGet(
            "/logs",
            async (
                Core.Logging.LogLevel? minLevel,
                string? category,
                DateTimeOffset? from,
                DateTimeOffset? to,
                string? search,
                int? limit,
                long? beforeSequence,
                ILogQueryService query,
                HttpContext ctx) =>
            {
                LogQueryRequest request = new(minLevel, category, from, to, search, limit, beforeSequence);

                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                LogQueryResult result = await query
                    .QueryAsync(request, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Ok(
                    ApiResponse<LogQueryResult>.FromResult(
                        Result<LogQueryResult>.Success(result),
                        traceId));
            })
        .WithName("QueryLogs")
        ;

        apiGroup.MapGet(
            "/events/daemon",
            async (HttpContext httpContext, IEventBus eventBus, SseConnectionGate sseGate, IOptionsSnapshot<ArcanumSettings> settings, CancellationToken cancellationToken) =>
            {

                if (!sseGate.TryAcquire(out SseConnectionLease? sseLease))
                {

                    return SseConnectionResults.TooManyConnections(httpContext);

                }

                using (sseLease)
                {

                using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(
                    httpContext.RequestAborted,
                    cancellationToken);

                CancellationToken ct = streamCts.Token;

                SseStreamWriter.PrepareResponse(httpContext);

                ArrayBufferWriter<byte> sseBuffer = new(512);

                Utf8JsonWriter sseJsonWriter = new(sseBuffer, new JsonWriterOptions { Indented = false });

                TimeSpan heartbeatInterval = ResolveSseHeartbeatInterval(settings.Value);

                try
                {

                    await SseStreamWriter.StreamAsync(
                        httpContext,
                        eventBus.Subscribe<DaemonEvent>(ct),
                        async (DaemonEvent ev, CancellationToken writeCt) =>
                        {
                            await WriteSseJsonAsync(httpContext, ev, ArcanumJsonContext.Default.DaemonEvent, sseBuffer, sseJsonWriter, writeCt).ConfigureAwait(false);
                        },
                        heartbeatInterval,
                        ct).ConfigureAwait(false);

                }
                catch (OperationCanceledException)
                {

                    await SseStreamWriter.WriteDoneAsync(httpContext).ConfigureAwait(false);

                }
                finally
                {

                    sseJsonWriter.Dispose();

                }

                return Results.Empty;

                }

            })
        .WithName("GetDaemonEvents");

        apiGroup.MapGet(
            "/events/mcp",
            async (HttpContext httpContext, IEventBus eventBus, SseConnectionGate sseGate, IOptionsSnapshot<ArcanumSettings> settings, CancellationToken cancellationToken) =>
            {

                if (!sseGate.TryAcquire(out SseConnectionLease? sseLease))
                {

                    return SseConnectionResults.TooManyConnections(httpContext);

                }

                using (sseLease)
                {

                using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(
                    httpContext.RequestAborted,
                    cancellationToken);

                CancellationToken ct = streamCts.Token;

                SseStreamWriter.PrepareResponse(httpContext);

                ArrayBufferWriter<byte> sseBuffer = new(512);

                Utf8JsonWriter sseJsonWriter = new(sseBuffer, new JsonWriterOptions { Indented = false });

                TimeSpan heartbeatInterval = ResolveSseHeartbeatInterval(settings.Value);

                try
                {

                    await SseStreamWriter.StreamAsync(
                        httpContext,
                        eventBus.Subscribe<McpServerEvent>(ct),
                        async (McpServerEvent ev, CancellationToken writeCt) =>
                        {
                            await WriteSseJsonAsync(httpContext, ev, ArcanumJsonContext.Default.McpServerEvent, sseBuffer, sseJsonWriter, writeCt).ConfigureAwait(false);
                        },
                        heartbeatInterval,
                        ct).ConfigureAwait(false);

                }
                catch (OperationCanceledException)
                {

                    await SseStreamWriter.WriteDoneAsync(httpContext).ConfigureAwait(false);

                }
                finally
                {

                    sseJsonWriter.Dispose();

                }

                return Results.Empty;

                }

            })
        .WithName("GetMcpEvents")
        ;

        apiGroup.MapGet(
            "/events/logs",
            async (HttpContext httpContext, ILogQueryService query, SseConnectionGate sseGate, IOptionsSnapshot<ArcanumSettings> settings, CancellationToken cancellationToken) =>
            {

                if (!sseGate.TryAcquire(out SseConnectionLease? sseLease))
                {

                    return SseConnectionResults.TooManyConnections(httpContext);

                }

                using (sseLease)
                {

                using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(
                    httpContext.RequestAborted,
                    cancellationToken);

                CancellationToken ct = streamCts.Token;

                SseStreamWriter.PrepareResponse(httpContext);

                ArrayBufferWriter<byte> sseBuffer = new(512);

                Utf8JsonWriter sseJsonWriter = new(sseBuffer, new JsonWriterOptions { Indented = false });

                TimeSpan heartbeatInterval = ResolveSseHeartbeatInterval(settings.Value);

                try
                {

                    await httpContext.Response.Body.WriteAsync(SseLogsConnected, ct).ConfigureAwait(false);

                    await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);

                    await SseStreamWriter.StreamAsync(
                        httpContext,
                        query.StreamAsync(null, ct),
                        async (LogEntry entry, CancellationToken writeCt) =>
                        {
                            await WriteSseJsonAsync(
                                httpContext,
                                entry,
                                ArcanumJsonContext.Default.LogEntry,
                                sseBuffer,
                                sseJsonWriter,
                                writeCt).ConfigureAwait(false);
                        },
                        heartbeatInterval,
                        ct).ConfigureAwait(false);

                }
                catch (OperationCanceledException)
                {

                    await SseStreamWriter.WriteDoneAsync(httpContext).ConfigureAwait(false);

                }
                finally
                {

                    sseJsonWriter.Dispose();

                }

                return Results.Empty;

                }

            })
        .WithName("StreamLogs")
        ;

        RouteGroupBuilder unseenServant = apiGroup.MapGroup("/unseen-servant");

        MapUnseenServantJobRoutes(unseenServant, routeNamePrefix: "UnseenServant");

        RouteGroupBuilder daemon = apiGroup.MapGroup("/daemon");

        MapUnseenServantJobRoutes(daemon, routeNamePrefix: "Daemon");

        apiGroup.MapGet(
            "/daemons",
            async (IDaemonRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                DaemonJobInfo[] jobs = await registry.GetAllAsync(ctx.RequestAborted).ConfigureAwait(false);

                return Results.Ok(ApiResponse<DaemonJobInfo[]>.FromResult(Result<DaemonJobInfo[]>.Success(jobs), traceId));
            })
        .WithName("ListDaemons");

        apiGroup.MapGet(
            "/daemons/{id}",
            async (string id, IDaemonRegistry registry, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                DaemonJobInfo? job = await registry.GetAsync(id, ctx.RequestAborted).ConfigureAwait(false);

                return job is null
                    ? Results.Json(
                        ApiResponse<DaemonJobInfo>.FromResult(
                            Result<DaemonJobInfo>.Failure(new Error("Daemon.NotFound", "No daemon job exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseDaemonJobInfo,
                        statusCode: StatusCodes.Status404NotFound)
                    : Results.Ok(ApiResponse<DaemonJobInfo>.FromResult(Result<DaemonJobInfo>.Success(job), traceId));
            })
        .WithName("GetDaemon");

        apiGroup.MapPost(
            "/daemons/{id}/run",
            async (string id, IDaemonRunner runner, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                Result<DaemonExecutionSummary> result = await runner
                    .RunAsync(id, force: true, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return result.IsSuccess
                    ? Results.Ok(ApiResponse<DaemonExecutionSummary>.FromResult(result, traceId))
                    : Results.BadRequest(ApiResponse<DaemonExecutionSummary>.FromResult(
                        Result<DaemonExecutionSummary>.Failure(result.Error), traceId));
            })
        .WithName("RunDaemon");

        apiGroup.MapGet(
            "/daemons/{id}/history",
            async (string id, IDaemonExecutionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                DaemonExecutionSummary[] history = await repo
                    .GetHistoryAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return Results.Ok(ApiResponse<DaemonExecutionSummary[]>.FromResult(
                    Result<DaemonExecutionSummary[]>.Success(history), traceId));
            })
        .WithName("GetDaemonHistory");

        apiGroup.MapGet(
            "/executions/{id}",
            async (string id, IDaemonExecutionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                DaemonExecutionDetail? execution = await repo
                    .GetAsync(id, ctx.RequestAborted)
                    .ConfigureAwait(false);

                return execution is null
                    ? Results.Json(
                        ApiResponse<DaemonExecutionDetail>.FromResult(
                            Result<DaemonExecutionDetail>.Failure(new Error("Execution.NotFound", "No execution exists with that id.")),
                            traceId),
                        ArcanumJsonContext.Default.ApiResponseDaemonExecutionDetail,
                        statusCode: StatusCodes.Status404NotFound)
                    : Results.Ok(ApiResponse<DaemonExecutionDetail>.FromResult(
                        Result<DaemonExecutionDetail>.Success(execution), traceId));
            })
        .WithName("GetExecution");

        apiGroup.MapPost(
            "/executions/{id}/cancel",
            async (string id, IDaemonExecutionRepository repo, HttpContext ctx) =>
            {
                string traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

                try
                {
                    DaemonExecutionSummary summary = await repo
                        .CancelAsync(id, ctx.RequestAborted)
                        .ConfigureAwait(false);

                    return Results.Ok(ApiResponse<DaemonExecutionSummary>.FromResult(
                        Result<DaemonExecutionSummary>.Success(summary), traceId));
                }
                catch (InvalidOperationException)
                {
                    return Results.BadRequest(ApiResponse<DaemonExecutionSummary>.FromResult(
                        Result<DaemonExecutionSummary>.Failure(
                            new Error("Daemon.NotRunning", "Execution is not running or does not exist.")),
                        traceId));
                }
            })
        .WithName("CancelExecution");

        apiGroup.MapPost(
            "/commlink/send",
            async (
                HttpContext httpContext,
                ICommLinkDispatcher commLink,
                CancellationToken cancellationToken) =>
            {
                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                CommLinkMessageRequestDto? body;

                IResult? jsonError;

                (body, jsonError) = await ApiRequestJson.ReadAsync(
                    httpContext,
                    ArcanumJsonContext.Default.CommLinkMessageRequestDto,
                    static ctx => ApiRequestJson.InvalidBodyResult(
                        ctx,
                        ApiRequestJson.MalformedJsonMessage),
                    cancellationToken).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (body is null)
                {
                    Result<bool> invalid = Result<bool>.Failure(
                        new Error("Validation.InvalidBody", "Request body is required."));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
                }

                if (string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Body))
                {
                    Result<bool> invalid = Result<bool>.Failure(
                        new Error("Validation.InvalidFields", "Title and body must not be empty."));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
                }

                string title = body.Title.Trim();

                string bodyText = body.Body.Trim();

                string source = string.IsNullOrWhiteSpace(body.Source) ? "api" : body.Source.Trim();

                CommLinkMessage message = new(title, bodyText, body.Severity, source);

                Result dispatch = await commLink
                    .DispatchAsync(message, cancellationToken)
                    .ConfigureAwait(false);

                if (dispatch.IsFailure)
                {
                    Result<bool> failed = Result<bool>.Failure(dispatch.Error);

                    return Results.Json(
                        ApiResponse<bool>.FromResult(failed, traceId),
                        ArcanumJsonContext.Default.ApiResponseBoolean,
                        statusCode: StatusCodes.Status502BadGateway);
                }

                Result<bool> ok = Result<bool>.Success(true);

                return Results.Ok(ApiResponse<bool>.FromResult(ok, traceId));
            })
        .WithName("PostCommLinkSend");
    }

    private static async Task WriteDaemonSseJsonAsync(
        HttpContext httpContext,
        DaemonEvent value,
        CancellationToken cancellationToken)
    {
        await WriteSseJsonAsync(httpContext, value, ArcanumJsonContext.Default.DaemonEvent, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSseJsonAsync<T>(
        HttpContext httpContext,
        T value,
        JsonTypeInfo<T> typeInfo,
        ArrayBufferWriter<byte> buffer,
        Utf8JsonWriter jsonWriter,
        CancellationToken cancellationToken)
    {

        buffer.Clear();

        buffer.Write(SseDataPrefix);

        jsonWriter.Reset();

        JsonSerializer.Serialize(jsonWriter, value, typeInfo);

        buffer.Write(SseLineBreak);

        await httpContext.Response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

        await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task WriteSseJsonAsync<T>(
        HttpContext httpContext,
        T value,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {

        ArrayBufferWriter<byte> buffer = new(512);

        Utf8JsonWriter jsonWriter = new(buffer);

        try
        {

            await WriteSseJsonAsync(httpContext, value, typeInfo, buffer, jsonWriter, cancellationToken).ConfigureAwait(false);

        }
        finally
        {

            jsonWriter.Dispose();

        }

    }

    private static TimeSpan ResolveSseHeartbeatInterval(ArcanumSettings settings)
    {

        int seconds = ArcanumSettingClamps.EventBusHeartbeatSeconds(
            settings.EventBus?.HeartbeatSeconds ?? new EventBusSettings().HeartbeatSeconds);

        return TimeSpan.FromSeconds(seconds);

    }

    private static string GetInformationalVersion()
    {

        string version = RetroDownfall.Arcanum.Core.ArcanumBuildInfo.InformationalVersion;

        int plus = version.IndexOf('+');

        if (plus >= 0)
        {

            version = version[..plus];

        }

        return version;

    }

    private static UnseenServantJobStatusDto ToUnseenServantJobStatusDto(
        UnseenServantJob job,
        IUnseenServantPacer pacer,
        IUnseenServantJobTracker tracker)
    {

        int effectiveInterval = pacer.GetEffectiveInterval(job);

        return new UnseenServantJobStatusDto(
            job.Name,
            job.TargetSpell,
            job.IntervalMinutes,
            effectiveInterval,
            job.Enabled,
            tracker.GetLastRunAt(job),
            tracker.GetNextDueAt(job, effectiveInterval),
            tracker.GetLastResult(job));

    }

    private static void MapUnseenServantJobRoutes(RouteGroupBuilder group, string routeNamePrefix)
    {

        group.MapGet(
            "/jobs",
            (IOptionsMonitor<ArcanumSettings> settings, IUnseenServantPacer pacer, IUnseenServantJobTracker tracker, HttpContext httpContext) =>
            {
                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                UnseenServantJobStatusDto[] dtos = (settings.CurrentValue.Daemon?.Jobs ?? [])
                    .Select(job => ToUnseenServantJobStatusDto(job, pacer, tracker))
                    .ToArray();

                Result<UnseenServantJobStatusDto[]> ok = Result<UnseenServantJobStatusDto[]>.Success(dtos);

                return Results.Ok(ApiResponse<UnseenServantJobStatusDto[]>.FromResult(ok, traceId));
            })
        .WithName($"{routeNamePrefix}GetDaemonJobs");

        group.MapPost(
            "/jobs/{name}/initiative",
            async (
                string name,
                HttpContext httpContext,
                IUnseenServantPacer pacer,
                IUnseenServantJobTracker tracker,
                IOptionsMonitor<ArcanumSettings> settings,
                CancellationToken cancellationToken) =>
            {
                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                AdjustInitiativeRequestDto? body;

                IResult? jsonError;

                (body, jsonError) = await ApiRequestJson.ReadAsync(
                    httpContext,
                    ArcanumJsonContext.Default.AdjustInitiativeRequestDto,
                    static ctx => ApiRequestJson.InvalidBodyResult<UnseenServantJobStatusDto>(
                        ctx,
                        ApiRequestJson.MalformedJsonMessage,
                        ArcanumJsonContext.Default.ApiResponseUnseenServantJobStatusDto),
                    cancellationToken).ConfigureAwait(false);

                if (jsonError is not null)
                {
                    return jsonError;
                }

                if (body is null)
                {
                    Result<UnseenServantJobStatusDto> invalid = Result<UnseenServantJobStatusDto>.Failure(
                        new Error("Validation.InvalidBody", "Request body is required."));

                    return Results.BadRequest(ApiResponse<UnseenServantJobStatusDto>.FromResult(invalid, traceId));
                }

                string trimmedName = name.Trim();

                if (trimmedName.Length == 0)
                {
                    Result<UnseenServantJobStatusDto> invalid = Result<UnseenServantJobStatusDto>.Failure(
                        new Error("Validation.InvalidJobName", "Job name must not be empty."));

                    return Results.BadRequest(ApiResponse<UnseenServantJobStatusDto>.FromResult(invalid, traceId));
                }

                pacer.SetDynamicInterval(trimmedName, body.IntervalMinutes);

                UnseenServantJob? configured = (settings.CurrentValue.Daemon?.Jobs ?? []).FirstOrDefault(
                    job => string.Equals(job.Name.Trim(), trimmedName, StringComparison.Ordinal));

                UnseenServantJob jobForInterval = configured
                    ?? new UnseenServantJob
                    {
                        Name = trimmedName,
                        TargetSpell = string.Empty,
                        IntervalMinutes = 60,
                        Enabled = false,
                    };

                UnseenServantJobStatusDto dto = ToUnseenServantJobStatusDto(jobForInterval, pacer, tracker);

                Result<UnseenServantJobStatusDto> ok = Result<UnseenServantJobStatusDto>.Success(dto);

                return Results.Ok(ApiResponse<UnseenServantJobStatusDto>.FromResult(ok, traceId));
            })
        .WithName($"{routeNamePrefix}PostDaemonJobInitiative");

    }

}
