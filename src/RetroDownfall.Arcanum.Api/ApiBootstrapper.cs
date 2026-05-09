using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Workspace;
using Scalar.AspNetCore;

namespace RetroDownfall.Arcanum.Api;

public static class ApiBootstrapper
{
    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "AddOpenApi pulls MVC model metadata with RequiresUnreferencedCode; Arcanum uses minimal APIs plus source-generated OpenAPI metadata—no controller-based model binding at runtime.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "ILC attributes the OpenAPI/Mvc.Abstractions ModelMetadata path as RequiresDynamicCode during Native AOT publish; registration is bounded to MapOpenApi/Scalar and minimal APIs.")]

    public static IServiceCollection AddArcanumApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IHumanPromptRegistry, HumanPromptRegistry>();

        services.AddArcanumInfrastructure(configuration);

        services.AddArcanumDaemonServices();

        services.AddSingleton<ApiKeyEndpointFilter>();

        services.AddCors(static options =>
        {
            options.AddPolicy(
                "AllowAll",
                static policy =>
                {
                    policy.AllowAnyOrigin();

                    policy.AllowAnyHeader();

                    policy.AllowAnyMethod();
                });
        });

        services.AddOpenApi();

        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

        services.AddHttpClient(
            "Ollama",
            (sp, client) =>
            {
                ArcanumSettings settings = sp.GetRequiredService<IOptions<ArcanumSettings>>().Value;

                client.BaseAddress = new Uri(settings.Ollama.Endpoint);

                client.Timeout = Timeout.InfiniteTimeSpan;
            });

        services.AddScoped<OllamaApiClient>(sp =>
        {
            IHttpClientFactory factory = sp.GetRequiredService<IHttpClientFactory>();

            ArcanumSettings settings = sp.GetRequiredService<IOptions<ArcanumSettings>>().Value;

            return new OllamaApiClient(factory.CreateClient("Ollama"), settings.Ollama.DefaultModel);
        });

        services.AddScoped<IOllamaApiClient>(sp => sp.GetRequiredService<OllamaApiClient>());

        services.AddScoped<IChatClient>(sp => sp.GetRequiredService<OllamaApiClient>());

        services.AddScoped<IArcanumIntelligenceProvider, OllamaIntelligenceProvider>();

