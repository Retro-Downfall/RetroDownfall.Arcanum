using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Intelligence;
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

                if (ev.Type == IntelligenceEventType.Error)
                {
                    // Fix round 1, item 1: a provider that yields IntelligenceEventType.Error as an
                    // ordinary element of its own event stream — not by throwing — completes this
                    // await foreach normally, same as a genuine success. Status stays 200 (headers
                    // already sent) and the body is non-empty, so without this, PersistClaimAsync's
                    // own buffered/aborted fallback would cache the failure as a permanently
                    // replayable "success." Set eagerly, the moment the Error event is seen, rather
                    // than deferred to after the loop: if the client disconnects or the enumerator
                    // faults on a later MoveNextAsync, the decision is already made before whichever
                    // exit path runs.
                    TurnContextGuards.MarkIdempotencyNeverCache(httpContext);
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

            // Fix round 1, item 1: not calling MarkIdempotencyTerminal here (W2-1) is not by itself
            // enough — PersistClaimAsync's own buffered/aborted fallback treats any non-empty,
            // non-aborted body as terminal independent of the marker, and a client that is still
            // connected here (httpContext.RequestAborted is false in every branch that reaches this
            // point; the three early returns above cover the cases where it is true) leaves that
            // fallback free to cache this failure frame anyway. Set explicitly and unconditionally,
            // before the write attempt, so it holds even if the write itself then fails.
            TurnContextGuards.MarkIdempotencyNeverCache(httpContext);

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

            // Fix round 1, item 1: see the matching comment in the OperationCanceledException arm
            // above — a client still connected here (the common case for a provider-side fault)
            // left PersistClaimAsync's own buffered/aborted fallback free to cache this failure
            // frame regardless of whether the terminal marker was set, so this has to say so
            // explicitly.
            TurnContextGuards.MarkIdempotencyNeverCache(httpContext);

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
