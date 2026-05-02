using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
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
        "IL2026",
        Justification = "ILC attributes the same OpenAPI/Mvc.Abstractions ModelMetadata path as IL2026 during Native AOT publish; registration is bounded to MapOpenApi/Scalar and minimal APIs.")]

    public static IServiceCollection AddArcanumApiServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddArcanumInfrastructure(configuration);

        services.AddSingleton<ApiKeyEndpointFilter>();

        services.AddOpenApi();

        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, ArcanumJsonContext.Default));

        services.AddScoped<OllamaApiClient>(sp =>
        {
            ArcanumSettings settings = sp.GetRequiredService<IOptions<ArcanumSettings>>().Value;

            return new OllamaApiClient(settings.Ollama.Endpoint, defaultModel: string.Empty);
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

        })
        .WithName("PostIntelligencePingStream");
    }
}
