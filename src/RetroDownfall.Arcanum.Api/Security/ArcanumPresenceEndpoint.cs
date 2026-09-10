using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>
/// Maps the minimal anonymous challenge that lets a local client authenticate the server before it
/// transmits the master API key. The response contains no health, configuration, or installation
/// data; only a version, the actual listener authority, and a fresh-nonce-bound proof.
/// </summary>
internal static class ArcanumPresenceEndpoint
{
    internal static RouteHandlerBuilder MapArcanumPresenceEndpoint(
        this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(
                ArcanumPresenceProofProtocol.Path,
                CreateProof)
            .WithName("GetArcanumPresenceProof")
            .WithMetadata(InstallationResetRecoveryApiRouteMetadata.Presence)
            .WithMetadata(GrimoireAdmissionExemptRouteMetadata.Instance);

    private static IResult CreateProof(
        HttpContext httpContext,
        IApiKeyDigestCache digestCache,
        ArcanumProcessCapabilityService processCapabilities)
    {
        if (!ArcanumTransportPeer.IsLoopback(httpContext))
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers.CacheControl = "no-store";

        StringValues nonceValues = httpContext.Request.Headers[
            ArcanumApiHeaders.PresenceNonce];

        if (nonceValues.Count != 1
            || !ArcanumPresenceProofProtocol.TryDecode(
                nonceValues[0],
                ArcanumPresenceProofProtocol.NonceBytes,
                out byte[]? nonce))
        {
            return Results.BadRequest();
        }

        using PresenceProofBuffers buffers = new(nonce!);

        if (!TryResolveAuthority(httpContext, out string? authority))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!digestCache.TryGetPresenceDigest(out byte[]? keyDigest)
            || keyDigest is null
            || keyDigest.Length != ArcanumPresenceProofProtocol.KeyDigestBytes)
        {
            if (keyDigest is not null)
            {
                CryptographicOperations.ZeroMemory(keyDigest);
            }

            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        buffers.KeyDigest = keyDigest;

        buffers.ProcessCapability = processCapabilities.Issue();

        buffers.CapabilityEnvelope =
            ArcanumPresenceProofProtocol.CreateCapabilityEnvelope(
                buffers.KeyDigest,
                buffers.Nonce,
                authority!,
                buffers.ProcessCapability);

        byte[] proof = ArcanumPresenceProofProtocol.ComputeProof(
            buffers.KeyDigest,
            buffers.Nonce,
            authority!,
            buffers.CapabilityEnvelope);

        buffers.Proof = proof;

        httpContext.Response.Headers[ArcanumApiHeaders.PresenceVersion] =
            ArcanumPresenceProofProtocol.Version;

        httpContext.Response.Headers[ArcanumApiHeaders.PresenceAuthority] =
            authority;

        httpContext.Response.Headers[ArcanumApiHeaders.PresenceProof] =
            ArcanumPresenceProofProtocol.Encode(buffers.Proof);

        httpContext.Response.Headers[ArcanumApiHeaders.PresenceCapability] =
            ArcanumPresenceProofProtocol.Encode(buffers.CapabilityEnvelope);

        return Results.NoContent();
    }

    private static bool TryResolveAuthority(
        HttpContext httpContext,
        out string? authority)
    {
        authority = null;

        IConnectionSocketFeature? socketFeature =
            httpContext.Features.Get<IConnectionSocketFeature>();

        EndPoint? localEndPoint;

        try
        {
            localEndPoint = socketFeature is null
                ? httpContext.Features
                    .Get<IConnectionEndPointFeature>()?
                    .LocalEndPoint
                : socketFeature.Socket.LocalEndPoint;
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException
                or SocketException)
        {
            return false;
        }

        if (localEndPoint is not IPEndPoint { Port: > 0 } listener)
        {
            return false;
        }

        string scheme = httpContext.Features.Get<ITlsConnectionFeature>() is null
            ? Uri.UriSchemeHttp
            : Uri.UriSchemeHttps;

        Uri uri = new UriBuilder(
            scheme,
            "localhost",
            listener.Port).Uri;

        return ArcanumPresenceProofProtocol.TryCanonicalAuthority(
            uri,
            out authority);
    }

    private sealed class PresenceProofBuffers(byte[] nonce) : IDisposable
    {
        internal byte[] Nonce { get; } = nonce;

        internal byte[] KeyDigest { get; set; } = [];

        internal byte[] Proof { get; set; } = [];

        internal byte[] ProcessCapability { get; set; } = [];

        internal byte[] CapabilityEnvelope { get; set; } = [];

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Nonce);
            CryptographicOperations.ZeroMemory(KeyDigest);
            CryptographicOperations.ZeroMemory(Proof);
            CryptographicOperations.ZeroMemory(ProcessCapability);
            CryptographicOperations.ZeroMemory(CapabilityEnvelope);
        }
    }
}
