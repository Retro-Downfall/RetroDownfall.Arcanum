using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    private const string HttpConstructionSendOwner = """
        namespace RetroDownfall.Arcanum.Infrastructure.Guild
        {
            internal static class GuildSpendSourceCredential
            {
                internal sealed class VerifiedSource
                {
                    private readonly string _secret = "fixture-only";

                    internal string GenerationId { get; } = "generation";

                    internal async Task<HttpResponseMessage> SendAsync(HttpClient client, CancellationToken ct)
                    {
                        using HttpRequestMessage request = new(HttpMethod.Get,
                            "https://openrouter.ai/api/v1/generation?id=" + Uri.EscapeDataString(GenerationId));

                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _secret);

                        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        """;

    private const string HttpConstructionInputs = """
        public static class HttpConstructionInput
        {
            public static Func<HttpMessageHandler>? UnknownTransport { get; set; }

            internal static HttpMessageHandler UnknownHandler = null!;

            internal static HttpClient UnknownClient = null!;

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

        sealed class InstallingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                request.Content = new InstalledContent();
                return Task.FromResult(new HttpResponseMessage());
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) System.IO.File.Delete("injected-handler-dispose");
                base.Dispose(disposing);
            }
        }

        sealed class InstalledContent : HttpContent
        {
            protected override Task SerializeToStreamAsync(System.IO.Stream stream, System.Net.TransportContext? context) => Task.CompletedTask;

            protected override bool TryComputeLength(out long length) { length = 0; return true; }

            protected override void Dispose(bool disposing)
            {
                if (disposing) System.IO.File.Delete("installed-content-dispose");
                base.Dispose(disposing);
            }
        }
        """;

    [Theory]
    [InlineData("direct-null", true)]
    [InlineData("forwarded-null", true)]
    [InlineData("unknown-transport", false)]
    [InlineData("installing-handler", false)]
    [InlineData("unknown-client", false)]
    [InlineData("reassigned-handler", false)]
    [InlineData("handler-alias", false)]
    [InlineData("owning-client", false)]
    [InlineData("mutated-request", false)]
    [InlineData("callback-effect", false)]
    [InlineData("foreign-get", false)]
    [InlineData("method-builder", false)]
    [InlineData("shadow-stock-builder", false)]
    public void Http_construction_cleanup_requires_closed_actual_transport_and_request_owner(string shape, bool closed)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverHttpConstructionShape(shape);

        if (closed)
        {
            foreach (string type in new[] { "System.Net.Http.HttpRequestMessage", "System.Net.Http.HttpMessageHandler", "System.Net.Http.HttpClient" })
            {
                Assert.True(result.Items.Any(site => site.Callee == type + ".Dispose" && site.Kind == HostedProducerSiteKind.ProviderCall),
                    "Missing " + type + ".Dispose; diagnostics: " + string.Join(" | ", result.Diagnostics.Select(static diagnostic => diagnostic.Code + ": " + diagnostic.Detail)));

                Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                    && diagnostic.Detail.StartsWith(type + ".Dispose", StringComparison.Ordinal));
            }
        }
        else
        {
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Detail.StartsWith("System.Net.Http.HttpRequestMessage.Dispose", StringComparison.Ordinal));
        }

        if (shape == "installing-handler")
        {
            // This conditional delegate origin is not resolved as the registered default.
            // Its actual cleanup obligation must remain explicit, never mechanically exempted.
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Detail.StartsWith("System.Net.Http.HttpMessageHandler.Dispose", StringComparison.Ordinal));
        }

        if (shape == "callback-effect")
        {
            // The isolated deferred-callback certificate checks the effect; creating the
            // handler does not execute that callback in the main source graph.
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_HTTP_CONNECT_CERTIFICATE_UNPROVEN");

            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
                && diagnostic.Detail.StartsWith("System.Net.Http.HttpMessageHandler.Dispose", StringComparison.Ordinal));
        }
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverHttpConstructionShape(string shape, string? callsOverride = null)
    {
        string transport = shape switch
        {
            "unknown-transport" => "HttpConstructionInput.UnknownTransport",
            "installing-handler" => "static () => new InstallingHandler()",
            _ => "null",
        };

        string reader = "static class HttpConstructionReader { internal static async Task ReadAsync(Func<HttpMessageHandler>? transport, CancellationToken ct) { "
            + "using HttpMessageHandler handler = transport?.Invoke() ?? HttpConstructionInput.MakeHandler(); "
            + "using HttpClient client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }; "
            + "using HttpResponseMessage response = await new RetroDownfall.Arcanum.Infrastructure.Guild.GuildSpendSourceCredential.VerifiedSource().SendAsync(client, ct); "
            + "_ = response.StatusCode; } }";

        if (shape == "unknown-client") reader = reader.Replace(
            "new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }", "HttpConstructionInput.UnknownClient", StringComparison.Ordinal);

        if (shape == "owning-client") reader = reader.Replace("disposeHandler: false", "disposeHandler: true", StringComparison.Ordinal);

        if (shape == "handler-alias") reader = reader.Replace("using HttpClient client", "var alias = handler; using HttpClient client", StringComparison.Ordinal);

        if (shape == "reassigned-handler") reader = reader.Replace("using HttpMessageHandler handler = transport?.Invoke() ?? HttpConstructionInput.MakeHandler();",
            "HttpMessageHandler handler = transport?.Invoke() ?? HttpConstructionInput.MakeHandler(); handler = HttpConstructionInput.UnknownHandler; using (handler) {", StringComparison.Ordinal)
            .Replace("_ = response.StatusCode; } }", "_ = response.StatusCode; } } }", StringComparison.Ordinal);

        string owner = HttpConstructionSendOwner;

        if (shape == "mutated-request") owner = owner.Replace("return await client.SendAsync", "request.Content = new InstalledContent(); return await client.SendAsync", StringComparison.Ordinal);

        if (shape == "foreign-get") owner = owner.Replace("internal static class GuildSpendSourceCredential", "internal static class HttpMethod { internal static System.Net.Http.HttpMethod Get => System.Net.Http.HttpMethod.Get; } internal static class GuildSpendSourceCredential", StringComparison.Ordinal);

        if (shape == "method-builder") owner = owner.Replace("internal async Task<HttpResponseMessage> SendAsync",
            "[System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(EarlyResponseBuilder<>))] internal async Task<HttpResponseMessage> SendAsync", StringComparison.Ordinal);

        string inputs = HttpConstructionInputs;

        if (shape == "method-builder") inputs += EarlyHttpResponseBuilder;

        if (shape == "shadow-stock-builder") inputs += ShadowHttpResponseBuilder;

        if (shape == "callback-effect") inputs = inputs.Replace(
            "static (context, token) => System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(System.IO.Stream.Null)",
            "static (context, token) => { System.IO.File.Delete(\"connect-effect\"); return System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(System.IO.Stream.Null); }", StringComparison.Ordinal);

        string calls = shape == "direct-null"
            ? "using HttpMessageHandler handler = HttpConstructionInput.MakeHandler(); using HttpClient client = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan }; "
                + "using HttpResponseMessage response = await new RetroDownfall.Arcanum.Infrastructure.Guild.GuildSpendSourceCredential.VerifiedSource().SendAsync(client, token); _ = response.StatusCode;"
            : "await HttpConstructionReader.ReadAsync(" + transport + ", token);";

        calls = callsOverride ?? calls;

        CSharpCompilation compilation = CompileWithProductionReferencePack("using System.Net.Http;\n"
            + R2Source(R2Admission + calls + " await Task.CompletedTask;", owner + inputs + reader), "RetroDownfall.Arcanum.Infrastructure")
            .WithAssemblyName("RetroDownfall.Arcanum.Infrastructure");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return EnumerationDiscover(compilation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Null_and_unknown_transport_callers_do_not_borrow_each_others_cleanup_certificate(bool nullFirst)
    {
        const string known = "await HttpConstructionReader.ReadAsync(null, token); ";

        const string unknown = "await HttpConstructionReader.ReadAsync(HttpConstructionInput.UnknownTransport, token); ";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverHttpConstructionShape("forwarded-null", nullFirst ? known + unknown : unknown + known);

        Assert.Contains(result.Items, static site => site.Callee == "System.Net.Http.HttpRequestMessage.Dispose"
            && site.Kind == HostedProducerSiteKind.ProviderCall);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("System.Net.Http.HttpRequestMessage.Dispose", StringComparison.Ordinal));
    }

    private const string EarlyHttpResponseBuilder = """
        sealed class EarlyResponseBuilder<TResult>
        {
            public static EarlyResponseBuilder<TResult> Create() => new EarlyResponseBuilder<TResult>();
            public void Start<TStateMachine>(ref TStateMachine machine) where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine => machine.MoveNext();
            public void SetStateMachine(System.Runtime.CompilerServices.IAsyncStateMachine machine) { }
            public void SetResult(TResult result) { }
            public void SetException(Exception exception) { }
            public System.Threading.Tasks.Task<TResult> Task => System.Threading.Tasks.Task.FromResult<TResult>(default!);
            public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine machine)
                where TAwaiter : System.Runtime.CompilerServices.INotifyCompletion where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine { }
            public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine machine)
                where TAwaiter : System.Runtime.CompilerServices.ICriticalNotifyCompletion where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine { }
        }
        """;

    private const string ShadowHttpResponseBuilder = """
        namespace System.Runtime.CompilerServices
        {
            public sealed class AsyncTaskMethodBuilder<TResult>
            {
                public static AsyncTaskMethodBuilder<TResult> Create() => new AsyncTaskMethodBuilder<TResult>();
                public void Start<TStateMachine>(ref TStateMachine machine) where TStateMachine : IAsyncStateMachine => machine.MoveNext();
                public void SetStateMachine(IAsyncStateMachine machine) { }
                public void SetResult(TResult result) { }
                public void SetException(Exception exception) { }
                public System.Threading.Tasks.Task<TResult> Task => System.Threading.Tasks.Task.FromResult<TResult>(default!);
                public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine machine)
                    where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine { }
                public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine machine)
                    where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine { }
            }
        }
        """;
}
