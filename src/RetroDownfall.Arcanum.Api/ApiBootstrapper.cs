using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaSharp;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
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

        services.AddSingleton<ApiKeyEndpointFilter>();

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

    public static void MapArcanumEndpoints(this WebApplication app)
    {
        app.MapOpenApi();

        app.MapScalarApiReference();

        var apiGroup = app.MapGroup("/api").AddEndpointFilter<ApiKeyEndpointFilter>();

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
            if (body is null || string.IsNullOrWhiteSpace(body.Prompt))
            {
                Result<string> invalid = Result<string>.Failure(new Error("Validation.InvalidPrompt", "Prompt is required."));

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

            if (body is null || string.IsNullOrWhiteSpace(body.Prompt))
            {
                Result<string> invalid = Result<string>.Failure(new Error("Validation.InvalidPrompt", "Prompt is required."));

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
                IntelligenceEvent errorEvent = new(IntelligenceEventType.Error, ex.Message);

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
    }
}
