using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed partial class A2AClientTests
{
    [Fact]
    public async Task Guild_card_body_finishes_inside_its_physical_discovery_admission()
    {
        using HttpResponseMessage card = CardResponse(BuildFakeCard());

        byte[] bytes = await card.Content.ReadAsByteArrayAsync();

        bool admitted = false;

        int admissions = 0;

        DiscoveryAdmissionContent content = new(bytes, () => admitted);

        using RecordingHttpHandler handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        ArcanumSettings settings = EnabledSettings([DiscoveryUrl]);

        settings.Features.Guild = true;

        DiscoveryAdmissionDns dns = new(DeterministicDns(), () => admitted);

        DiscoveryAdmissionClientFactory factory = new(handler, () => admitted);

        A2AClientService client = new(factory, new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<A2AClientService>.Instance, dns);

        var result = await client.DiscoverGuildCardAsync(DiscoveryUrl, default, new DiscoveryAdmission(() =>
        {
            admissions++;

            admitted = true;
        }, () => admitted = false));

        Assert.True(result.IsSuccess, result.Error.Message);

        Assert.True(content.ReadUnderAdmission);

        Assert.True(content.DisposedUnderAdmission);

        Assert.Equal((true, true), Assert.Single(factory.CardLifetimes));

        Assert.True(admissions >= 3, "Discovery DNS, the card response, and advertised interface DNS require separate admissions.");

        Assert.True(dns.AllRequestsAdmitted);

        Assert.True(dns.Requests >= 2);
    }

    [Fact]
    public async Task Guild_card_resolution_requires_physical_admission_before_network()
    {
        RecordingHttpHandler handler = new(_ => Task.FromResult(CardResponse(BuildFakeCard())));

        ArcanumSettings settings = EnabledSettings([DiscoveryUrl]);

        settings.Features.Guild = true;

        var result = await CreateClient(handler, settings).DiscoverGuildCardAsync(DiscoveryUrl, default);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Guild.InvalidRequest, result.Error.Code);

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Guild_card_client_is_disposed_inside_the_attempt_on_http_failure_or_cancellation(bool cancelled)
    {
        using CancellationTokenSource stop = new();

        using RecordingHttpHandler handler = new(_ =>
        {
            if (!cancelled) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));

            stop.Cancel();

            return Task.FromCanceled<HttpResponseMessage>(stop.Token);
        });

        ArcanumSettings settings = EnabledSettings([DiscoveryUrl]);

        settings.Features.Guild = true;

        bool admitted = false;

        DiscoveryAdmissionClientFactory factory = new(handler, () => admitted);

        A2AClientService client = new(factory, new TestOptionsMonitor<ArcanumSettings>(settings),
            NullLogger<A2AClientService>.Instance, DeterministicDns());

        DiscoveryAdmission gate = new(() => admitted = true, () => admitted = false);

        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DiscoverGuildCardAsync(DiscoveryUrl, stop.Token, gate));

            Assert.Single(handler.Requests);

            Assert.Single(factory.CardLifetimes);
        }
        else
        {
            var result = await client.DiscoverGuildCardAsync(DiscoveryUrl, stop.Token, gate);

            Assert.True(result.IsFailure);

            Assert.Equal(ErrorCodes.Sending.AgentCardInvalid, result.Error.Code);

            Assert.Equal(2, handler.Requests.Count);

            Assert.Equal(2, factory.CardLifetimes.Count);
        }

        Assert.All(factory.CardLifetimes, static lifetime => Assert.Equal((true, true), lifetime));
    }

    [Fact]
    public async Task Guild_interface_DNS_rechecks_the_actual_destination_inside_its_admission()
    {
        const string withdrawn = "http://withdrawn-interface.example/agent";

        var card = BuildFakeCard();

        card.SupportedInterfaces![0].Url = withdrawn;

        RecordingHttpHandler handler = new(_ => Task.FromResult(CardResponse(card)));

        ArcanumSettings settings = EnabledSettings([DiscoveryUrl, withdrawn]);

        settings.Features.Guild = true;

        DiscoveryDestinationDns dns = new();

        int admissions = 0;

        var result = await CreateClient(handler, settings, dns: dns).DiscoverGuildCardAsync(DiscoveryUrl, default,
            new DiscoveryAdmission(() =>
            {
                if (++admissions == 3) settings.Integrations!.A2A!.AllowedRemoteAgents = [DiscoveryUrl];
            }));

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.AgentNotAllowed, result.Error.Code);

        Assert.Equal(3, admissions);

        Assert.DoesNotContain("withdrawn-interface.example", dns.Hosts);
    }

    private sealed class DiscoveryDestinationDns : RetroDownfall.Arcanum.Core.Security.IDnsResolver
    {
        internal List<string> Hosts { get; } = [];

        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
        {
            Hosts.Add(host);

            return Task.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34")]);
        }
    }

    private sealed class DiscoveryAdmission(Action? entering = null, Action? leaving = null) : IA2AGuildDiscoveryAdmission
    {
        public async Task<T> AdmitAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            entering?.Invoke();

            try { return await action(ct); }
            finally { leaving?.Invoke(); }
        }
    }

    private sealed class DiscoveryAdmissionDns(RetroDownfall.Arcanum.Core.Security.IDnsResolver inner,
        Func<bool> admitted) : RetroDownfall.Arcanum.Core.Security.IDnsResolver
    {
        internal bool AllRequestsAdmitted = true;

        internal int Requests;

        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
        {
            Requests++;

            AllRequestsAdmitted &= admitted();

            return inner.GetHostAddressesAsync(host, cancellationToken);
        }
    }

    private sealed class DiscoveryAdmissionClientFactory(HttpMessageHandler handler, Func<bool> admitted) : IHttpClientFactory
    {
        internal List<(bool CreatedUnderAdmission, bool DisposedUnderAdmission)> CardLifetimes { get; } = [];

        public HttpClient CreateClient(string name)
        {
            bool createdUnderAdmission = admitted();

            return new DiscoveryAdmissionClient(handler, bufferSize =>
            {
                if (bufferSize == A2AClientService.MaxAgentCardBytes) CardLifetimes.Add((createdUnderAdmission, admitted()));
            });
        }

        private sealed class DiscoveryAdmissionClient(HttpMessageHandler handler, Action<long> recordDispose)
            : HttpClient(handler, disposeHandler: false)
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing) recordDispose(MaxResponseContentBufferSize);

                base.Dispose(disposing);
            }
        }
    }

    private sealed class DiscoveryAdmissionContent(byte[] bytes, Func<bool> admitted) : HttpContent
    {
        internal bool ReadUnderAdmission { get; private set; }

        internal bool DisposedUnderAdmission { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposedUnderAdmission = admitted();

            base.Dispose(disposing);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadUnderAdmission = admitted();

            return stream.WriteAsync(bytes).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }

    [Fact]
    public async Task Guild_card_resolution_refuses_a_null_advertised_interface_without_throwing()
    {
        var card = BuildFakeCard();

        card.SupportedInterfaces = [null!];

        RecordingHttpHandler handler = new(_ => Task.FromResult(CardResponse(card)));

        ArcanumSettings settings = EnabledSettings([DiscoveryUrl]);

        settings.Features.Guild = true;

        A2AClientService client = CreateClient(handler, settings);

        var result = await client.DiscoverGuildCardAsync(DiscoveryUrl, default, new DiscoveryAdmission());

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Sending.AgentCardInvalid, result.Error.Code);

        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task Guild_card_resolution_requires_an_explicit_configured_destination_before_network()
    {
        RecordingHttpHandler handler = new(_ => Task.FromResult(CardResponse(BuildFakeCard())));
        ArcanumSettings settings = EnabledSettings();
        settings.Features.Guild = true;
        A2AClientService client = CreateClient(handler, settings);

        var result = await client.DiscoverGuildCardAsync(DiscoveryUrl, default);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Sending.AgentNotAllowed, result.Error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Guild_card_resolution_preserves_guarded_discovery_without_creating_remote_work()
    {
        RecordingHttpHandler handler = new(request => Task.FromResult(request.Method == HttpMethod.Get
            ? CardResponse(BuildFakeCard()) : new HttpResponseMessage(HttpStatusCode.BadRequest)));
        ArcanumSettings settings = EnabledSettings([DiscoveryUrl]);
        settings.Features.Guild = true;
        A2AClientService client = CreateClient(handler, settings);

        var result = await client.DiscoverGuildCardAsync(DiscoveryUrl, default, new DiscoveryAdmission());

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(BuildFakeCard().Name, result.Value.Name);
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task Guild_card_resolution_does_not_treat_a_configured_origin_as_an_arbitrary_selector()
    {
        RecordingHttpHandler handler = new(_ => Task.FromResult(CardResponse(BuildFakeCard())));
        ArcanumSettings settings = EnabledSettings([DiscoveryUrl]);
        settings.Features.Guild = true;
        A2AClientService client = CreateClient(handler, settings);

        var result = await client.DiscoverGuildCardAsync(DiscoveryUrl + "different-agent", default);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Sending.AgentNotAllowed, result.Error.Code);
        Assert.Empty(handler.Requests);
    }
}