        return services;
    }

    /// <summary>
    /// Enables permissive CORS for browser-based API consumers (must run before endpoint middleware).
    /// </summary>
    public static void UseArcanumCors(this WebApplication app)
    {
        app.UseCors("AllowAll");
    }

    public static void MapArcanumEndpoints(this WebApplication app)
    {
        RouteGroupBuilder openAiV1 = app.MapGroup("/v1").AddEndpointFilter<ApiKeyEndpointFilter>();

        openAiV1.MapOpenAiV1ChatCompletions();

        var apiGroup = app.MapGroup("/api").AddEndpointFilter<ApiKeyEndpointFilter>();

        apiGroup.MapOpenApi();

        apiGroup.MapScalarApiReference();

        apiGroup.MapGet("/health", (HttpContext httpContext) =>
        {
            Result<string> healthResult = "Arcanum API is online";

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            ApiResponse<string> response = ApiResponse<string>.FromResult(healthResult, traceId);

            return Results.Ok(response);
        })
        .WithName("GetHealth");

        apiGroup.MapPost("/intelligence/ping", async (PingRequest? body, IArcanumIntelligenceProvider intelligence, HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            if (body is null
                || (string.IsNullOrWhiteSpace(body.Prompt) && body.StatelessMessages is not { Count: > 0 }))
            {
                Result<string> invalid = Result<string>.Failure(new Error("Validation.InvalidPrompt", "Prompt is required unless StatelessMessages is provided."));

                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                return Results.BadRequest(ApiResponse<string>.FromResult(invalid, badTraceId));
            }

            Result<string> result = await intelligence.ExecutePromptAsync(body, cancellationToken).ConfigureAwait(false);

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            ApiResponse<string> response = ApiResponse<string>.FromResult(result, traceId);

            return result.IsSuccess
                ? Results.Ok(response)
                : Results.Json(response, ArcanumJsonContext.Default.ApiResponseString, statusCode: StatusCodes.Status500InternalServerError);
        })
        .WithName("PostIntelligencePing");

        apiGroup.MapPost(
            "/intelligence/human-response",
            async (HttpContext httpContext, IHumanPromptRegistry registry, CancellationToken cancellationToken) =>
            {
                SubmitHumanResponseRequest? body = await httpContext.Request
                    .ReadFromJsonAsync(ArcanumJsonContext.Default.SubmitHumanResponseRequest, cancellationToken)
                    .ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                if (body is null
                    || string.IsNullOrWhiteSpace(body.PromptId)
                    || string.IsNullOrWhiteSpace(body.Answer))
                {
                    Result<bool> invalid = Result<bool>.Failure(
                        new Error("Validation.InvalidHumanResponse", "promptId and answer are required."));

                    return Results.BadRequest(ApiResponse<bool>.FromResult(invalid, traceId));
                }

                bool accepted = registry.TrySubmitResponse(body.PromptId.Trim(), body.Answer);

                Result<bool> ok = Result<bool>.Success(accepted);

                return Results.Ok(ApiResponse<bool>.FromResult(ok, traceId));
            })
        .WithName("PostIntelligenceHumanResponse");

        apiGroup.MapPost("/intelligence/ping-stream", async (HttpContext httpContext, CancellationToken cancellationToken) =>
        {
            using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(
                httpContext.RequestAborted,
                cancellationToken);

            CancellationToken ct = streamCts.Token;

            PingRequest? body = await httpContext.Request.ReadFromJsonAsync(ArcanumJsonContext.Default.PingRequest, ct).ConfigureAwait(false);

            if (body is null
                || (string.IsNullOrWhiteSpace(body.Prompt) && body.StatelessMessages is not { Count: > 0 }))
            {
                Result<string> invalid = Result<string>.Failure(new Error("Validation.InvalidPrompt", "Prompt is required unless StatelessMessages is provided."));

                string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

                await httpContext.Response.WriteAsJsonAsync(ApiResponse<string>.FromResult(invalid, badTraceId), ArcanumJsonContext.Default.ApiResponseString, cancellationToken: ct).ConfigureAwait(false);

                return;
            }

            IArcanumIntelligenceProvider intelligence = httpContext.RequestServices.GetRequiredService<IArcanumIntelligenceProvider>();

            httpContext.Response.ContentType = "application/x-ndjson; charset=utf-8";

            ArrayBufferWriter<byte> eventBuffer = new(256);

            try
            {
                await foreach (IntelligenceEvent ev in intelligence.StreamPromptAsync(body, ct).ConfigureAwait(false))
                {
                    eventBuffer.ResetWrittenCount();

                    await using (Utf8JsonWriter jsonWriter = new(eventBuffer))
                    {
                        JsonSerializer.Serialize(jsonWriter, ev, ArcanumJsonContext.Default.IntelligenceEvent);
                    }

                    eventBuffer.Write(NewlineBytes);

                    await httpContext.Response.Body.WriteAsync(eventBuffer.WrittenMemory, ct).ConfigureAwait(false);

                    await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ILogger streamLogger = httpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(ApiBootstrapper));

                streamLogger.LogError(ex, "Unhandled exception in ping-stream endpoint.");

                IntelligenceEvent errorEvent = new(
                    IntelligenceEventType.Error,
                    "An internal error occurred during inference streaming.");

                eventBuffer.ResetWrittenCount();

                await using (Utf8JsonWriter jsonWriter = new(eventBuffer))
                {
                    JsonSerializer.Serialize(jsonWriter, errorEvent, ArcanumJsonContext.Default.IntelligenceEvent);
                }

                eventBuffer.Write(NewlineBytes);

                await httpContext.Response.Body.WriteAsync(eventBuffer.WrittenMemory, CancellationToken.None).ConfigureAwait(false);

                await httpContext.Response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }

        })
        .WithName("PostIntelligencePingStream");

        apiGroup.MapPost("/mcp/reload", async (PingRequest? body, McpConnectionManager mcp, HttpContext httpContext, CancellationToken ct) =>
        {
            string workingDirectory = body?.WorkingDirectory ?? string.Empty;

            await mcp.ReloadAsync(workingDirectory, ct).ConfigureAwait(false);

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result<string> ok = Result<string>.Success("MCP partitions cleared; global re-bootstrapped.");

            return Results.Ok(ApiResponse<string>.FromResult(ok, traceId));
        })
        .WithName("PostMcpReload");

        apiGroup.MapPost("/intelligence/arsenal", async (PingRequest? body, McpConnectionManager mcp, HttpContext httpContext, CancellationToken ct) =>
        {
            string workingDirectory = body?.WorkingDirectory ?? string.Empty;

            string? spellRoot = ToolHelpers.TryNormalizeWorkspace(workingDirectory, out string? root, out _)
                ? root
                : null;

            IReadOnlyList<ParsedSpell> spells = await SpellScanner.ScanAsync(spellRoot, ct).ConfigureAwait(false);

            List<string> spellNames = spells.Select(static s => s.Name).ToList();

            List<string> nativeTools = ["GetLocalSystemTime"];

            List<McpServerStatusDto> servers = await mcp.GetServerStatusesAsync(workingDirectory, ct).ConfigureAwait(false);

            WorkspaceArsenalDto dto = new(spellNames, nativeTools, servers);

            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            Result<WorkspaceArsenalDto> arsenalOk = Result<WorkspaceArsenalDto>.Success(dto);

            return Results.Ok(ApiResponse<WorkspaceArsenalDto>.FromResult(arsenalOk, traceId));
        })
        .WithName("PostIntelligenceArsenal");

        apiGroup.MapGet(
            "/perception/look",
            async (string? directory, IEyeOfTheWorld eye, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                string path = string.IsNullOrWhiteSpace(directory) ? Environment.CurrentDirectory : directory;

                string resolved = Path.GetFullPath(path);

                if (!Directory.Exists(resolved))
                {
                    string badTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<PatternSnapshot> invalid = Result<PatternSnapshot>.Failure(
                        new Error("Perception.InvalidPath", "The specified directory does not exist or is inaccessible."));

                    return Results.BadRequest(ApiResponse<PatternSnapshot>.FromResult(invalid, badTraceId));
                }

                PatternSnapshot snapshot = await eye.PerceivePatternAsync(resolved, cancellationToken).ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<PatternSnapshot> ok = Result<PatternSnapshot>.Success(snapshot);

                return Results.Ok(ApiResponse<PatternSnapshot>.FromResult(ok, traceId));
            })
        .WithName("GetPerceptionLook");

        apiGroup.MapGet(
            "/conversations",
            async (int? take, IGrimoireRepository grimoire, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                int clamped = Math.Clamp(take ?? 50, 1, 200);

                IReadOnlyList<ConversationSummaryDto> summaries =
                    await grimoire.ListRecentConversationsAsync(clamped, cancellationToken).ConfigureAwait(false);

                List<ConversationSummaryDto> list = summaries is List<ConversationSummaryDto> concrete
                    ? concrete
                    : summaries.ToList();

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<List<ConversationSummaryDto>> ok = Result<List<ConversationSummaryDto>>.Success(list);

                return Results.Ok(ApiResponse<List<ConversationSummaryDto>>.FromResult(ok, traceId));
            })
        .WithName("GetConversations");

        apiGroup.MapGet(
            "/conversations/{id:guid}",
            async (Guid id, IGrimoireRepository grimoire, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                ConversationDetailDto? detail =
                    await grimoire.GetConversationDetailAsync(id, cancellationToken).ConfigureAwait(false);

                if (detail is null)
                {
                    string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<ConversationDetailDto> notFound = Result<ConversationDetailDto>.Failure(
                        new Error("Grimoire.ConversationNotFound", "No conversation exists with that id."));

                    return Results.NotFound(ApiResponse<ConversationDetailDto>.FromResult(notFound, traceId));
                }

                string okTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<ConversationDetailDto> ok = Result<ConversationDetailDto>.Success(detail);

                return Results.Ok(ApiResponse<ConversationDetailDto>.FromResult(ok, okTraceId));
            })
        .WithName("GetConversation");

        apiGroup.MapGet(
            "/conversations/{id:guid}/messages",
            async (Guid id, IGrimoireRepository grimoire, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                List<ConversationMessageDto>? messages =
                    await grimoire.GetConversationMessagesAsync(id, cancellationToken).ConfigureAwait(false);

                if (messages is null)
                {
                    string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<List<ConversationMessageDto>> notFound = Result<List<ConversationMessageDto>>.Failure(
                        new Error("Grimoire.ConversationNotFound", "No conversation exists with that id."));

                    return Results.NotFound(ApiResponse<List<ConversationMessageDto>>.FromResult(notFound, traceId));
                }

                string okTraceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<List<ConversationMessageDto>> ok = Result<List<ConversationMessageDto>>.Success(messages);

                return Results.Ok(ApiResponse<List<ConversationMessageDto>>.FromResult(ok, okTraceId));
            })
        .WithName("GetConversationMessages");

        apiGroup.MapDelete(
            "/conversations/{id:guid}",
            async (Guid id, IGrimoireRepository grimoire, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                int removed = await grimoire.DeleteConversationAsync(id, cancellationToken).ConfigureAwait(false);

                if (removed == 0)
                {
                    string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<bool> notFound = Result<bool>.Failure(
                        new Error("Grimoire.ConversationNotFound", "No conversation exists with that id."));

                    return Results.NotFound(ApiResponse<bool>.FromResult(notFound, traceId));
                }

                return Results.NoContent();
            })
        .WithName("DeleteConversation");

        apiGroup.MapPost(
            "/conversations/{id:guid}/rest",
            async (Guid id, IGrimoireRepository grimoire, ICampaignLoggerQueue queue, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                if (!await grimoire.ConversationExistsAsync(id, cancellationToken).ConfigureAwait(false))
                {
                    string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                    Result<bool> notFound = Result<bool>.Failure(
                        new Error("Grimoire.ConversationNotFound", "No conversation exists with that id."));

                    return Results.NotFound(ApiResponse<bool>.FromResult(notFound, traceId));
                }

                await queue.QueueAsync(id, cancellationToken).ConfigureAwait(false);

                return Results.Accepted();
            })
        .WithName("PostConversationRest");

        apiGroup.MapGet(
            "/lore",
            async (IGrimoireRepository grimoire, HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                List<LoreDto> list =
                    await grimoire.ListLoreAsync(cancellationToken).ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                Result<List<LoreDto>> ok = Result<List<LoreDto>>.Success(list);

                return Results.Ok(ApiResponse<List<LoreDto>>.FromResult(ok, traceId));
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
                UpsertLoreRequest? body = await httpContext.Request
                    .ReadFromJsonAsync(ArcanumJsonContext.Default.UpsertLoreRequest, cancellationToken)
                    .ConfigureAwait(false);

                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                if (body is null
                    || string.IsNullOrWhiteSpace(body.Key)
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

                await grimoire.ScribeLoreAsync(trimmedKey, body.Value, cancellationToken).ConfigureAwait(false);

                LoreDto? saved = await grimoire.GetLoreAsync(trimmedKey, cancellationToken).ConfigureAwait(false);

                if (saved is null)
                {
                    Result<LoreDto> failed = Result<LoreDto>.Failure(
                        new Error("Grimoire.LorePersistFailed", "Lore was not found after save."));

                    return Results.Json(
                        ApiResponse<LoreDto>.FromResult(failed, traceId),
                        ArcanumJsonContext.Default.ApiResponseLoreDto,
                        statusCode: StatusCodes.Status500InternalServerError);
                }

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

                Result<bool> ok = Result<bool>.Success(removed);

                return Results.Ok(ApiResponse<bool>.FromResult(ok, traceId));
            })
        .WithName("DeleteLore");

        RouteGroupBuilder daemon = apiGroup.MapGroup("/daemon");

        daemon.MapGet(
            "/jobs",
            (IOptionsMonitor<ArcanumSettings> settings, IUnseenServantPacer pacer, HttpContext httpContext) =>
            {
                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                UnseenServantJobStatusDto[] dtos = (settings.CurrentValue.Daemon?.Jobs ?? [])
                    .Select(job => new UnseenServantJobStatusDto(
                        job.Name,
                        job.TargetSpell,
                        job.IntervalMinutes,
                        pacer.GetEffectiveInterval(job),
                        job.Enabled))
                    .ToArray();

                Result<UnseenServantJobStatusDto[]> ok = Result<UnseenServantJobStatusDto[]>.Success(dtos);

                return Results.Ok(ApiResponse<UnseenServantJobStatusDto[]>.FromResult(ok, traceId));
            })
        .WithName("GetDaemonJobs");

        daemon.MapPost(
            "/jobs/{name}/initiative",
            async (
                string name,
                HttpContext httpContext,
                IUnseenServantPacer pacer,
                IOptionsMonitor<ArcanumSettings> settings,
                CancellationToken cancellationToken) =>
            {
                string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

                AdjustInitiativeRequestDto? body = await httpContext.Request
                    .ReadFromJsonAsync(ArcanumJsonContext.Default.AdjustInitiativeRequestDto, cancellationToken)
                    .ConfigureAwait(false);

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

                UnseenServantJobStatusDto dto = new(
                    jobForInterval.Name,
                    jobForInterval.TargetSpell,
                    jobForInterval.IntervalMinutes,
                    pacer.GetEffectiveInterval(jobForInterval),
                    jobForInterval.Enabled);

                Result<UnseenServantJobStatusDto> ok = Result<UnseenServantJobStatusDto>.Success(dto);

                return Results.Ok(ApiResponse<UnseenServantJobStatusDto>.FromResult(ok, traceId));
            })
        .WithName("PostDaemonJobInitiative");
    }
}
