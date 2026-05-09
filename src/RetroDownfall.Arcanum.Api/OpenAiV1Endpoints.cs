using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api;

internal static class OpenAiV1Endpoints
{

    private static readonly byte[] SseDataPrefix = "data: "u8.ToArray();

    private static readonly byte[] SseLineBreak = "\n\n"u8.ToArray();

    private static readonly byte[] SseDone = "data: [DONE]\n\n"u8.ToArray();

    internal static void MapOpenAiV1ChatCompletions(this RouteGroupBuilder v1)
    {
        _ = v1.MapPost("/chat/completions", HandleChatCompletionsAsync).WithName("PostOpenAiChatCompletions");
    }

    private static async Task<IResult> HandleChatCompletionsAsync(
        HttpContext httpContext,
        IArcanumIntelligenceProvider intelligence,
        IOptions<ArcanumSettings> settings,
        CancellationToken cancellationToken)
    {
        OpenAiChatRequest? body = await httpContext.Request
            .ReadFromJsonAsync(ArcanumJsonContext.Default.OpenAiChatRequest, cancellationToken)
            .ConfigureAwait(false);

        if (body is null || body.Messages is null || body.Messages.Count == 0)
        {
            return Results.Json(
                new OpenAiErrorResponse(new OpenAiErrorDetail("messages is required and must be non-empty.", "invalid_request_error")),
                ArcanumJsonContext.Default.OpenAiErrorResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        foreach (OpenAiChatMessage m in body.Messages)
        {
            if (string.IsNullOrWhiteSpace(m.Role))
            {
                return Results.Json(
                    new OpenAiErrorResponse(new OpenAiErrorDetail("Each message must include a non-empty role.", "invalid_request_error")),
                    ArcanumJsonContext.Default.OpenAiErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        PingRequest ping = OpenAiChatCompletionMapper.ToPingRequest(body);

        string resolvedModel = !string.IsNullOrWhiteSpace(body.Model)
            ? body.Model.Trim()
            : settings.Value.Ollama.DefaultModel;

        if (string.IsNullOrWhiteSpace(resolvedModel))
        {
            return Results.Json(
                new OpenAiErrorResponse(new OpenAiErrorDetail("No model configured.", "invalid_request_error")),
                ArcanumJsonContext.Default.OpenAiErrorResponse,
                statusCode: StatusCodes.Status400BadRequest);
        }

        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        string completionId = "chatcmpl-" + Guid.NewGuid().ToString("N");

        if (!body.Stream)
        {
            Result<string> result = await intelligence.ExecutePromptAsync(ping, cancellationToken).ConfigureAwait(false);

            if (result.IsFailure)
            {
                string errMsg = string.IsNullOrWhiteSpace(result.Error.Message)
                    ? "Inference failed."
                    : result.Error.Message;

                return Results.Json(
                    new OpenAiErrorResponse(new OpenAiErrorDetail(errMsg, "api_error")),
                    ArcanumJsonContext.Default.OpenAiErrorResponse,
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            OpenAiChatResponse response = new(
                completionId,
                "chat.completion",
                created,
                resolvedModel,
                [
                    new OpenAiChatChoice(
                        0,
                        new OpenAiChatAssistantMessage("assistant", result.Value),
                        "stop"),
                ],
                new OpenAiUsage(0, 0, 0));

            return Results.Json(response, ArcanumJsonContext.Default.OpenAiChatResponse);
        }

        httpContext.Response.ContentType = "text/event-stream; charset=utf-8";

        httpContext.Response.Headers.CacheControl = "no-cache";

        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(
            httpContext.RequestAborted,
            cancellationToken);

        CancellationToken ct = streamCts.Token;

        try
        {
            await foreach (IntelligenceEvent ev in intelligence.StreamPromptAsync(ping, ct).ConfigureAwait(false))
            {
                if (ev.Type == IntelligenceEventType.Token && !string.IsNullOrEmpty(ev.Data))
                {
                    OpenAiChatChunk chunk = new(
                        completionId,
                        "chat.completion.chunk",
                        created,
                        resolvedModel,
                        [new OpenAiChatStreamChoice(0, new OpenAiDelta(ev.Data), null)]);

                    await WriteSseJsonAsync(httpContext, chunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct)
                        .ConfigureAwait(false);
                }
                else if (ev.Type == IntelligenceEventType.Error)
                {
                    OpenAiChatChunk errChunk = new(
                        completionId,
                        "chat.completion.chunk",
                        created,
                        resolvedModel,
                        [new OpenAiChatStreamChoice(0, new OpenAiDelta(ev.Message), "stop")]);

                    await WriteSseJsonAsync(httpContext, errChunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct)
                        .ConfigureAwait(false);

                    await httpContext.Response.Body.WriteAsync(SseDone, ct).ConfigureAwait(false);

                    await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);

                    return Results.Empty;
                }
            }

            OpenAiChatChunk finalChunk = new(
                completionId,
                "chat.completion.chunk",
                created,
                resolvedModel,
                [
                    new OpenAiChatStreamChoice(
                        0,
                        new OpenAiDelta(string.Empty),
                        "stop"),
                ]);

            await WriteSseJsonAsync(httpContext, finalChunk, ArcanumJsonContext.Default.OpenAiChatChunk, ct)
                .ConfigureAwait(false);

            await httpContext.Response.Body.WriteAsync(SseDone, ct).ConfigureAwait(false);

            await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return Results.Empty;
    }

    private static async Task WriteSseJsonAsync<T>(
        HttpContext httpContext,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> buffer = new(512);

        await httpContext.Response.Body.WriteAsync(SseDataPrefix, cancellationToken).ConfigureAwait(false);

        await using (Utf8JsonWriter jsonWriter = new(buffer, new JsonWriterOptions { Indented = false }))
        {
            JsonSerializer.Serialize(jsonWriter, value, typeInfo);
        }

        await httpContext.Response.Body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);

        await httpContext.Response.Body.WriteAsync(SseLineBreak, cancellationToken).ConfigureAwait(false);

        await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

}
