using System.Net;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static class ArcanumApiCredentialLeaseTestFactory
{
    private static readonly Uri PresenceUri =
        new("http://localhost:5001/api/presence");

    internal static ArcanumApiCredentialLease Create(
        string? apiKey,
        string? serverKey = null)
    {
        SecretStoreReadResult local = apiKey is null
            ? SecretStoreReadResult.Missing()
            : SecretStoreReadResult.Ok(apiKey);

        return Create(
            static _ => Task.FromResult(SecretStoreReadResult.Missing()),
            _ => Task.FromResult(local),
            serverKey ?? apiKey ?? "test-server-key");
    }

    internal static ArcanumApiCredentialLease Create(
        ISecretStore secretStore,
        string serverKey)
    {
        ArgumentNullException.ThrowIfNull(secretStore);

        return Create(
            static _ => Task.FromResult(SecretStoreReadResult.Missing()),
            cancellationToken => secretStore
                .PeekApiKeyReadResultAsync()
                .WaitAsync(cancellationToken),
            serverKey);
    }

    internal static ArcanumApiCredentialLease Create(
        Func<CancellationToken, Task<SecretStoreReadResult>> mirrorReader,
        Func<CancellationToken, Task<SecretStoreReadResult>> primaryReader,
        string serverKey)
    {
        HttpClient client = new(new PresenceHandler(serverKey))
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        return new ArcanumApiCredentialLease(
            client,
            PresenceUri,
            mirrorReader,
            primaryReader);
    }

    private sealed class PresenceHandler(string serverKey) : HttpMessageHandler
    {
        private readonly ArcanumProcessCapabilityService _processCapabilities = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string encodedNonce = request.Headers
                .GetValues(ArcanumApiHeaders.PresenceNonce)
                .Single();

            if (!ArcanumPresenceProofProtocol.TryDecode(
                    encodedNonce,
                    ArcanumPresenceProofProtocol.NonceBytes,
                    out byte[]? nonce)
                || !ArcanumPresenceProofProtocol.TryCanonicalAuthority(
                    request.RequestUri!,
                    out string? authority))
            {
                throw new InvalidOperationException(
                    "The credential-lease test request was malformed.");
            }

            byte[] encodedKey = Encoding.UTF8.GetBytes(serverKey);
            byte[] digest = SHA256.HashData(encodedKey);
            byte[] processCapability = _processCapabilities.Issue();
            byte[] capabilityEnvelope =
                ArcanumPresenceProofProtocol.CreateCapabilityEnvelope(
                    digest,
                    nonce!,
                    authority!,
                    processCapability);
            byte[] proof = ArcanumPresenceProofProtocol.ComputeProof(
                digest,
                nonce!,
                authority!,
                capabilityEnvelope);

            try
            {
                HttpResponseMessage response = new(HttpStatusCode.NoContent);

                response.Headers.TryAddWithoutValidation(
                    ArcanumApiHeaders.PresenceVersion,
                    ArcanumPresenceProofProtocol.Version);

                response.Headers.TryAddWithoutValidation(
                    ArcanumApiHeaders.PresenceAuthority,
                    authority);

                response.Headers.TryAddWithoutValidation(
                    ArcanumApiHeaders.PresenceProof,
                    ArcanumPresenceProofProtocol.Encode(proof));

                response.Headers.TryAddWithoutValidation(
                    ArcanumApiHeaders.PresenceCapability,
                    ArcanumPresenceProofProtocol.Encode(capabilityEnvelope));

                return Task.FromResult(response);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(nonce!);
                CryptographicOperations.ZeroMemory(encodedKey);
                CryptographicOperations.ZeroMemory(digest);
                CryptographicOperations.ZeroMemory(processCapability);
                CryptographicOperations.ZeroMemory(capabilityEnvelope);
                CryptographicOperations.ZeroMemory(proof);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _processCapabilities.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
