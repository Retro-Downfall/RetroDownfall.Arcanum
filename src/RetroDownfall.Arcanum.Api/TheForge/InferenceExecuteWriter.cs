using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class InferenceExecuteWriter
{

    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    public static async Task WriteBufferedAsync(
        HttpContext httpContext,
        IArcanumIntelligenceProvider intelligence,
        PingRequest request,
        CancellationToken cancellationToken)
    {
        Result<PromptTurnResult> turn = await intelligence
            .ExecutePromptAsync(request, cancellationToken)
            .ConfigureAwait(false);

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        Result<PromptResponseDto> envelopeResult = turn.IsFailure
            ? Result<PromptResponseDto>.Failure(turn.Error)
            : Result<PromptResponseDto>.Success(new PromptResponseDto(
                turn.Value.Text,
                turn.Value.Usage,
                turn.Value.ToolCalls,
                turn.Value.FinishReason));

        ApiResponse<PromptResponseDto> response = ApiResponse<PromptResponseDto>.FromResult(envelopeResult, traceId);

        if (turn.IsSuccess)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;

            await JsonSerializer.SerializeAsync(
                httpContext.Response.Body,
                response,
                ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        httpContext.Response.StatusCode = ArcanumErrorMapper.ResolveStatusCode(turn.Error.Code);

        await JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            response,
            ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteStreamAsync(
        HttpContext httpContext,
        IArcanumIntelligenceProvider intelligence,
        PingRequest request,
        CancellationToken cancellationToken,
        InferenceAuditContext? auditContext = null)
    {
        using CancellationTokenSource streamCts = CancellationTokenSource.CreateLinkedTokenSource(
            httpContext.RequestAborted,
            cancellationToken);

        CancellationToken ct = streamCts.Token;

        httpContext.Response.ContentType = "application/x-ndjson; charset=utf-8";

        httpContext.Response.Headers.CacheControl = "no-cache";

        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        ArrayBufferWriter<byte> eventBuffer = new(256);

        Utf8JsonWriter jsonWriter = new(eventBuffer);

        // Track whether any NDJSON frame has been streamed. Mid-stream exceptions still
        // emit a terminal IntelligenceEventType.Error when the client is writable (wire
        // contract). Client disconnects are handled separately and must not write to a
        // dead socket.
        bool responseStarted = false;

        try
        {
            await foreach (IntelligenceEvent ev in intelligence.StreamPromptAsync(request, ct, auditContext).ConfigureAwait(false))
            {

                eventBuffer.ResetWrittenCount();

                jsonWriter.Reset();

                JsonSerializer.Serialize(jsonWriter, ev, ArcanumJsonContext.Default.IntelligenceEvent);

                eventBuffer.Write(NewlineBytes);

                await httpContext.Response.Body.WriteAsync(eventBuffer.WrittenMemory, ct).ConfigureAwait(false);

                await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);

                responseStarted = true;

            }

        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception ex) when (ClientDisconnect.IsClientDisconnect(ex, httpContext))
        {

            // Client disconnected mid-stream (broken pipe / reset). Cancel the linked
            // inference CTS so the producer stops promptly, then break silently.

            streamCts.Cancel();

        }
        catch (Exception ex)
        {

            // Emit a terminal error frame whenever the client is still writable — including
            // after partial output — so native NDJSON clients always observe a terminal Error.

            IntelligenceEvent errorEvent = new(
                IntelligenceEventType.Error,
                "An internal error occurred during inference streaming.");

            eventBuffer.ResetWrittenCount();

            jsonWriter.Reset();

            JsonSerializer.Serialize(jsonWriter, errorEvent, ArcanumJsonContext.Default.IntelligenceEvent);

            eventBuffer.Write(NewlineBytes);

            try
            {

                await httpContext.Response.Body.WriteAsync(eventBuffer.WrittenMemory, httpContext.RequestAborted).ConfigureAwait(false);

                await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);

            }

            catch (Exception writeEx) when (ClientDisconnect.IsClientDisconnect(writeEx, httpContext))
            {

                // Disconnect while writing the terminal error — swallow as disconnect.

            }

            catch (Exception writeEx)
            {

                Debug.WriteLine($"Failed to write stream error frame: {writeEx.Message}");

            }

            if (ex is not OperationCanceledException)
            {

                Debug.WriteLine($"Stream inference failed (responseStarted={responseStarted}): {ex.Message}");

            }

        }
        finally
        {

            jsonWriter.Dispose();

        }
    }

}
