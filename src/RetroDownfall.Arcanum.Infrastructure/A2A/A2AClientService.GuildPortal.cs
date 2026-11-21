using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using A2A;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

public sealed partial class A2AClientService
{
    private const int MaxPortalReceiptBytes = GuildPortalMessageProtocol.MaximumReceiptQueryBytes;

    /// <summary>Opens a guarded channel without crossing the durable informational handoff boundary.</summary>
    internal Task<Result<T>> WithGuildPortalPeerAsync<T>(string destination, GuildRemoteTransportDescriptor transport,
        IA2AGuildDiscoveryAdmission discoveryAdmission, bool receiptOnly,
        Func<GuildPortalPeer, CancellationToken, Task<Result<T>>> operation, CancellationToken cancellationToken)
    {
        if (!transport.PortalMessagesV1)
        {
            return Task.FromResult(Result<T>.Failure(PortalUnavailable));
        }

        Result current = ValidateCurrentGuildDestination(destination, null, required: true, existingTask: receiptOnly);

        if (current.IsFailure)
        {
            return Task.FromResult(Result<T>.Failure(current.Error));
        }

        bool negotiated = false;

        return RunGuardedAsync<T>(destination, null, cancellationToken, async (_, card, _, _, _, token) =>
        {
            using GuildPortalPeer? peer = CreateGuildPortalPeer(destination, transport, card, negotiated, receiptOnly);

            return peer is null ? PortalUnavailable : await operation(peer, token).ConfigureAwait(false);
        }, discoveryAdmission, guildDiscovery: true, existingGuildTask: receiptOnly,
            observePortalCapability: qualified => negotiated = qualified, freshCard: true);
    }

    private GuildPortalPeer? CreateGuildPortalPeer(string destination, GuildRemoteTransportDescriptor? transport,
        AgentCard card, bool negotiated, bool receiptOnly)
    {
        if (transport is not { PortalMessagesV1: true } || !negotiated
            || !HasPortalCapability(card, destination) || ValidateGuildTarget(card, transport).IsFailure
            || !TryGetPortalEndpoint(transport.Endpoint, destination, out Uri? endpoint)
            || !HasPortalCredential(_options.CurrentValue))
        {
            return null;
        }

        return new GuildPortalPeer(this, destination, endpoint, card, receiptOnly);
    }

