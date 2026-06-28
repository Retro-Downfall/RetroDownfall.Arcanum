using Microsoft.AspNetCore.Http;
using System.Net.Http;

namespace RetroDownfall.Arcanum.Api.Streaming;

/// <summary>
/// Classifies exceptions raised by SSE/NDJSON write paths as a client disconnect so each
/// streaming endpoint can treat broken-pipe failures uniformly: cancel the linked
/// <see cref="CancellationTokenSource"/> (so inference/chronicle producers stop promptly),
/// break the write loop silently, and never write an error frame to a dead socket.
/// </summary>
/// <remarks>
/// <see cref="IOException"/> covers OS-level broken-pipe/reset-by-peer on the raw response
/// stream. <see cref="HttpIOException"/> is the ASP.NET Core wrapper for the same condition
/// on the HTTP response body. <see cref="OperationCanceledException"/> is included only when
/// the request has been aborted (<see cref="HttpContext.RequestAborted"/>) — a clean
/// cooperative cancellation that did not abort the request is handled by the caller's
/// existing <c>catch (OperationCanceledException)</c> branch and is intentionally NOT
/// classified as a disconnect here.
/// </remarks>
internal static class ClientDisconnect
{

    /// <summary>
    /// Returns <c>true</c> when <paramref name="exception"/> represents a client disconnect
    /// (broken pipe / aborted request) rather than an application fault.
    /// </summary>
    public static bool IsClientDisconnect(Exception? exception, HttpContext? httpContext = null)
    {

        if (exception is null)
        {

            return false;

        }

        if (exception is IOException or HttpIOException)
        {

            return true;

        }

        if (exception is OperationCanceledException oce)
        {

            return httpContext is { } ctx && ctx.RequestAborted.IsCancellationRequested && oce.CancellationToken == ctx.RequestAborted;

        }

        return false;

    }

}
