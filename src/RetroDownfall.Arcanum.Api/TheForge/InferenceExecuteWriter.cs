using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class InferenceExecuteWriter
{

    /// <summary>
    /// Client-visible NDJSON Error text for wall-clock inference timeout (aligned with
    /// WizardIntelligenceProvider.PublicInferenceTimeoutMessage / Hub.Timeout).
    /// </summary>
    internal const string PublicStreamTimeoutMessage =
        "Inference timed out. Increase Arcanum:Intelligence:InferenceTimeoutSeconds or retry with a shorter prompt.";

    /// <summary>
    /// Client-visible NDJSON Error text for caught streaming exceptions (not intentional
    /// provider Error events). Keep in sync with WizardIntelligenceProvider.PublicInferenceFailureMessage.
    /// </summary>
    internal const string PublicStreamFailureMessage =
        "Inference failed. Ensure the provider is running and reachable, then try again. See server logs for details.";

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

        ILogger logger = httpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(InferenceExecuteWriter).FullName ?? nameof(InferenceExecuteWriter));

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
            // DESIGN / WizardIntelligenceProvider cancellation rule:
            // 1) Client abort (RequestAborted) → stop cleanly (no error frame).
            // 2) Inference wall-clock timeout (!caller cancel) → Hub.Timeout frame.
            // 3) Host/caller cancellation → sanitized failure frame (not labeled timeout).
            if (httpContext.RequestAborted.IsCancellationRequested)
            {
                return;
            }

            string publicMessage = cancellationToken.IsCancellationRequested
                ? PublicStreamFailureMessage
                : PublicStreamTimeoutMessage;

            try
            {
                IntelligenceEvent cancelEvent = new(
                    IntelligenceEventType.Error,
                    publicMessage);

                eventBuffer.ResetWrittenCount();
                jsonWriter.Reset();
                JsonSerializer.Serialize(jsonWriter, cancelEvent, ArcanumJsonContext.Default.IntelligenceEvent);
                eventBuffer.Write(NewlineBytes);
                await httpContext.Response.Body.WriteAsync(eventBuffer.WrittenMemory, CancellationToken.None)
                    .ConfigureAwait(false);
                await httpContext.Response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception writeEx) when (ClientDisconnect.IsClientDisconnect(writeEx, httpContext))
            {
            }
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
            // Full exception detail stays in logs; the client frame is sanitized (W3.5).

            logger.LogError(
                ex,
                "Stream inference failed (responseStarted={ResponseStarted}, model={Model}).",
                responseStarted,
                request.Model);

            // ServeCommand clears MEL providers; Serilog is the durable sink for operators.
            Serilog.Log.Error(
                ex,
                "Stream inference failed (responseStarted={ResponseStarted}, model={Model}).",
                responseStarted,
                request.Model);

            IntelligenceEvent errorEvent = new(
                IntelligenceEventType.Error,
                PublicStreamFailureMessage);

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

                logger.LogWarning(writeEx, "Failed to write stream error frame after inference failure.");

            }

        }
        finally
        {

            jsonWriter.Dispose();

        }
    }

}