    internal static bool HasPortalCredential(ArcanumSettings settings)
    {
        string? name = settings.ResolveA2A().OutboundCredentialEnvironmentVariable;

        return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(name.Trim()));
    }

    private static bool HasPortalCapability(AgentCard card, string discovery) =>
        card.Capabilities?.Extensions?.Count(extension => extension?.Uri == GuildPortalMessageProtocol.Capability) == 1
        && card.SupportedInterfaces?.FirstOrDefault() is { ProtocolBinding: "JSONRPC", ProtocolVersion: "1.0" } rpc
        && TryGetPortalEndpoint(rpc.Url, discovery, out _);

    private static bool TryGetPortalEndpoint(string? endpoint, string discovery, out Uri portal)
    {
        portal = null!;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? rpc)
            || !Uri.TryCreate(discovery, UriKind.Absolute, out Uri? origin)
            || !string.Equals(rpc.GetLeftPart(UriPartial.Authority), origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            || rpc.Query.Length != 0 || rpc.Fragment.Length != 0 || rpc.UserInfo.Length != 0)
        {
            return false;
        }

        portal = rpc;

        return true;
    }

    private static Error PortalUnavailable => new(ErrorCodes.Guild.DestinationChanged,
        "The authenticated peer does not currently offer the accepted Guild informational message capability.");

    private static Error UnknownPortalReceipt => new(ErrorCodes.Guild.ReconciliationRequired,
        "The peer did not supply an authenticated informational message receipt for the exact task and intent.");

    /// <summary>Ephemeral authority to submit the exact store-admitted informational operation to this peer.</summary>
    internal sealed class GuildPortalPeer(A2AClientService owner, string destination, Uri endpoint, AgentCard card, bool receiptOnly)
        : IDisposable
    {
        private static readonly GuildJsonContext PortalWire = new(new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            AllowDuplicateProperties = false,
        });

        private int _closed;

        internal Task<Result<GuildPortalMessageDeliveryReceipt>> SendAsync(GuildPortalMessageRequest message, CancellationToken ct)
        {
            Result<string> digest = GuildPortalMessageProtocol.ComputeIntentDigest(message);

            if (digest.IsFailure || digest.Value != message.IntentDigest || receiptOnly)
            {
                return Task.FromResult(Result<GuildPortalMessageDeliveryReceipt>.Failure(
                    new(ErrorCodes.Guild.InvalidRequest, "The message does not match the admitted peer intent.")));
            }

            return ExchangeAsync(message, message.ReceiptQuery(), GuildPortalMessageProtocol.RouteSuffix,
                PortalWire.GuildPortalMessageRequest, GuildPortalMessageProtocol.MaximumRequestBytes, observation: false, ct);
        }

        internal Task<Result<GuildPortalMessageDeliveryReceipt>> ReadReceiptAsync(GuildPortalMessageReceiptQuery query, CancellationToken ct) =>
            GuildPortalMessageProtocol.ValidReceiptQuery(query)
                ? ExchangeAsync(query, query, GuildPortalMessageProtocol.ReceiptRouteSuffix,
                    PortalWire.GuildPortalMessageReceiptQuery, GuildPortalMessageProtocol.MaximumReceiptQueryBytes, observation: true, ct)
                : Task.FromResult(Result<GuildPortalMessageDeliveryReceipt>.Failure(
                    new(ErrorCodes.Guild.InvalidRequest, "The receipt query does not bind the admitted peer intent.")));

        private async Task<Result<GuildPortalMessageDeliveryReceipt>> ExchangeAsync<T>(T payload,
            GuildPortalMessageReceiptQuery expected, string suffix, JsonTypeInfo<T> schema, int requestLimit,
            bool observation, CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return PortalUnavailable;
            }

            Result current = owner.ValidateCurrentGuildDestination(destination, card, required: true, existingTask: observation);

            if (current.IsFailure)
            {
                return current.Error;
            }

            string[] allowed = owner._options.CurrentValue.ResolveA2A().AllowedRemoteAgents ?? [];

            using HttpClient http = owner.CreateOutboundClient(endpoint.AbsoluteUri, allowed, MaxPortalReceiptBytes, out bool credentialAttached);

            if (!credentialAttached)
            {
                return PortalUnavailable;
            }

            byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, schema);

            if (body.Length > requestLimit)
            {
                return new Error(ErrorCodes.Guild.PayloadTooLarge, "The informational exchange exceeds its transport allocation.");
            }

            // The suffix is closed and derived only from the vetted, frozen same-origin RPC endpoint.
            Uri target = new(endpoint.GetLeftPart(UriPartial.Authority) + endpoint.AbsolutePath.TrimEnd('/') + suffix);

            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            bounded.CancelAfter(OutboundConnectTimeout);

            using ByteArrayContent content = new(body);

            content.Headers.ContentType = new("application/json");

            try
            {
                using HttpResponseMessage response = await http.PostAsync(target, content, bounded.Token).ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxPortalReceiptBytes)
                {
                    return UnknownPortalReceipt;
                }

                await using Stream data = await response.Content.ReadAsStreamAsync(bounded.Token).ConfigureAwait(false);

                var decoded = await JsonSerializer.DeserializeAsync(data, PortalWire.ApiResponseGuildPortalMessageDeliveryReceipt, bounded.Token)
                    .ConfigureAwait(false);

                if (decoded is not { IsSuccess: true, Error: null, Data: { } receipt }
                    || receipt.TaskId != expected.TaskId || receipt.ContextId != expected.ContextId
                    || receipt.MessageId != expected.MessageId || receipt.IntentDigest != expected.IntentDigest
                    || receipt.ReceiptId == Guid.Empty || receipt.Revision < 1
                    || receipt.DeliveryState is not (GuildDeliveryState.Pending or GuildDeliveryState.Applied or GuildDeliveryState.Withheld)
                    || (receipt.DeliveryState == GuildDeliveryState.Applied
                        ? receipt.ApplicationReceiptId is null || receipt.ApplicationReceiptId == Guid.Empty
                        : receipt.ApplicationReceiptId is not null))
                {
                    return UnknownPortalReceipt;
                }

                return receipt;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return UnknownPortalReceipt;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
            {
                return UnknownPortalReceipt;
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _closed, 1);
    }
}
