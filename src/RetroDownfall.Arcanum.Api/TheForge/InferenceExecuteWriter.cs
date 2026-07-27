using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.TheForge;

internal static class InferenceExecuteWriter
{

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
            : Result<PromptResponseDto>.Success(PromptResponseDto.From(turn.Value));

        ApiResponse<PromptResponseDto> response = ApiResponse<PromptResponseDto>.FromResult(envelopeResult, traceId);

        if (turn.IsSuccess)
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;

            await JsonSerializer.SerializeAsync(
                httpContext.Response.Body,
                response,
                ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
                cancellationToken).ConfigureAwait(false);

            TurnContextGuards.MarkIdempotencyTerminal(httpContext);

            return;
        }

        httpContext.Response.StatusCode = ArcanumErrorMapper.ResolveStatusCode(turn.Error.Code);

        await JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            response,
            ArcanumJsonContext.Default.ApiResponsePromptResponseDto,
            cancellationToken).ConfigureAwait(false);

        TurnContextGuards.MarkIdempotencyTerminal(httpContext);
    }

    public static async Task WriteStreamAsync(
        HttpContext httpContext,
        IArcanumIntelligenceProvider intelligence,
        PingRequest request,
        CancellationToken cancellationToken,
        InferenceAuditContext? auditContext = null)
    {
        // Prefer RequestServices in production; tolerate null/partial providers in unit tests.
        IServiceProvider? services = httpContext.RequestServices;

        DisconnectPolicy disconnectPolicy = services
            ?.GetService<IOptionsSnapshot<ArcanumSettings>>()
            ?.Value.ResolveIntelligence().DisconnectPolicy
            ?? DisconnectPolicy.Auto;

        bool continueThenReplay = TurnContextGuards.ResolveContinueThenReplay(httpContext, disconnectPolicy);

        using CancellationTokenSource streamCts = continueThenReplay
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted, cancellationToken);

        CancellationToken ct = streamCts.Token;

        httpContext.Response.ContentType = "application/x-ndjson; charset=utf-8";

        httpContext.Response.Headers.CacheControl = "no-cache";

        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        ArrayBufferWriter<byte> eventBuffer = new(256);

        Utf8JsonWriter jsonWriter = new(eventBuffer);

        bool responseStarted = false;

        bool clientGone = false;

        string loggerCategory = typeof(InferenceExecuteWriter).FullName ?? nameof(InferenceExecuteWriter);

        ILogger logger = services
            ?.GetService<ILoggerFactory>()
            ?.CreateLogger(loggerCategory)
            ?? NullLoggerFactory.Instance.CreateLogger(loggerCategory);

        try
        {
            await foreach (IntelligenceEvent ev in intelligence.StreamPromptAsync(request, ct, auditContext).ConfigureAwait(false))
            {
                if (clientGone)
                {
                    // Continue-then-replay: drain remaining events without writing.
                    continue;
                }

                eventBuffer.ResetWrittenCount();

                jsonWriter.Reset();

                JsonSerializer.Serialize(jsonWriter, ev, ArcanumJsonContext.Default.IntelligenceEvent);

                eventBuffer.Write(NewlineBytes);

                try
                {
                    await httpContext.Response.Body.WriteAsync(eventBuffer.WrittenMemory, ct).ConfigureAwait(false);

                    await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);

                    responseStarted = true;
                }
                catch (Exception writeEx) when (ClientDisconnect.IsClientDisconnect(writeEx, httpContext))
                {
                    clientGone = true;

                    if (!continueThenReplay)
                    {
                        streamCts.Cancel();
                        break;
                    }
                }

            }

            if (!clientGone || continueThenReplay)
            {
                TurnContextGuards.MarkIdempotencyTerminal(httpContext);
            }

        }
        catch (OperationCanceledException)
        {
            if (httpContext.RequestAborted.IsCancellationRequested && !continueThenReplay)
            {
                return;
            }

            if (continueThenReplay && httpContext.RequestAborted.IsCancellationRequested)
            {
                // Inference CTS was not linked to RequestAborted; treat as clean finish if producer ended.
                return;
            }

            try
            {
                IntelligenceEvent cancelEvent = new(
                    IntelligenceEventType.Error,
                    PublicStreamFailureMessage);

                eventBuffer.ResetWrittenCount();
                jsonWriter.Reset();
                JsonSerializer.Serialize(jsonWriter, cancelEvent, ArcanumJsonContext.Default.IntelligenceEvent);
                eventBuffer.Write(NewlineBytes);
                await httpContext.Response.Body.WriteAsync(eventBuffer.WrittenMemory, CancellationToken.None)
                    .ConfigureAwait(false);
                await httpContext.Response.Body.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                TurnContextGuards.MarkIdempotencyTerminal(httpContext);
            }
            catch (Exception writeEx) when (ClientDisconnect.IsClientDisconnect(writeEx, httpContext))
            {
            }
        }
        catch (Exception ex) when (ClientDisconnect.IsClientDisconnect(ex, httpContext))
        {
            if (!continueThenReplay)
            {
                streamCts.Cancel();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Stream inference failed (responseStarted={ResponseStarted}, model={Model}).",
                responseStarted,
                request.Model);

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

                TurnContextGuards.MarkIdempotencyTerminal(httpContext);
            }
            catch (Exception writeEx) when (ClientDisconnect.IsClientDisconnect(writeEx, httpContext))
            {
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
