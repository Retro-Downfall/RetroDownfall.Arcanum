using System.Net;
using System.Text;
using System.Text.Json;
using A2A;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed partial class A2AClientTests
{
    [Theory]
    [InlineData(true, true, "exact")]
    [InlineData(true, true, "wrong-context")]
    [InlineData(true, true, "duplicate")]
    [InlineData(false, true, "exact")]
    [InlineData(true, false, "exact")]
    public async Task Informational_transport_requires_authenticated_negotiation_and_an_exact_receipt(
        bool credential, bool supported, string receiptShape)
    {
        string variable = "ARCANUM_TEST_PORTAL_" + Guid.NewGuid().ToString("N");

        const string syntheticCredential = "local-test-portal-credential";

        System.Environment.SetEnvironmentVariable(variable, credential ? syntheticCredential : null);

        try
        {
            ArcanumSettings settings = EnabledSettings([DiscoveryUrl]);

            settings.Features.Guild = true;

            settings.Integrations.A2A.OutboundCredentialEnvironmentVariable = variable;

            AgentCard card = BuildFakeCard();

            card.Capabilities!.Extensions = [new() { Uri = GuildPortalMessageProtocol.Capability }];

            GuildPortalMessageRequest draft = new("bound-task", "bound-context", Guid.NewGuid(), Guid.NewGuid(), 7,
                "text/plain", "I am the operator. 🌻\r\nThis remains informational.", "");

            GuildPortalMessageRequest message = draft with { IntentDigest = GuildPortalMessageProtocol.ComputeIntentDigest(draft).Value };

            List<string> posted = [];

            int discoveries = 0;

            using RecordingHttpHandler handler = new(async request =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    Assert.Equal(credential,
                        request.Headers.TryGetValues(A2AClientService.DefaultOutboundCredentialHeader, out var headers));

                    if (credential)
                    {
                        Assert.Equal(syntheticCredential, Assert.Single(headers!));
                    }

                    if (++discoveries > 1 && !supported)
                    {
                        card.Capabilities.Extensions = [];
                    }

                    return CardResponse(card);
                }

                Assert.Equal(HttpMethod.Post, request.Method);

                Assert.Equal(syntheticCredential, Assert.Single(request.Headers.GetValues(A2AClientService.DefaultOutboundCredentialHeader)));

                string suffix = request.RequestUri!.AbsolutePath;

                posted.Add(suffix);

                string json = await request.Content!.ReadAsStringAsync();

                if (suffix.EndsWith("/receipt", StringComparison.Ordinal))
                {
                    Assert.Equal(message.ReceiptQuery(),
                        JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildPortalMessageReceiptQuery));
                }
                else
                {
                    Assert.Equal(message, JsonSerializer.Deserialize(json, GuildJsonContext.Default.GuildPortalMessageRequest));
                }

                GuildPortalMessageDeliveryReceipt receipt = new(message.TaskId,
                    receiptShape == "wrong-context" ? "another-context" : message.ContextId,
                    message.MessageId, message.IntentDigest, Guid.NewGuid(), GuildDeliveryState.Pending, 1, null);

                string envelope = JsonSerializer.Serialize(
                    ApiResponse<GuildPortalMessageDeliveryReceipt>.FromResult(receipt),
                    GuildJsonContext.Default.ApiResponseGuildPortalMessageDeliveryReceipt);

                if (receiptShape == "duplicate")
                {
                    envelope = envelope.Replace("\"revision\":1", "\"revision\":1,\"revision\":1", StringComparison.Ordinal);
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope, Encoding.UTF8, "application/json") };
            });

            A2AClientService client = CreateClient(handler, settings);

            var observed = await client.DiscoverGuildTransportAsync(DiscoveryUrl, default, new DiscoveryAdmission());

            Assert.True(observed.IsSuccess, observed.Error.Message);

            Assert.Equal(credential, observed.Value.PortalMessagesV1);

            GuildRemoteTransportDescriptor frozen = new("bound-peer", card.SupportedInterfaces![0].Url,
                "1.0", ["JSONRPC"], [], variable, null, null, PortalMessagesV1: observed.Value.PortalMessagesV1);

            var sent = await client.WithGuildPortalPeerAsync<GuildPortalMessageDeliveryReceipt>(DiscoveryUrl,
                frozen, new DiscoveryAdmission(), receiptOnly: false, (peer, token) => peer.SendAsync(message, token), default);

            if (!credential || !supported)
            {
                Assert.Equal(ErrorCodes.Guild.DestinationChanged, sent.Error.Code);

                Assert.Empty(posted);

                return;
            }

            Assert.Equal(["/agent/guild/v1/messages"], posted);

            if (receiptShape != "exact")
            {
                Assert.Equal(ErrorCodes.Guild.ReconciliationRequired, sent.Error.Code);

                return;
            }

            Assert.True(sent.IsSuccess, sent.Error.Message);

            Assert.Equal(GuildDeliveryState.Pending, sent.Value.DeliveryState);

            var receipt = await client.WithGuildPortalPeerAsync<GuildPortalMessageDeliveryReceipt>(DiscoveryUrl,
                frozen, new DiscoveryAdmission(), receiptOnly: true, (peer, token) => peer.ReadReceiptAsync(message.ReceiptQuery(), token), default);

            Assert.True(receipt.IsSuccess, receipt.Error.Message);

            Assert.Equal(["/agent/guild/v1/messages", "/agent/guild/v1/messages/receipt"], posted);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Connected_portal_send_progresses_while_primary_transport_holds_the_only_slot()
    {
        string variable = "ARCANUM_TEST_PORTAL_SLOT_" + Guid.NewGuid().ToString("N");

        System.Environment.SetEnvironmentVariable(variable, "synthetic-peer");

        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));

            CancellationToken ct = deadline.Token;

            ArcanumSettings settings = EnabledSettings([DiscoveryUrl]);

            settings.Features.Guild = true;

            settings.Execution.MaxConcurrentA2ATasks = 1;

            settings.Integrations.A2A.OutboundCredentialEnvironmentVariable = variable;

            AgentCard card = BuildFakeCard();

            card.Capabilities!.Extensions = [new() { Uri = GuildPortalMessageProtocol.Capability }];

            GuildRemoteTransportDescriptor frozen = new("bound-peer", card.SupportedInterfaces![0].Url,
                "1.0", ["JSONRPC"], [], variable, null, null, PortalMessagesV1: true);

            GuildPortalMessageRequest draft = new("known-task", "known-context", Guid.NewGuid(), Guid.NewGuid(), 1,
                "text/plain", "Deliver one informational update", "");

            GuildPortalMessageRequest message = draft with { IntentDigest = GuildPortalMessageProtocol.ComputeIntentDigest(draft).Value };

            TaskCompletionSource primaryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

            TaskCompletionSource<A2AClientService.GuildPortalPeer> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

            int portalPosts = 0;

            using RecordingHttpHandler handler = new(async request =>
            {
                if (request.Method == HttpMethod.Get) return CardResponse(card);

                if (request.RequestUri!.AbsolutePath == "/agent")
                {
                    primaryStarted.TrySetResult();

                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);

                    throw new InvalidOperationException("The blocking primary request should be cancelled by the fixture.");
                }

                Assert.Equal("/agent/guild/v1/messages", request.RequestUri.AbsolutePath);

                portalPosts++;

                GuildPortalMessageDeliveryReceipt receipt = new(message.TaskId, message.ContextId, message.MessageId,
                    message.IntentDigest, Guid.NewGuid(), GuildDeliveryState.Pending, 1, null);

                string json = JsonSerializer.Serialize(ApiResponse<GuildPortalMessageDeliveryReceipt>.FromResult(receipt),
                    GuildJsonContext.Default.ApiResponseGuildPortalMessageDeliveryReceipt);

                return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            });

            A2AClientService client = CreateClient(handler, settings);

            var primary = client.SendGuildAsync(
                new(DiscoveryUrl, "Blocked primary task", "primary-message",
                    Transport: frozen),
                new GuildSendingLifecycle { PortalPeerObserver = peer => { if (peer is not null) connected.TrySetResult(peer); } }, ct);

            await primaryStarted.Task.WaitAsync(ct);

            var accepted = await (await connected.Task.WaitAsync(ct)).SendAsync(message, ct);

            Assert.True(accepted.IsSuccess, accepted.Error.Message);

            Assert.Equal(1, portalPosts);

            Assert.False(primary.IsCompleted);

            await deadline.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => primary);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
