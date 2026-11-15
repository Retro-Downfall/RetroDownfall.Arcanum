using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private static readonly string[] ReviewedHttpMetadataLeaves =
    [
        "System.Net.Http.HttpClient..ctor",
        "System.Net.Http.HttpClient.Timeout setter",
        "System.Net.Http.HttpRequestMessage..ctor",
        "System.Net.Http.HttpMethod.Get",
        "System.Net.Http.HttpRequestMessage.Headers",
        "System.Net.Http.Headers.HttpRequestHeaders.Authorization setter",
        "System.Net.Http.Headers.AuthenticationHeaderValue..ctor",
        "System.Net.Http.SocketsHttpHandler..ctor",
        "System.Net.Http.SocketsHttpHandler.AllowAutoRedirect setter",
        "System.Net.Http.SocketsHttpHandler.UseProxy setter",
        "System.Net.Http.SocketsHttpHandler.ConnectCallback setter",
    ];

    [Theory]
    [InlineData("direct-null")]
    [InlineData("forwarded-null")]
    public void Closed_HTTP_construction_classifies_exact_metadata_leaves_and_retains_physical_calls(string shape)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverHttpConstructionShape(shape);

        Assert.All(ReviewedHttpMetadataLeaves, leaf =>
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == leaf));

        Assert.Contains(result.Items, static site => site.Callee == "System.Net.Http.HttpClient.SendAsync"
            && site.Kind == HostedProducerSiteKind.ProviderCall);

        foreach (string type in new[] { "System.Net.Http.HttpRequestMessage", "System.Net.Http.HttpMessageHandler", "System.Net.Http.HttpClient" })
        {
            Assert.Contains(result.Items, site => site.Callee == type + ".Dispose"
                && site.Kind == HostedProducerSiteKind.ProviderCall);
        }
    }

    [Theory]
    [InlineData("unknown-handler")]
    [InlineData("hashing-handler")]
    [InlineData("derived-client")]
    public void Client_constructor_metadata_does_not_hide_unknown_or_authored_logging_dispatch(string shape)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverHttpMetadataCase(shape);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Net.Http.HttpClient..ctor");
    }

    [Fact]
    public void String_request_constructor_requires_the_closed_URI_parser_universe()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverHttpMetadataCase("registered-parser");

        // The exact HTTPS sender body is unchanged. This is a refusal of the closed
        // parser-universe premise, not a claim that registration replaces HTTPS.
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Net.Http.HttpRequestMessage..ctor");
    }

    [Theory]
    [InlineData("argument-effect")]
    [InlineData("initializer-effect")]
    public void Metadata_profiles_keep_argument_and_initializer_effects_in_discovery(string shape)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverHttpMetadataCase(shape);

        Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.Kind == HostedProducerSiteKind.FileSystemEffect
            && site.EnclosingType == "HttpMetadataEffects");
    }

    [Fact]
    public void Connect_certificate_refuses_nested_handler_cleanup_factory_proofs()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverHttpMetadataCase("nested-cleanup-factory");

        // The outer callback reaches one distinct leaf factory whose callback returns
        // Stream.Null. This is finite even before the refusal; no recursive fixture runs.
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_HTTP_CONNECT_CERTIFICATE_UNPROVEN"
            && diagnostic.Detail.Contains("HOSTED_DISPOSAL_TARGET_UNRESOLVED: System.Net.Http.HttpMessageHandler.Dispose", StringComparison.Ordinal));
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverHttpMetadataCase(string shape)
    {
        string owner = HttpConstructionSendOwner;

        string inputs = HttpConstructionInputs;

        string calls = "using HttpMessageHandler handler = HttpConstructionInput.MakeHandler(); "
            + "using HttpClient client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }; "
            + "using HttpResponseMessage response = await new RetroDownfall.Arcanum.Infrastructure.Guild.GuildSpendSourceCredential.VerifiedSource().SendAsync(client, token); "
            + "_ = response.StatusCode;";

        string extra = string.Empty;

        switch (shape)
        {
            case "unknown-handler":
                calls = calls.ReplaceSdkFixtureText("HttpConstructionInput.MakeHandler()", "HttpConstructionInput.UnknownHandler", StringComparison.Ordinal);
                break;
            case "hashing-handler":
                calls = calls.ReplaceSdkFixtureText("HttpConstructionInput.MakeHandler()", "new HttpMetadataHashingHandler()", StringComparison.Ordinal);
                extra = """
                    sealed class HttpMetadataHashingHandler : HttpMessageHandler
                    {
                        public override int GetHashCode() { System.IO.File.Delete("handler-hash-effect"); return 1; }
                        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
                            => Task.FromResult(new HttpResponseMessage());
                    }
                    """;
                break;
            case "derived-client":
                calls = calls.ReplaceSdkFixtureText("new(handler, disposeHandler: false)", "new HttpMetadataHashingClient(handler)", StringComparison.Ordinal);
                extra = """
                    sealed class HttpMetadataHashingClient : HttpClient
                    {
                        internal HttpMetadataHashingClient(HttpMessageHandler handler) : base(handler, disposeHandler: false) { }
                        public override int GetHashCode() { System.IO.File.Delete("client-hash-effect"); return 1; }
                    }
                    """;
                break;
            case "registered-parser":
                calls = "UriParser.Register(new HttpMetadataUriParser(), \"arcanum-metadata\", 443); " + calls;
                extra = """
                    sealed class HttpMetadataUriParser : UriParser
                    {
                        protected override void InitializeAndValidate(Uri uri, out UriFormatException? parsingError)
                        {
                            System.IO.File.Delete("uri-parser-effect");
                            parsingError = null;
                        }
                    }
                    """;
                break;
            case "nested-cleanup-factory":
                inputs = inputs.ReplaceSdkFixtureText(
                    "static (context, token) => System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(System.IO.Stream.Null)",
                    "static (context, token) => { using HttpMessageHandler handler = HttpMetadataLeafFactory.MakeHandler(); "
                        + "using HttpClient client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }; "
                        + "return System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(System.IO.Stream.Null); }", StringComparison.Ordinal);
                extra = """
                    static class HttpMetadataLeafFactory
                    {
                        internal static HttpMessageHandler MakeHandler()
                        {
                            return new SocketsHttpHandler
                            {
                                AllowAutoRedirect = false,
                                UseProxy = false,
                                ConnectCallback = static (context, token) => System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(System.IO.Stream.Null)
                            };
                        }
                    }
                    """;
                break;
            case "argument-effect":
                owner = owner.ReplaceSdkFixtureText("(\"Bearer\", _secret)", "(\"Bearer\", HttpMetadataEffects.HeaderParameter)", StringComparison.Ordinal);
                extra = HttpMetadataEffectInputs;
                break;
            case "initializer-effect":
                inputs = inputs.ReplaceSdkFixtureText("UseProxy = false", "UseProxy = HttpMetadataEffects.UseProxy", StringComparison.Ordinal);
                extra = HttpMetadataEffectInputs;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }

        CSharpCompilation compilation = CompileWithProductionReferencePack("using System.Net.Http;\n"
            + R2Source(R2Admission + calls + " await Task.CompletedTask;", owner + inputs + extra), "RetroDownfall.Arcanum.Infrastructure")
            .WithAssemblyName("RetroDownfall.Arcanum.Infrastructure");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return EnumerationDiscover(compilation);
    }

    private const string HttpMetadataEffectInputs = """
        static class HttpMetadataEffects
        {
            internal static string HeaderParameter
            {
                get { System.IO.File.Delete("header-argument-effect"); return "fixture-only"; }
            }

            internal static bool UseProxy
            {
                get { System.IO.File.Delete("handler-initializer-effect"); return false; }
            }
        }
        """;
}
