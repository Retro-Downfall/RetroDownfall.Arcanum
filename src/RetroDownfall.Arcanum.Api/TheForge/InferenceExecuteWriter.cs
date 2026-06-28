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

        httpContext.Response.StatusCode = InferenceErrorMapper.ResolveStatusCode(turn.Error.Code);

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
        CancellationToken cancellationToken)
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

        // W3.4 Group A (S10): track whether any NDJSON frame has been streamed to the
        // response body. This mirrors HttpResponse.HasStarted (which Kestrel sets on the
        // first body write) but is host-agnostic — DefaultHttpContext does not flip
        // HasStarted on direct body writes, and the decision here is precisely "have we
        // already streamed frames" rather than "have HTTP headers been sent". Once true,
        // a late non-disconnect exception must NOT append an error frame: the frame would
        // be ambiguous to a client that has already consumed partial output and may target
        // a dead socket.
        bool responseStarted = false;

        try
        {
            await foreach (IntelligenceEvent ev in intelligence.StreamPromptAsync(request, ct).ConfigureAwait(false))
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

            // W3.4 Group A (S10): client disconnected mid-stream (broken pipe / reset).
            // Cancel the linked inference CTS so the producer stops promptly, then break
            // silently. No error frame is written to the dead socket.

            streamCts.Cancel();

        }
        catch (Exception ex)
        {

            // W3.4 Group A (S10): only emit an error frame when nothing has been streamed
            // yet. Once the NDJSON response has started (tokens already sent), appending an
            // error frame is ambiguous to the client and may target a dead socket.

            if (!responseStarted)
            {

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

                catch (Exception writeEx)
                {

                    Debug.WriteLine($"Failed to write stream error frame: {writeEx.Message}");

                }

            }

            if (ex is not OperationCanceledException)
            {

                Debug.WriteLine($"Stream inference failed: {ex.Message}");

            }

        }
        finally
        {

            jsonWriter.Dispose();

        }
    }

}
