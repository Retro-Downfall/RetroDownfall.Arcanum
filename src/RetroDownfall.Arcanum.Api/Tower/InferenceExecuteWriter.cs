using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Primitives;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Streaming;
using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Api.Tower;

internal static class InferenceExecuteWriter
{

    /// <summary>
    /// Client-visible NDJSON Error text for caught streaming exceptions (not intentional
    /// provider Error events). Uses the centralized native inference-failure contract.
    /// </summary>
    internal const string PublicStreamFailureMessage =
        PublicInferenceErrorMessages.NativeGenericFailure;

    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    public static async Task WriteBufferedAsync(
        HttpContext httpContext,
        IArcanumIntelligenceProvider intelligence,
        PingRequest request,
        CancellationToken cancellationToken,
        CanonicalCampaignContext? campaign = null)
    {
        Result<PromptTurnResult> turn = await intelligence
            .ExecutePromptAsync(
                request,
                ArcanumInvocationContexts.ForTurn(httpContext, request, campaign),
                cancellationToken)
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
        InferenceAuditContext? auditContext = null,
        CanonicalCampaignContext? campaign = null)
    {
        // Prefer RequestServices in production; tolerate null/partial providers in unit tests.
        IServiceProvider? services = httpContext.RequestServices;

        DisconnectPolicy disconnectPolicy = services
            ?.GetService<IOptionsSnapshot<ArcanumSettings>>()
            ?.Value.ResolveIntelligence().DisconnectPolicy
            ?? DisconnectPolicy.Auto;

        bool continueThenReplay = TurnContextGuards.ResolveContinueThenReplay(httpContext, disconnectPolicy);
        CancellationToken ownershipLost = TurnIdempotencyAmbient.OwnershipLostToken;

        using CancellationTokenSource streamCts = continueThenReplay
            ? CancellationTokenSource.CreateLinkedTokenSource(ownershipLost)
            : CancellationTokenSource.CreateLinkedTokenSource(
                httpContext.RequestAborted,
                cancellationToken,
                ownershipLost);

        CancellationToken ct = streamCts.Token;

        httpContext.Response.ContentType = "application/x-ndjson; charset=utf-8";

        // Never a bare assignment; see SseStreamWriter.PrepareResponse. A protected stream keeps
        // its exact private tuple, and an ordinary one keeps the shipped streaming default.
        CovenantProtectedResponseHeaders.ApplyStreamingDefaultWithoutWeakening(httpContext);

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
            await foreach (IntelligenceEvent ev in intelligence.StreamPromptAsync(
                request,
                ArcanumInvocationContexts.ForTurn(httpContext, request, campaign),
                ct,
                auditContext).ConfigureAwait(false))
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
            if (ownershipLost.IsCancellationRequested)
            {
                return;
            }

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
        // Direction matters here. The write-side catches above already absorb a broken pipe on the
        // response socket; anything reaching this whole-pipeline catch came out of the producer's
        // MoveNextAsync. An IOException/HttpIOException raised there while the client socket is
        // still healthy is a PROVIDER fault, and the caller is owed the terminal Error frame the
        // general catch below writes — so only classify it as a disconnect when the client really
        // is gone.
        catch (Exception ex) when (
            ClientDisconnect.IsClientDisconnect(ex, httpContext)
            && (clientGone || httpContext.RequestAborted.IsCancellationRequested))
        {
            if (!continueThenReplay)
            {
                streamCts.Cancel();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                "Stream inference failed with exception type {ExceptionType} (responseStarted={ResponseStarted}, trace={TraceId}).",
                ex.GetType().FullName,
                responseStarted,
                httpContext.TraceIdentifier);

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
                logger.LogWarning(
                    "Failed to write stream error frame after inference failure; exception type {ExceptionType}, trace {TraceId}.",
                    writeEx.GetType().FullName,
                    httpContext.TraceIdentifier);
            }

        }
        finally
        {
            jsonWriter.Dispose();
        }
    }

}
