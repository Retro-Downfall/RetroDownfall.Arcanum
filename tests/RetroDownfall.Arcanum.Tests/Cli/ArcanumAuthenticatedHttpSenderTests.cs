using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ArcanumAuthenticatedHttpSenderTests
{
    private static readonly HashSet<string> HttpTransportMethods = new(StringComparer.Ordinal)
    {
        "DeleteAsync",
        "DeleteFromJsonAsync",
        "GetAsync",
        "GetByteArrayAsync",
        "GetFromJsonAsAsyncEnumerable",
        "GetFromJsonAsync",
        "GetStreamAsync",
        "GetStringAsync",
        "PatchAsync",
        "PatchAsJsonAsync",
        "PostAsync",
        "PostAsJsonAsync",
        "PutAsync",
        "PutAsJsonAsync",
        "Send",
        "SendAsync",
    };

    [Fact]
    public async Task Replayable_request_renews_once_after_unauthorized_and_rebuilds_the_request()
    {
        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        RecordingHandler handler = new(requestNumber =>
            new HttpResponseMessage(
                requestNumber == 1
                    ? HttpStatusCode.Unauthorized
                    : HttpStatusCode.OK));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        int requestFactoryCalls = 0;

        using ArcanumAuthenticatedHttpResponse sent =
            await ArcanumAuthenticatedHttpSender.SendAsync(
                client,
                credentials,
                () =>
                {
                    Interlocked.Increment(ref requestFactoryCalls);

                    return new HttpRequestMessage(HttpMethod.Get, "api/health");
                },
                HttpCompletionOption.ResponseHeadersRead,
                canReplayAfterUnauthorized: true,
                CancellationToken.None);

        Assert.True(sent.IsAuthenticated);
        Assert.Equal(HttpStatusCode.OK, sent.Response!.StatusCode);
        Assert.Equal(2, requestFactoryCalls);
        Assert.Equal(2, handler.Requests.Count);

        string firstCapability = Assert.Single(
            handler.Requests[0].Headers.GetValues(
                ArcanumApiHeaders.ProcessCapability));

        string renewedCapability = Assert.Single(
            handler.Requests[1].Headers.GetValues(
                ArcanumApiHeaders.ProcessCapability));

        Assert.NotEqual(firstCapability, renewedCapability);
        Assert.All(
            handler.Requests,
            static request => Assert.False(
                request.Headers.Contains(ArcanumApiHeaders.ApiKey)));
    }

    [Fact]
    public async Task Nonreplayable_request_renews_for_the_next_call_without_replaying_the_body()
    {
        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        RecordingHandler handler = new(requestNumber =>
            new HttpResponseMessage(
                requestNumber == 1
                    ? HttpStatusCode.Unauthorized
                    : HttpStatusCode.OK));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        int firstFactoryCalls = 0;

        using (ArcanumAuthenticatedHttpResponse first =
               await ArcanumAuthenticatedHttpSender.SendAsync(
                   client,
                   credentials,
                   () =>
                   {
                       Interlocked.Increment(ref firstFactoryCalls);

                       return new HttpRequestMessage(HttpMethod.Post, "api/upload")
                       {
                           Content = new StringContent("single-pass"),
                       };
                   },
                   HttpCompletionOption.ResponseHeadersRead,
                   canReplayAfterUnauthorized: false,
                   CancellationToken.None))
        {
            Assert.True(first.IsAuthenticated);
            Assert.Equal(HttpStatusCode.Unauthorized, first.Response!.StatusCode);
        }

        Assert.Equal(1, firstFactoryCalls);

        using ArcanumAuthenticatedHttpResponse second =
            await ArcanumAuthenticatedHttpSender.SendAsync(
                client,
                credentials,
                static () => new HttpRequestMessage(HttpMethod.Get, "api/health"),
                HttpCompletionOption.ResponseHeadersRead,
                canReplayAfterUnauthorized: true,
                CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, second.Response!.StatusCode);
        Assert.Equal(2, handler.Requests.Count);

        string rejectedCapability = Assert.Single(
            handler.Requests[0].Headers.GetValues(
                ArcanumApiHeaders.ProcessCapability));

        string nextCapability = Assert.Single(
            handler.Requests[1].Headers.GetValues(
                ArcanumApiHeaders.ProcessCapability));

        Assert.NotEqual(rejectedCapability, nextCapability);
    }

    [Fact]
    public async Task Persistent_unauthorized_response_is_returned_after_exactly_one_renewal()
    {
        SuccessfulPresenceHandler presenceHandler = new("test-key");
        using HttpClient presenceClient = new(presenceHandler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        int mirrorReads = 0;
        int primaryReads = 0;

        using ArcanumApiCredentialLease credentials = new(
            presenceClient,
            new Uri("http://localhost:5001/api/presence"),
            _ =>
            {
                Interlocked.Increment(ref mirrorReads);

                return Task.FromResult(SecretStoreReadResult.Missing());
            },
            _ =>
            {
                Interlocked.Increment(ref primaryReads);

                return Task.FromResult(SecretStoreReadResult.Ok("test-key"));
            });

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        using ArcanumAuthenticatedHttpResponse sent =
            await ArcanumAuthenticatedHttpSender.SendAsync(
                client,
                credentials,
                static () => new HttpRequestMessage(HttpMethod.Get, "api/health"),
                HttpCompletionOption.ResponseHeadersRead,
                canReplayAfterUnauthorized: true,
                CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, sent.Response!.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, presenceHandler.RequestCount);
        Assert.Equal(1, mirrorReads);
        Assert.Equal(1, primaryReads);

        ApiCredentialLeaseResult renewed = await credentials.ResolveAsync(
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        string lastAttemptCapability = Assert.Single(
            handler.Requests[1].Headers.GetValues(
                ArcanumApiHeaders.ProcessCapability));

        Assert.True(renewed.IsVerified);
        Assert.NotNull(renewed.ProcessCapability);
        Assert.NotEqual(lastAttemptCapability, renewed.ProcessCapability);
        Assert.Equal(3, presenceHandler.RequestCount);
        Assert.Equal(1, mirrorReads);
        Assert.Equal(1, primaryReads);
    }

    [Fact]
    public async Task Failed_refresh_after_unauthorized_preserves_the_typed_presence_diagnosis()
    {
        FailingRefreshPresenceHandler presenceHandler = new("test-key");
        HttpClient presenceClient = new(presenceHandler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        using ArcanumApiCredentialLease credentials = new(
            presenceClient,
            new Uri("http://localhost:5001/api/presence"),
            static _ => Task.FromResult(SecretStoreReadResult.Missing()),
            static _ => Task.FromResult(SecretStoreReadResult.Ok("test-key")));

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        using ArcanumAuthenticatedHttpResponse sent =
            await ArcanumAuthenticatedHttpSender.SendAsync(
                client,
                credentials,
                static () => new HttpRequestMessage(HttpMethod.Get, "api/health"),
                HttpCompletionOption.ResponseHeadersRead,
                canReplayAfterUnauthorized: true,
                CancellationToken.None);

        Assert.False(sent.IsAuthenticated);
        Assert.Equal(
            ErrorCodes.Security.UnverifiedLocalApi,
            ArcanumApiCredentialFailureMapper.ToError(sent.Credentials).Code);
        Assert.Single(handler.Requests);
        Assert.Equal(2, presenceHandler.RequestCount);
    }

    [Theory]
    [InlineData(ArcanumApiHeaders.ProcessCapability, "must-not-leak")]
    [InlineData(ArcanumApiHeaders.ApiKey, "must-not-leak")]
    [InlineData("Authorization", "Bearer must-not-leak")]
    [InlineData("x-arcanum-process-capability", "must-not-leak")]
    [InlineData("x-arcanum-key", "must-not-leak")]
    [InlineData("authorization", "Bearer must-not-leak")]
    public async Task Request_factory_cannot_supply_a_credential_header(
        string headerName,
        string headerValue)
    {
        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ArcanumAuthenticatedHttpSender.SendAsync(
                client,
                credentials,
                () =>
                {
                    HttpRequestMessage request = new(HttpMethod.Get, "api/health");
                    Assert.True(request.Headers.TryAddWithoutValidation(
                        headerName,
                        headerValue));

                    return request;
                },
                HttpCompletionOption.ResponseHeadersRead,
                canReplayAfterUnauthorized: true,
                CancellationToken.None));

        Assert.Contains(
            "leave local API authentication",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(ArcanumApiHeaders.ProcessCapability, "must-not-leak")]
    [InlineData(ArcanumApiHeaders.ApiKey, "must-not-leak")]
    [InlineData("x-arcanum-process-capability", "must-not-leak")]
    [InlineData("x-arcanum-key", "must-not-leak")]
    public async Task Request_content_cannot_smuggle_a_credential_header(
        string headerName,
        string headerValue)
    {
        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ArcanumAuthenticatedHttpSender.SendAsync(
                client,
                credentials,
                () =>
                {
                    HttpRequestMessage request = new(HttpMethod.Post, "api/health")
                    {
                        Content = new StringContent("body"),
                    };

                    Assert.True(request.Content.Headers.TryAddWithoutValidation(
                        headerName,
                        headerValue));

                    return request;
                },
                HttpCompletionOption.ResponseHeadersRead,
                canReplayAfterUnauthorized: true,
                CancellationToken.None));

        Assert.Contains(
            "leave local API authentication",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Request_content_without_credentials_is_sent_normally()
    {
        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        using ArcanumAuthenticatedHttpResponse sent =
            await ArcanumAuthenticatedHttpSender.SendAsync(
                client,
                credentials,
                static () => new HttpRequestMessage(HttpMethod.Post, "api/health")
                {
                    Content = new StringContent("body"),
                },
                HttpCompletionOption.ResponseHeadersRead,
                canReplayAfterUnauthorized: true,
                CancellationToken.None);

        Assert.True(sent.IsAuthenticated);
        Assert.Equal(HttpStatusCode.OK, sent.Response!.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("http://localhost:5002/", "api/health")]
    [InlineData("http://localhost:5001/", "http://localhost:5002/api/health")]
    public async Task Relative_and_absolute_requests_cannot_cross_the_verified_presence_authority(
        string baseAddress,
        string requestUri)
    {
        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri(baseAddress),
        };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ArcanumAuthenticatedHttpSender.SendAsync(
                client,
                credentials,
                () => new HttpRequestMessage(HttpMethod.Get, requestUri),
                HttpCompletionOption.ResponseHeadersRead,
                canReplayAfterUnauthorized: true,
                CancellationToken.None));

        Assert.Contains(
            "verified local API presence authority",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(ArcanumApiHeaders.ProcessCapability, "must-not-leak")]
    [InlineData(ArcanumApiHeaders.ApiKey, "must-not-leak")]
    [InlineData("Authorization", "Bearer must-not-leak")]
    [InlineData("x-arcanum-process-capability", "must-not-leak")]
    [InlineData("x-arcanum-key", "must-not-leak")]
    [InlineData("authorization", "Bearer must-not-leak")]
    public async Task Http_client_default_credential_headers_are_rejected_before_send(
        string headerName,
        string headerValue)
    {
        using ArcanumApiCredentialLease credentials =
            ArcanumApiCredentialLeaseTestFactory.Create("test-key");

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("http://localhost:5001/"),
        };

        Assert.True(client.DefaultRequestHeaders.TryAddWithoutValidation(headerName, headerValue));

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ArcanumAuthenticatedHttpSender.SendAsync(
                client,
                credentials,
                static () => new HttpRequestMessage(HttpMethod.Get, "api/health"),
                HttpCompletionOption.ResponseHeadersRead,
                canReplayAfterUnauthorized: true,
                CancellationToken.None));

        Assert.Contains(
            "leave authentication headers",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Credential_failure_mapping_preserves_each_actionable_diagnosis()
    {
        Error missing = ArcanumApiCredentialFailureMapper.ToError(
            ApiCredentialLeaseResult.CredentialFailure(
                SecretStoreReadStatus.Missing,
                "missing guidance\r\ncontinued"));
        Error unreadable = ArcanumApiCredentialFailureMapper.ToError(
            ApiCredentialLeaseResult.CredentialFailure(
                SecretStoreReadStatus.Corrupted,
                "unreadable guidance\r\ncontinued"));
        Error mismatch = ArcanumApiCredentialFailureMapper.ToError(
            ApiCredentialLeaseResult.CredentialFailure(
                SecretStoreReadStatus.Ok,
                "credential mismatch"));
        Error timeout = ArcanumApiCredentialFailureMapper.ToError(
            ApiCredentialLeaseResult.Transient(
                HealthProbeState.Timeout,
                "presence timed out"));
        Error unavailable = ArcanumApiCredentialFailureMapper.ToError(
            ApiCredentialLeaseResult.Transient(
                HealthProbeState.UnhealthyStatus,
                "host is starting"));
        Error unverified = ArcanumApiCredentialFailureMapper.ToError(
            ApiCredentialLeaseResult.Transient(
                HealthProbeState.UnexpectedResponder,
                "invalid\r\npresence proof"));

        Assert.Equal(ErrorCodes.Security.MissingApiKey, missing.Code);
        Assert.Equal("missing guidance continued", missing.Message);
        Assert.Equal(ErrorCodes.Security.CredentialUnreadable, unreadable.Code);
        Assert.Equal("unreadable guidance continued", unreadable.Message);
        Assert.Equal(ErrorCodes.Auth.Unauthorized, mismatch.Code);
        Assert.Equal("credential mismatch", mismatch.Message);
        Assert.Equal(ErrorCodes.Connection.Timeout, timeout.Code);
        Assert.Equal("presence timed out", timeout.Message);
        Assert.Equal(ErrorCodes.Connection.Unreachable, unavailable.Code);
        Assert.Equal("host is starting", unavailable.Message);
        Assert.True(
            ArcanumApiCredentialFailureMapper.IsRetryable(
                ApiCredentialLeaseResult.Transient(
                    HealthProbeState.UnhealthyStatus,
                    "host is starting")));
        Assert.Equal(ErrorCodes.Security.UnverifiedLocalApi, unverified.Code);
        Assert.Equal("invalid presence proof", unverified.Message);
        Assert.False(
            ArcanumApiCredentialFailureMapper.IsRetryable(
                ApiCredentialLeaseResult.Transient(
                    HealthProbeState.UnexpectedResponder,
                    "invalid presence proof")));
    }

    [Theory]
    [InlineData(HealthProbeState.Timeout, ErrorCodes.Connection.Timeout, true)]
    [InlineData(HealthProbeState.ConnectionRefused, ErrorCodes.Connection.Unreachable, true)]
    [InlineData(HealthProbeState.NetworkUnreachable, ErrorCodes.Connection.Unreachable, true)]
    [InlineData(HealthProbeState.DnsFailure, ErrorCodes.Connection.Unreachable, true)]
    [InlineData(HealthProbeState.UnhealthyStatus, ErrorCodes.Connection.Unreachable, true)]
    [InlineData(HealthProbeState.TlsFailure, ErrorCodes.Security.UnverifiedLocalApi, false)]
    [InlineData(HealthProbeState.UnexpectedResponder, ErrorCodes.Security.UnverifiedLocalApi, false)]
    [InlineData(HealthProbeState.Unauthorized, ErrorCodes.Auth.Unauthorized, false)]
    public void Every_transient_credential_state_maps_to_its_exact_code_and_retry_policy(
        HealthProbeState state,
        string expectedCode,
        bool expectedRetryable)
    {
        ApiCredentialLeaseResult result = ApiCredentialLeaseResult.Transient(
            state,
            "safe guidance");

        Assert.Equal(
            expectedCode,
            ArcanumApiCredentialFailureMapper.ToError(result).Code);
        Assert.Equal(
            expectedRetryable,
            ArcanumApiCredentialFailureMapper.IsRetryable(result));
    }

    [Fact]
    public void Every_cli_http_send_uses_the_authenticated_sender_or_an_exact_narrow_allowance()
    {
        CSharpCompilation compilation = CliCompilation();
        HttpTransportSite[] rawTransports = DiscoverHttpTransportSites(
            compilation,
            AuthoredCliTrees(compilation));

        string[] expectedRawTransportRoots =
        [
            "src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiCredentialLease.cs|ArcanumApiCredentialLease|RequestChallengeAsync|SendAsync|invocation|_client",
            "src/RetroDownfall.Arcanum.Cli/Services/ArcanumAuthenticatedHttpSender.cs|ArcanumAuthenticatedHttpSender|SendAsync|SendAsync|invocation|client",
            "src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupProviderProbe.cs|SetupProviderProbe|ProbeAsync|GetAsync|invocation|client",
        ];

        Assert.Equal(
            expectedRawTransportRoots.Order(StringComparer.Ordinal),
            rawTransports
                .Select(DescribeHttpTransportSite)
                .Order(StringComparer.Ordinal));

        Assert.All(
            rawTransports,
            site => Assert.True(
                IsExactRawTransportAllowance(site),
                $"The raw transport allowance no longer binds its reviewed request URI source: {DescribeHttpTransportSite(site)}"));
    }

    [Fact]
    public void Every_cli_http_client_construction_belongs_to_the_closed_transport_composition()
    {
        CSharpCompilation compilation = CliCompilation();
        HttpClientConstructionSite[] clientConstructions =
            DiscoverHttpClientConstructionSites(
                compilation,
                AuthoredCliTrees(compilation));

        string[] expectedComposition =
        [
            "src/RetroDownfall.Arcanum.Cli/Commands/DoctorCommand.cs|DoctorCommand|BuildApiReachabilityCheckAsync|CreateClient|name=ArcanumApiRequest",
            "src/RetroDownfall.Arcanum.Cli/Diagnostics/HostHealthDiagnostics.cs|HostHealthComponentsCheck|InspectAsync|CreateClient|name=ArcanumApiRequest",
            "src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.Watch.cs|ArcanumApiClient|WatchSseAsync|CreateClient|name=ArcanumApi",
            "src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.cs|ArcanumApiClient|AskStreamAsync|CreateClient|name=ArcanumApi",
            "src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.cs|ArcanumApiClient|DownloadSessionAttachmentAsync|CreateClient|name=ArcanumApi",
            "src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.cs|ArcanumApiClient|ResearchWebAsync|CreateClient|name=ArcanumApi",
            "src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.cs|ArcanumApiClient|SendRequestAsync|CreateClient|name=parameter:httpClientName",
            "src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.cs|ArcanumApiClient|StreamApprenticeChronicleAsync|CreateClient|name=ArcanumApi",
            "src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiClient.cs|ArcanumApiClient|UploadSessionAttachmentAsync|CreateClient|name=ArcanumApiRequest",
            "src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiCredentialLease.cs|ArcanumApiCredentialLease|CreateHttpClient|CreateClient|name=ArcanumApiRequest",
            "src/RetroDownfall.Arcanum.Cli/Services/FileBatchApiClient.cs|FileBatchApiClient|DownloadFileAsync|CreateClient|name=ArcanumApi",
            "src/RetroDownfall.Arcanum.Cli/Services/FileBatchApiClient.cs|FileBatchApiClient|SendJsonAsync|CreateClient|name=ArcanumApiRequest",
            "src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupProviderProbe.cs|SetupProviderProbe|ProbeAsync|new HttpClient|handler=handlerFactory.Create(),disposeHandler=true",
        ];

        Assert.Equal(
            expectedComposition.Order(StringComparer.Ordinal),
            clientConstructions
                .Select(DescribeHttpClientConstructionSite)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Semantic_transport_inventory_recognizes_real_static_conditional_and_method_group_sends()
    {
        CSharpCompilation compilation = CompileTransportFixture(
            """
            using System;
            using System.Net.Http;
            using System.Threading;
            using System.Threading.Tasks;
            using WebClient = System.Net.Http.HttpClient;
            using JsonTransport = System.Net.Http.Json.HttpClientJsonExtensions;

            internal static class Fixture
            {
                internal static async Task ProbeAsync(
                    WebClient client,
                    HttpRequestMessage request,
                    CancellationToken cancellationToken)
                {
                    _ = client?.SendAsync(request, cancellationToken);
                    _ = await JsonTransport.GetFromJsonAsync<object>(
                        client,
                        "api/item",
                        cancellationToken);
                    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> deferred =
                        client.SendAsync;
                    _ = deferred;
                }
            }
            """);

        HttpTransportSite[] sites = DiscoverHttpTransportSites(
            compilation,
            compilation.SyntaxTrees);

        Assert.Equal(
            [
                "GetFromJsonAsync|invocation",
                "SendAsync|invocation",
                "SendAsync|method-group",
            ],
            sites
                .Select(static site =>
                    $"{site.Method.Name}|{(site.Invocation is null ? "method-group" : "invocation")}")
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Semantic_transport_inventory_ignores_spelling_shadows()
    {
        CSharpCompilation compilation = CompileTransportFixture(
            """
            using System.Net.Http;
            using System.Threading;
            using System.Threading.Tasks;

            internal sealed class ShadowClient
            {
                internal Task<HttpResponseMessage> SendAsync(
                    HttpRequestMessage request,
                    CancellationToken cancellationToken) =>
                    Task.FromResult(new HttpResponseMessage());
            }

            internal static class HttpClientJsonExtensions
            {
                internal static Task<object> GetFromJsonAsync(
                    ShadowClient client,
                    string requestUri,
                    CancellationToken cancellationToken) =>
                    Task.FromResult(new object());
            }

            internal static class ArcanumAuthenticatedHttpSender
            {
                internal static Task<HttpResponseMessage> SendAsync(
                    ShadowClient client,
                    HttpRequestMessage request,
                    CancellationToken cancellationToken) =>
                    Task.FromResult(new HttpResponseMessage());
            }

            internal static class Fixture
            {
                internal static async Task ProbeAsync(
                    ShadowClient client,
                    HttpRequestMessage request,
                    CancellationToken cancellationToken)
                {
                    _ = await client.SendAsync(request, cancellationToken);
                    _ = await HttpClientJsonExtensions.GetFromJsonAsync(
                        client,
                        "api/item",
                        cancellationToken);
                    _ = await ArcanumAuthenticatedHttpSender.SendAsync(
                        client,
                        request,
                        cancellationToken);
                }
            }
            """);

        Assert.Empty(DiscoverHttpTransportSites(
            compilation,
            compilation.SyntaxTrees));
    }

    [Fact]
    public void Semantic_client_inventory_recognizes_named_factory_method_groups_and_target_typed_new()
    {
        CSharpCompilation compilation = CompileTransportFixture(
            """
            using System;
            using System.Net.Http;
            using WebClient = System.Net.Http.HttpClient;

            internal sealed class ShadowFactory
            {
                internal WebClient CreateClient(string name) => null!;
            }

            namespace Shadows
            {
                internal sealed class HttpClient
                {
                }
            }

            internal static class Fixture
            {
                internal static void Probe(IHttpClientFactory factory, ShadowFactory shadow)
                {
                    WebClient named = factory.CreateClient(name: "ArcanumApiRequest");
                    Func<string, WebClient> deferred = factory.CreateClient;
                    WebClient direct = new();
                    _ = shadow.CreateClient("shadow");
                    _ = new Shadows.HttpClient();
                    _ = named;
                    _ = deferred;
                    _ = direct;
                }
            }
            """);

        HttpClientConstructionSite[] sites = DiscoverHttpClientConstructionSites(
            compilation,
            compilation.SyntaxTrees);

        Assert.Equal(3, sites.Length);
        Assert.Contains(
            sites,
            static site => site.Kind == HttpClientConstructionKind.FactoryInvocation
                && DescribeFactoryClientName(site.FactoryInvocation!)
                    == "ArcanumApiRequest");
        Assert.Contains(
            sites,
            static site => site.Kind == HttpClientConstructionKind.FactoryMethodGroup);
        Assert.Contains(
            sites,
            static site => site.Kind == HttpClientConstructionKind.Direct);
    }

    private static CSharpCompilation CliCompilation() =>
        Assert.Single(
            HostedGrimoireProducerInventory.ProductionCompilations,
            static compilation =>
                compilation.AssemblyName == "RetroDownfall.Arcanum.Cli");

    private static IReadOnlyList<SyntaxTree> AuthoredCliTrees(
        CSharpCompilation compilation)
    {
        HashSet<string> authoredPaths = ProductionSourceInventory.Sources()
            .Select(static source => NormalizeSourcePath(source.RelativePath))
            .Where(static path => path.StartsWith(
                "src/RetroDownfall.Arcanum.Cli/",
                StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        SyntaxTree[] trees =
        [
            .. compilation.SyntaxTrees.Where(tree =>
                authoredPaths.Contains(NormalizeSourcePath(tree.FilePath))),
        ];

        Assert.NotEmpty(trees);

        return trees;
    }

    private static CSharpCompilation CompileTransportFixture(string source)
    {
        CSharpCompilation cliCompilation = CliCompilation();
        CSharpParseOptions parseOptions = (CSharpParseOptions)AuthoredCliTrees(
            cliCompilation)[0].Options;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            source,
            parseOptions,
            "TransportInventoryFixture.cs");
        CSharpCompilation fixture = CSharpCompilation.Create(
            "TransportInventoryFixture",
            [tree],
            cliCompilation.References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        Assert.DoesNotContain(
            fixture.GetDiagnostics(),
            static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        return fixture;
    }

    private static HttpTransportSite[] DiscoverHttpTransportSites(
        CSharpCompilation compilation,
        IEnumerable<SyntaxTree> trees)
    {
        List<HttpTransportSite> sites = [];

        foreach (SyntaxTree tree in trees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            SyntaxNode root = tree.GetRoot();

            foreach (InvocationExpressionSyntax invocation in root
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>())
            {
                if (model.GetOperation(invocation) is IInvocationOperation operation
                    && IsFrameworkHttpTransportMethod(operation.TargetMethod))
                {
                    sites.Add(new HttpTransportSite(
                        model,
                        invocation,
                        operation.TargetMethod,
                        operation));
                }
            }

            foreach (ExpressionSyntax reference in root
                .DescendantNodes()
                .OfType<ExpressionSyntax>()
                .Where(IsOutermostMethodGroupCandidate))
            {
                if (model.GetSymbolInfo(reference).Symbol is IMethodSymbol method
                    && IsFrameworkHttpTransportMethod(method))
                {
                    sites.Add(new HttpTransportSite(
                        model,
                        reference,
                        method,
                        Invocation: null));
                }
            }
        }

        return [.. sites];
    }

    private static HttpClientConstructionSite[] DiscoverHttpClientConstructionSites(
        CSharpCompilation compilation,
        IEnumerable<SyntaxTree> trees)
    {
        List<HttpClientConstructionSite> sites = [];

        foreach (SyntaxTree tree in trees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            SyntaxNode root = tree.GetRoot();

            foreach (InvocationExpressionSyntax invocation in root
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>())
            {
                if (model.GetOperation(invocation) is IInvocationOperation operation
                    && IsHttpClientFactoryCreate(operation.TargetMethod, compilation))
                {
                    sites.Add(new HttpClientConstructionSite(
                        model,
                        invocation,
                        HttpClientConstructionKind.FactoryInvocation,
                        operation,
                        Creation: null,
                        operation.TargetMethod));
                }
            }

            foreach (ExpressionSyntax reference in root
                .DescendantNodes()
                .OfType<ExpressionSyntax>()
                .Where(IsOutermostMethodGroupCandidate))
            {
                if (model.GetSymbolInfo(reference).Symbol is IMethodSymbol method
                    && IsHttpClientFactoryCreate(method, compilation))
                {
                    sites.Add(new HttpClientConstructionSite(
                        model,
                        reference,
                        HttpClientConstructionKind.FactoryMethodGroup,
                        FactoryInvocation: null,
                        Creation: null,
                        method));
                }
            }

            foreach (BaseObjectCreationExpressionSyntax creation in root
                .DescendantNodes()
                .OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (model.GetOperation(creation) is IObjectCreationOperation operation
                    && IsHttpClientType(operation.Type, compilation))
                {
                    sites.Add(new HttpClientConstructionSite(
                        model,
                        creation,
                        HttpClientConstructionKind.Direct,
                        FactoryInvocation: null,
                        operation,
                        operation.Constructor));
                }
            }
        }

        return [.. sites];
    }

    private static bool IsOutermostMethodGroupCandidate(ExpressionSyntax expression)
    {
        if (expression.Parent is InvocationExpressionSyntax invocation
            && invocation.Expression == expression)
        {
            return false;
        }

        if (expression is SimpleNameSyntax
            && expression.Parent is MemberAccessExpressionSyntax member
            && member.Name == expression)
        {
            return false;
        }

        if (expression is SimpleNameSyntax
            && expression.Parent is MemberBindingExpressionSyntax binding
            && binding.Name == expression)
        {
            return false;
        }

        return expression is MemberAccessExpressionSyntax
            or MemberBindingExpressionSyntax
            or SimpleNameSyntax;
    }

    private static bool IsFrameworkHttpTransportMethod(IMethodSymbol method)
    {
        IMethodSymbol target = method.ReducedFrom ?? method;

        return HttpTransportMethods.Contains(target.Name)
            && (IsExactType(
                    target.ContainingType,
                    "System.Net.Http",
                    nameof(HttpClient))
                || IsExactType(
                    target.ContainingType,
                    "System.Net.Http.Json",
                    "HttpClientJsonExtensions"));
    }

    private static bool IsHttpClientFactoryCreate(
        IMethodSymbol method,
        Compilation compilation)
    {
        if (method.Name != "CreateClient"
            || !IsHttpClientType(method.ReturnType, compilation))
        {
            return false;
        }

        INamedTypeSymbol? factoryType = compilation.GetTypeByMetadataName(
            "System.Net.Http.IHttpClientFactory");

        if (factoryType is null)
        {
            return false;
        }

        IMethodSymbol target = method.ReducedFrom ?? method;

        if (SymbolEqualityComparer.Default.Equals(
                target.ContainingType,
                factoryType))
        {
            return true;
        }

        if (target.IsExtensionMethod
            && target.Parameters.Length > 0
            && SymbolEqualityComparer.Default.Equals(
                target.Parameters[0].Type,
                factoryType))
        {
            return true;
        }

        return target.ContainingType.AllInterfaces.Any(
            candidate => SymbolEqualityComparer.Default.Equals(
                candidate,
                factoryType));
    }

    private static bool IsHttpClientType(
        ITypeSymbol? type,
        Compilation compilation)
    {
        INamedTypeSymbol? httpClient = compilation.GetTypeByMetadataName(
            "System.Net.Http.HttpClient");

        for (INamedTypeSymbol? candidate = type as INamedTypeSymbol;
             candidate is not null;
             candidate = candidate.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, httpClient))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExactType(
        ITypeSymbol? type,
        string namespaceName,
        string metadataName) =>
        type is not null
        && type.MetadataName == metadataName
        && type.ContainingNamespace.ToDisplayString() == namespaceName;

    private static bool IsExactRawTransportAllowance(HttpTransportSite site) =>
        IsExactPresenceChallengeAllowance(site)
        || IsExactAuthenticatedSenderAllowance(site)
        || IsExactSetupProviderAllowance(site);

    private static bool IsExactPresenceChallengeAllowance(
        HttpTransportSite site)
    {
        if (!IsExactOwner(
                site,
                "src/RetroDownfall.Arcanum.Cli/Services/ArcanumApiCredentialLease.cs",
                nameof(ArcanumApiCredentialLease),
                "RequestChallengeAsync")
            || site.Invocation is null
            || !IsReference(
                InvocationReceiver(site.Invocation),
                SymbolKind.Field,
                "_client"))
        {
            return false;
        }

        IArgumentOperation? requestArgument = Argument(
            site.Invocation,
            "request");

        if (ReferenceSymbol(requestArgument?.Value) is not ILocalSymbol request
            || Initializer(request) is not { } initializer
            || site.Model.GetOperation(initializer) is not
                IObjectCreationOperation requestCreation
            || !IsExactType(
                requestCreation.Type,
                "System.Net.Http",
                nameof(HttpRequestMessage)))
        {
            return false;
        }

        IOperation? method = Argument(requestCreation, "method")?.Value;
        IOperation? uri = Argument(requestCreation, "requestUri")?.Value;

        return StripConversions(method) is IPropertyReferenceOperation
            {
                Property.Name: "Get",
                Property.IsStatic: true,
            } methodProperty
            && IsExactType(
                methodProperty.Property.ContainingType,
                "System.Net.Http",
                nameof(HttpMethod))
            && StripConversions(uri) is IFieldReferenceOperation
            {
                Field.Name: "_presenceUri",
            } uriField
            && uriField.Field.ContainingType.Name
                == nameof(ArcanumApiCredentialLease);
    }

    private static bool IsExactAuthenticatedSenderAllowance(
        HttpTransportSite site)
    {
        if (!IsExactOwner(
                site,
                "src/RetroDownfall.Arcanum.Cli/Services/ArcanumAuthenticatedHttpSender.cs",
                nameof(ArcanumAuthenticatedHttpSender),
                nameof(ArcanumAuthenticatedHttpSender.SendAsync))
            || site.Invocation is null
            || ReferenceSymbol(InvocationReceiver(site.Invocation)) is not
                IParameterSymbol client
            || client.Name != "client"
            || ReferenceSymbol(Argument(site.Invocation, "request")?.Value) is not
                ILocalSymbol request
            || Initializer(request) is not { } initializer
            || RequestFactoryInvocation(
                site.Model.GetOperation(initializer)) is not
                { } requestFactoryCall
            || requestFactoryCall.TargetMethod.MethodKind
                != MethodKind.DelegateInvoke
            || ReferenceSymbol(requestFactoryCall.Instance) is not
                IParameterSymbol { Name: "requestFactory" })
        {
            return false;
        }

        MethodDeclarationSyntax owner = site.Syntax
            .AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>()
            .First();

        return owner.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.SpanStart < site.Syntax.SpanStart)
            .Select(invocation => site.Model.GetOperation(invocation))
            .OfType<IInvocationOperation>()
            .Any(guard =>
                guard.TargetMethod.Name == "BindRequestToVerifiedAuthority"
                && guard.TargetMethod.ContainingType.Name
                    == nameof(ArcanumAuthenticatedHttpSender)
                && SymbolEqualityComparer.Default.Equals(
                    ReferenceSymbol(Argument(guard, "client")?.Value),
                    client)
                && SymbolEqualityComparer.Default.Equals(
                    ReferenceSymbol(Argument(guard, "request")?.Value),
                    request)
                && StripConversions(
                    Argument(guard, "verifiedAuthority")?.Value) is
                    IPropertyReferenceOperation
                    {
                        Property.Name: "CanonicalAuthority",
                    } authority
                && authority.Property.ContainingType.Name
                    == nameof(ArcanumApiCredentialLease));
    }

    private static bool IsExactSetupProviderAllowance(HttpTransportSite site)
    {
        if (!IsExactOwner(
                site,
                "src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupProviderProbe.cs",
                "SetupProviderProbe",
                "ProbeAsync")
            || site.Invocation is null
            || !IsReference(
                InvocationReceiver(site.Invocation),
                SymbolKind.Local,
                "client")
            || ReferenceSymbol(
                Argument(site.Invocation, "requestUri")?.Value) is not
                ILocalSymbol { Name: "probeUrl" } probeUrl
            || Initializer(probeUrl) is not { } initializer
            || StripConversions(site.Model.GetOperation(initializer)) is not
                IBinaryOperation
                {
                    OperatorKind: BinaryOperatorKind.Add,
                } concatenation
            || !concatenation.RightOperand.ConstantValue.HasValue
            || !Equals(
                concatenation.RightOperand.ConstantValue.Value,
                "/models")
            || StripConversions(concatenation.LeftOperand) is not
                IInvocationOperation trimEnd
            || !IsStringMethod(trimEnd.TargetMethod, "TrimEnd")
            || StripConversions(trimEnd.Instance) is not
                IInvocationOperation trim
            || !IsStringMethod(trim.TargetMethod, "Trim")
            || ReferenceSymbol(trim.Instance) is not
                IParameterSymbol { Name: "endpoint" }
            || trimEnd.Arguments.Length != 1)
        {
            return false;
        }

        return IsSingleSlash(trimEnd.Arguments[0].Value);
    }

    private static bool IsStringMethod(IMethodSymbol method, string name) =>
        method.Name == name
        && IsExactType(method.ContainingType, "System", nameof(String));

    private static bool IsSingleSlash(IOperation? operation)
    {
        IOperation? value = StripConversions(operation);

        if (value?.ConstantValue is { HasValue: true, Value: '/' })
        {
            return true;
        }

        return value is IArrayCreationOperation
        {
            Initializer.ElementValues.Length: 1,
        } array
        && IsSingleSlash(array.Initializer.ElementValues[0]);
    }

    private static bool IsExactOwner(
        HttpTransportSite site,
        string sourcePath,
        string typeName,
        string methodName)
    {
        IMethodSymbol? owner = site.Model.GetEnclosingSymbol(
            site.Syntax.SpanStart) as IMethodSymbol;

        return owner is not null
            && NormalizeSourcePath(site.Syntax.SyntaxTree.FilePath) == sourcePath
            && owner.ContainingType.Name == typeName
            && owner.Name == methodName;
    }

    private static string DescribeHttpTransportSite(HttpTransportSite site)
    {
        string shape = site.Invocation is null
            ? "method-group"
            : "invocation";
        string receiver = site.Invocation is null
            ? MethodGroupReceiver(site.Syntax)
            : InvocationReceiver(site.Invocation)?.Syntax.ToString()
                ?? "<static>";

        return $"{DescribeOwner(site.Model, site.Syntax, site.Method.Name)}|{shape}|{receiver}";
    }

    private static string DescribeHttpClientConstructionSite(
        HttpClientConstructionSite site)
    {
        string operation = site.Kind switch
        {
            HttpClientConstructionKind.FactoryInvocation => "CreateClient",
            HttpClientConstructionKind.FactoryMethodGroup =>
                "CreateClient method-group",
            _ => "new HttpClient",
        };
        string arguments = site.Kind switch
        {
            HttpClientConstructionKind.FactoryInvocation =>
                $"name={DescribeFactoryClientName(site.FactoryInvocation!)}",
            HttpClientConstructionKind.FactoryMethodGroup =>
                "name=<method-group>",
            _ => DescribeHttpClientConstructorArguments(site.Creation!),
        };

        return $"{DescribeOwner(site.Model, site.Syntax, operation)}|{arguments}";
    }

    private static string DescribeFactoryClientName(
        IInvocationOperation invocation)
    {
        IArgumentOperation? name = Argument(invocation, "name");

        if (name?.Value.ConstantValue is { HasValue: true, Value: string value })
        {
            return value;
        }

        ISymbol? symbol = ReferenceSymbol(name?.Value);

        return symbol is null
            ? "<default>"
            : $"{symbol.Kind.ToString().ToLowerInvariant()}:{symbol.Name}";
    }

    private static string DescribeHttpClientConstructorArguments(
        IObjectCreationOperation creation) =>
        string.Join(
            ',',
            creation.Arguments.Select(argument =>
                $"{argument.Parameter?.Name}={NormalizeExpression(argument.Value.Syntax)}"));

    private static string NormalizeExpression(SyntaxNode expression) =>
        expression.WithoutTrivia().ToFullString();

    private static IArgumentOperation? Argument(
        IInvocationOperation invocation,
        string parameterName) =>
        invocation.Arguments.SingleOrDefault(argument =>
            argument.Parameter?.Name == parameterName);

    private static IArgumentOperation? Argument(
        IObjectCreationOperation creation,
        string parameterName) =>
        creation.Arguments.SingleOrDefault(argument =>
            argument.Parameter?.Name == parameterName);

    private static IOperation? InvocationReceiver(
        IInvocationOperation invocation)
    {
        if (invocation.Instance is not null)
        {
            return StripConversions(invocation.Instance);
        }

        IMethodSymbol target = invocation.TargetMethod.ReducedFrom
            ?? invocation.TargetMethod;

        if (!target.IsExtensionMethod || target.Parameters.Length == 0)
        {
            return null;
        }

        return invocation.Arguments
            .FirstOrDefault(argument => argument.Parameter?.Ordinal == 0)
            ?.Value;
    }

    private static string MethodGroupReceiver(SyntaxNode reference)
    {
        if (reference is MemberAccessExpressionSyntax member)
        {
            return member.Expression.ToString();
        }

        ConditionalAccessExpressionSyntax? conditional = reference
            .AncestorsAndSelf()
            .OfType<ConditionalAccessExpressionSyntax>()
            .FirstOrDefault();

        return conditional?.Expression.ToString() ?? "<static>";
    }

    private static ISymbol? ReferenceSymbol(IOperation? operation) =>
        StripConversions(operation) switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation parameter => parameter.Parameter,
            IFieldReferenceOperation field => field.Field,
            IPropertyReferenceOperation property => property.Property,
            _ => null,
        };

    private static bool IsReference(
        IOperation? operation,
        SymbolKind kind,
        string name) =>
        ReferenceSymbol(operation) is { } symbol
        && symbol.Kind == kind
        && symbol.Name == name;

    private static IOperation? StripConversions(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    private static IInvocationOperation? RequestFactoryInvocation(
        IOperation? initializer)
    {
        IOperation? value = StripConversions(initializer);

        if (value is ICoalesceOperation coalesce)
        {
            value = StripConversions(coalesce.Value);
        }

        return value as IInvocationOperation;
    }

    private static ExpressionSyntax? Initializer(ILocalSymbol local) =>
        local.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax())
            .OfType<VariableDeclaratorSyntax>()
            .Select(static variable => variable.Initializer?.Value)
            .SingleOrDefault();

    private static string DescribeOwner(
        SemanticModel model,
        SyntaxNode node,
        string operation)
    {
        IMethodSymbol owner = Assert.IsAssignableFrom<IMethodSymbol>(
            model.GetEnclosingSymbol(node.SpanStart));

        return string.Join(
            '|',
            NormalizeSourcePath(node.SyntaxTree.FilePath),
            owner.ContainingType.Name,
            owner.Name,
            operation);
    }

    private static string NormalizeSourcePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        int source = normalized.IndexOf(
            "src/RetroDownfall.Arcanum.Cli/",
            StringComparison.Ordinal);

        return source < 0
            ? normalized
            : normalized[source..];
    }

    private sealed record HttpTransportSite(
        SemanticModel Model,
        SyntaxNode Syntax,
        IMethodSymbol Method,
        IInvocationOperation? Invocation);

    private sealed record HttpClientConstructionSite(
        SemanticModel Model,
        SyntaxNode Syntax,
        HttpClientConstructionKind Kind,
        IInvocationOperation? FactoryInvocation,
        IObjectCreationOperation? Creation,
        IMethodSymbol? Method);

    private enum HttpClientConstructionKind : byte
    {
        FactoryInvocation = 1,
        FactoryMethodGroup = 2,
        Direct = 3,
    }

    private sealed class RecordingHandler(
        Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private int _requestCount;

        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Requests.Add(request);
            int requestNumber = Interlocked.Increment(ref _requestCount);

            return Task.FromResult(responseFactory(requestNumber));
        }
    }

    private static HttpResponseMessage CreatePresenceResponse(
        HttpRequestMessage request,
        string apiKey)
    {
        Assert.True(
            ArcanumPresenceProofProtocol.TryDecode(
                Assert.Single(request.Headers.GetValues(
                    ArcanumApiHeaders.PresenceNonce)),
                ArcanumPresenceProofProtocol.NonceBytes,
                out byte[]? nonce));
        Assert.True(
            ArcanumPresenceProofProtocol.TryCanonicalAuthority(
                request.RequestUri!,
                out string? authority));

        byte[] encodedKey = Encoding.UTF8.GetBytes(apiKey);
        byte[] digest = SHA256.HashData(encodedKey);
        using ArcanumProcessCapabilityService capabilities = new();
        byte[] capability = capabilities.Issue();
        byte[] envelope = ArcanumPresenceProofProtocol.CreateCapabilityEnvelope(
            digest,
            nonce!,
            authority!,
            capability);
        byte[] proof = ArcanumPresenceProofProtocol.ComputeProof(
            digest,
            nonce!,
            authority!,
            envelope);

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
                ArcanumApiHeaders.PresenceCapability,
                ArcanumPresenceProofProtocol.Encode(envelope));
            response.Headers.TryAddWithoutValidation(
                ArcanumApiHeaders.PresenceProof,
                ArcanumPresenceProofProtocol.Encode(proof));

            return response;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce!);
            CryptographicOperations.ZeroMemory(encodedKey);
            CryptographicOperations.ZeroMemory(digest);
            CryptographicOperations.ZeroMemory(capability);
            CryptographicOperations.ZeroMemory(envelope);
            CryptographicOperations.ZeroMemory(proof);
        }
    }

    private sealed class FailingRefreshPresenceHandler(string apiKey) : HttpMessageHandler
    {
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(
                Interlocked.Increment(ref _requestCount) == 1
                    ? CreatePresenceResponse(request, apiKey)
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    private sealed class SuccessfulPresenceHandler(string apiKey) : HttpMessageHandler
    {
        private int _requestCount;

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Interlocked.Increment(ref _requestCount);

            return Task.FromResult(CreatePresenceResponse(request, apiKey));
        }
    }
}
