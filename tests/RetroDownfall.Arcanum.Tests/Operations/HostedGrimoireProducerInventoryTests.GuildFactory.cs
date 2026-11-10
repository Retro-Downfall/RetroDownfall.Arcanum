using Microsoft.CodeAnalysis;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void GuildNamedHttpFactoryProductionProfileClosesItsAuthoredSetupAndDeferredCallback()
    {
        var result = HostedGrimoireProducerInventory.ProbeProductionNamedHttpFactoryCreation(
            "RetroDownfall.Arcanum.Infrastructure.A2A.A2AClientService", "CreateOutboundClient");

        Assert.True(result.Complete, HostedGrimoireProducerInventory.RenderDiagnosticReport(result.Diagnostics));
    }

    [Fact]
    public void GuildNamedHttpFactoryClosesOnlyItsExactAuthoredConfiguration()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNamedFactory();

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Net.Http.IHttpClientFactory.CreateClient");
    }

    [Fact]
    public void GuildNamedHttpFactoryRetainsDefaultDependencyThroughAnOwnedRelay()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNamedFactory("bound-default");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Net.Http.IHttpClientFactory.CreateClient");
    }

    [Fact]
    public void GuildNamedHttpFactoryOwnsFreshSocketThroughConnectionTransferAndEveryFailure()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNamedFactory("socket-owned");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Net.Http.IHttpClientFactory.CreateClient");
    }

    [Theory]
    [InlineData("config-effect")]
    [InlineData("config-async-void")]
    [InlineData("unknown-config")]
    [InlineData("duplicate-name")]
    [InlineData("two-collections")]
    [InlineData("conditional-registration")]
    [InlineData("unknown-name")]
    [InlineData("factory-replacement")]
    [InlineData("direct-descriptor-add")]
    [InlineData("unknown-descriptor")]
    [InlineData("filter-descriptor")]
    [InlineData("direct-descriptor-replace")]
    [InlineData("handler-argument-effect")]
    [InlineData("handler-validation-effect")]
    [InlineData("handler-static-field-effect")]
    [InlineData("handler-static-sibling-effect")]
    [InlineData("collection-object-escape")]
    [InlineData("collection-list-escape")]
    [InlineData("collection-typed-opaque")]
    [InlineData("supplied-factory")]
    [InlineData("bound-supplied-factory")]
    [InlineData("global-defaults")]
    [InlineData("global-options")]
    [InlineData("global-filter")]
    [InlineData("builder-escape")]
    [InlineData("handler-mutation")]
    [InlineData("handler-over-depth")]
    [InlineData("callback-over-work")]
    [InlineData("callback-effect")]
    [InlineData("callback-detached")]
    [InlineData("callback-recursive-factory")]
    [InlineData("callback-boxed-task")]
    [InlineData("callback-boxed-delegate-task")]
    [InlineData("callback-two-bound-contexts")]
    [InlineData("callback-async-void-helper")]
    [InlineData("unknown-callback")]
    [InlineData("unknown-stream")]
    [InlineData("socket-borrowed")]
    [InlineData("socket-leak-on-throw")]
    [InlineData("socket-fallthrough-null")]
    [InlineData("socket-unprotected-setting")]
    [InlineData("socket-bookkeeping-before-dispose")]
    public void GuildNamedHttpFactoryRejectsUnknownMutableOrEscapingComposition(string mutation)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverNamedFactory(mutation);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Net.Http.IHttpClientFactory.CreateClient");
    }

    private static HostedProducerDiscovery<HostedProducerSite> DiscoverNamedFactory(string mutation = "")
    {
        string config = "static client => { client.Timeout = Timeout.InfiniteTimeSpan; client.MaxResponseContentBufferSize = 16777216; }";

        string callback = "static (context, token) => System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(System.IO.Stream.Null)";

        string suffix = "";

        string consumerRegistration = "services.AddSingleton<NamedConsumer>();";

        if (mutation == "config-effect") config = "static client => { System.IO.File.Delete(\"config\"); client.Timeout = Timeout.InfiniteTimeSpan; client.MaxResponseContentBufferSize = 16777216; }";

        if (mutation == "config-async-void") config = "static async client => { await Task.Yield(); client.Timeout = Timeout.InfiniteTimeSpan; client.MaxResponseContentBufferSize = 16777216; }";

        if (mutation == "unknown-config") config = "FactoryInput.Configure";

        if (mutation == "callback-effect") callback = "static (context, token) => { System.IO.File.Delete(\"connect\"); return System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(System.IO.Stream.Null); }";

        if (mutation == "callback-detached") callback = "static (context, token) => { _ = Task.Run(() => System.IO.File.Delete(\"late-connect\")); return System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(System.IO.Stream.Null); }";

        if (mutation == "unknown-callback") callback = "FactoryInput.Connect";

        if (mutation is "callback-boxed-task" or "callback-boxed-delegate-task") callback = "static (context, token) => { _ = FactoryGuard.Hidden(); return System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(System.IO.Stream.Null); }";

        if (mutation == "callback-two-bound-contexts") callback = "static (context, token) => { _ = FactoryGuard.Drop(static () => 42); _ = FactoryGuard.Drop(static () => FactoryInput.Http.GetAsync(\"https://peer.invalid/\")); return System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(System.IO.Stream.Null); }";

        if (mutation == "callback-async-void-helper") callback = "static (context, token) => { FactoryGuard.Hidden(); return System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(System.IO.Stream.Null); }";

        if (mutation == "callback-recursive-factory") callback = "static (context, token) => { FactoryInput.Consumer.Use(); return System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(System.IO.Stream.Null); }";

        if (mutation == "unknown-stream") callback = "static (context, token) => System.Threading.Tasks.ValueTask.FromResult<System.IO.Stream>(FactoryInput.UnknownStream)";

        if (mutation == "duplicate-name") suffix = "services.AddHttpClient(\"A2AOutbound\");";

        if (mutation == "two-collections")
        {
            suffix = "IServiceCollection second = new ServiceCollection(); second.AddHttpClient();";

            consumerRegistration = "second.AddSingleton<NamedConsumer>();";
        }

        if (mutation == "conditional-registration") suffix = "services.AddHttpClient();";

        if (mutation == "factory-replacement") suffix = "services.AddSingleton<System.Net.Http.IHttpClientFactory>(sp => FactoryInput.UnknownFactory);";

        if (mutation == "direct-descriptor-add") suffix = "services.Add(ServiceDescriptor.Singleton<System.Net.Http.IHttpClientFactory>(sp => FactoryInput.UnknownFactory));";

        if (mutation == "direct-descriptor-replace") suffix = "services[0] = ServiceDescriptor.Singleton<System.Net.Http.IHttpClientFactory>(sp => FactoryInput.UnknownFactory);";

        if (mutation == "unknown-descriptor") suffix = "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, FactoryInput.UnknownDescriptor);";

        if (mutation == "filter-descriptor") suffix = "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(services, ServiceDescriptor.Singleton<Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter, UnknownFilter>());";

        if (mutation == "global-defaults") suffix = "services.ConfigureHttpClientDefaults(builder => builder.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan));";

        if (mutation == "global-options") suffix = "services.ConfigureAll<Microsoft.Extensions.Http.HttpClientFactoryOptions>(options => options.HttpClientActions.Add(client => System.IO.File.Delete(\"global-options\")));";

        if (mutation == "global-filter") suffix = "services.AddSingleton<Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter, UnknownFilter>();";

        if (mutation == "builder-escape") suffix = "FactoryInput.Escape(builder);";

        if (mutation == "collection-object-escape") suffix = "FactoryInput.ConfigureObject((object)services);";

        if (mutation == "collection-list-escape") suffix = "FactoryInput.ConfigureList((System.Collections.Generic.IList<ServiceDescriptor>)services);";

        if (mutation == "collection-typed-opaque") suffix = "FactoryInput.ConfigureServices(services);";

        string registration = "services.AddHttpClient(" + (mutation == "unknown-name" ? "FactoryInput.Name" : "\"A2AOutbound\"")
            + ", " + config + ").RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(static () => FactoryGuard.Create());";

        if (mutation == "builder-escape") registration = "var builder = " + registration;

        if (mutation == "conditional-registration") registration = "if (FactoryInput.Enabled) { " + registration + " }";

        string guard = "static class FactoryGuard { internal static System.Net.Http.SocketsHttpHandler Create() => new System.Net.Http.SocketsHttpHandler "
            + "{ AllowAutoRedirect = false, UseProxy = false, ConnectCallback = " + callback + " }; } ";

        if (mutation == "handler-mutation") guard = "static class FactoryGuard { internal static System.Net.Http.SocketsHttpHandler Create() { var handler = new System.Net.Http.SocketsHttpHandler "
            + "{ AllowAutoRedirect = false, UseProxy = false, ConnectCallback = " + callback + " }; handler.ConnectCallback = FactoryInput.Connect; return handler; } } ";

        if (mutation == "handler-argument-effect")
        {
            registration = registration.Replace("FactoryGuard.Create()", "FactoryGuard.Create(FactoryInput.Effect())", StringComparison.Ordinal);

            guard = guard.Replace("Create()", "Create(int ignored)", StringComparison.Ordinal);
        }

        if (mutation == "handler-validation-effect") guard = "static class FactoryGuard { internal static System.Net.Http.SocketsHttpHandler Create() { ArgumentNullException.ThrowIfNull(FactoryInput.Effect()); return new System.Net.Http.SocketsHttpHandler "
            + "{ AllowAutoRedirect = false, UseProxy = false, ConnectCallback = " + callback + " }; } } ";

        if (mutation.StartsWith("socket-", StringComparison.Ordinal))
        {
            string socket = mutation == "socket-borrowed"
                ? "new System.Net.Sockets.Socket(default(System.Net.Sockets.SafeSocketHandle)!)"
                : "new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)";

            if (mutation == "socket-unprotected-setting") socket += " { NoDelay = true }";

            string body = "socket.NoDelay = true; await socket.ConnectAsync(context.DnsEndPoint, token); "
                + (mutation == "socket-fallthrough-null" ? "if (FactoryInput.Enabled) " : "")
                + "return new System.Net.Sockets.NetworkStream(socket, true);";

            string connect = "internal static async System.Threading.Tasks.ValueTask<System.IO.Stream> Connect(System.Net.Http.SocketsHttpConnectionContext context, CancellationToken token) { var socket = " + socket + "; "
                + (mutation == "socket-leak-on-throw" ? body : "try { " + body + " } catch { "
                    + (mutation == "socket-bookkeeping-before-dispose" ? "var errors = new System.Collections.Generic.List<Exception>(); errors.Add(new Exception()); " : "")
                    + "socket.Dispose(); throw; }")
                + (mutation == "socket-fallthrough-null" ? " return System.IO.Stream.Null;" : "") + " }";

            callback = "static (context, token) => FactoryGuard.Connect(context, token)";

            guard = "static class FactoryGuard { internal static System.Net.Http.SocketsHttpHandler Create() => new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = " + callback + " }; " + connect + " } ";
        }

        if (mutation is "callback-boxed-task" or "callback-boxed-delegate-task" or "callback-async-void-helper")
        {
            string helper = mutation switch
            {
                "callback-boxed-delegate-task" => "private static readonly Func<object> Boxed = static () => FactoryInput.Http.GetAsync(\"https://peer.invalid/\"); internal static object Hidden() => Boxed();",
                "callback-async-void-helper" => "internal static async void Hidden() { await Task.Delay(1); await FactoryInput.Http.GetAsync(\"https://peer.invalid/\"); }",
                _ => "internal static object Hidden() => FactoryInput.Http.GetAsync(\"https://peer.invalid/\");",
            };

            guard = guard.Replace(" }; } ", " }; " + helper + " } ", StringComparison.Ordinal);
        }

        if (mutation is "handler-static-field-effect" or "handler-static-sibling-effect")
        {
            registration = registration.Replace("FactoryGuard.Create()", "FactoryGuard.Create(SideConfig.Value)", StringComparison.Ordinal);

            guard = guard.Replace("Create()", "Create(int ignored)", StringComparison.Ordinal)
                + "static class SideConfig { internal static readonly int Value = "
                + (mutation == "handler-static-field-effect" ? "Effect()" : "1; private static readonly int Other = Effect()")
                + "; private static int Effect() { System.IO.File.Delete(\"static-setup\"); return 1; } } ";
        }

        if (mutation == "handler-over-depth")
        {
            registration = registration.Replace("FactoryGuard.Create()", "FactoryGuard.Forward0()", StringComparison.Ordinal);

            string forwarders = string.Join(" ", Enumerable.Range(0, 140).Select(index =>
                "internal static System.Net.Http.SocketsHttpHandler Forward" + index + "() => "
                + (index == 139 ? "Create()" : "Forward" + (index + 1) + "()") + ";"));

            guard = guard.Replace(" }; } ", " }; " + forwarders + " } ", StringComparison.Ordinal);
        }

        if (mutation == "callback-over-work")
        {
            string branches = string.Join(" ", Enumerable.Range(0, 15).Select(index =>
                "internal static System.IO.Stream Branch" + index + "() { "
                + (index == 14 ? "return System.IO.Stream.Null;" : "_ = Branch" + (index + 1) + "(); return Branch" + (index + 1) + "();") + " }"));

            guard = guard.Replace("System.IO.Stream.Null)", "FactoryGuard.Branch0())", StringComparison.Ordinal)
                .Replace(" }; } ", " }; " + branches + " } ", StringComparison.Ordinal);
        }

        if (mutation == "callback-two-bound-contexts") guard = guard.Replace(" }; } ", " }; internal static object Drop(Func<object> factory) => factory(); } ", StringComparison.Ordinal);

        string invocation = mutation is "supplied-factory" or "bound-supplied-factory"
            ? "new NamedConsumer(FactoryInput.UnknownFactory).Use();" : "FactoryInput.Consumer.Use();";

        string source = "using Microsoft.Extensions.DependencyInjection; " + R2Source(R2Admission
            + invocation + " await Task.CompletedTask;", """
            sealed class NamedConsumer
            {
                private readonly System.Net.Http.IHttpClientFactory _factory;
                public NamedConsumer(System.Net.Http.IHttpClientFactory factory) { _factory = factory; }
                internal void Use() { _ = _factory.CreateClient("A2AOutbound"); }
            }
            static class FactoryInput
            {
                internal static NamedConsumer Consumer = null!;
                internal static System.Net.Http.IHttpClientFactory UnknownFactory = null!;
                internal static System.IO.Stream UnknownStream = null!;
                internal static ServiceDescriptor UnknownDescriptor = null!;
                internal static readonly System.Net.Http.HttpClient Http = new();
                internal static string Name = "A2AOutbound";
                internal static bool Enabled;
                internal static int Effect() { System.IO.File.Delete("handler-argument"); return 1; }
                internal static Action<System.Net.Http.HttpClient> Configure = null!;
                internal static Func<System.Net.Http.SocketsHttpConnectionContext, CancellationToken, System.Threading.Tasks.ValueTask<System.IO.Stream>> Connect = null!;
                internal static void Escape(Microsoft.Extensions.DependencyInjection.IHttpClientBuilder builder) { }
                internal static extern void ConfigureObject(object value);
                internal static extern void ConfigureServices(IServiceCollection value);
                internal static extern void ConfigureList(System.Collections.Generic.IList<ServiceDescriptor> value);
            }
            sealed class UnknownFilter : Microsoft.Extensions.Http.IHttpMessageHandlerBuilderFilter
            {
                public Action<Microsoft.Extensions.Http.HttpMessageHandlerBuilder> Configure(Action<Microsoft.Extensions.Http.HttpMessageHandlerBuilder> next) => next;
            }
            """
            + guard
            + "static class FactorySetup { internal static void Register(IServiceCollection services) { " + registration + suffix + consumerRegistration + " } }");

        if (mutation is "bound-default" or "bound-supplied-factory") source = source.Replace(
            "internal void Use() { _ = _factory.CreateClient(\"A2AOutbound\"); }",
            "internal void Use() { new Relay(this).Use(); } internal void CreateNamed() { _ = _factory.CreateClient(\"A2AOutbound\"); }",
            StringComparison.Ordinal) + "sealed class Relay(NamedConsumer owner) { internal void Use() => owner.CreateNamed(); }";

        return DiscoverGuildA2ACompilation(CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api"));
    }

    [Theory]
    [InlineData("\"A2AOutbound\"")]
    [InlineData("FactoryInput.Name")]
    public void GuildNamedHttpFactoryWithoutClosedCompositionRetainsItsCallbackObligation(string name)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "_ = FactoryInput.Factory.CreateClient(" + name + "); await Task.CompletedTask;",
            "static class FactoryInput { internal static System.Net.Http.IHttpClientFactory Factory = null!; internal static string Name = \"A2AOutbound\"; }");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Net.Http.IHttpClientFactory.CreateClient");
    }

    [Theory]
    [InlineData("ProgressInput.Report(null);")]
    [InlineData("ProgressInput.Forward(null);")]
    [InlineData("System.IProgress<int>? progress = null; ProgressInput.Report(progress);")]
    [InlineData("ProgressInput.Report(default(System.IProgress<int>));")]
    public void GuildAbsentProgressConditionalReportRequiresCompleteNullReceiverBindings(string invocation)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            invocation + " await Task.CompletedTask;",
            "static class ProgressInput { internal static void Forward(System.IProgress<int>? progress) => Report(progress); "
            + "internal static void Report(System.IProgress<int>? progress) => progress?.Report(1); }");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.IProgress`1.Report");
    }

    [Theory]
    [InlineData("ProgressInput.Report(ProgressInput.Unknown);")]
    [InlineData("System.IProgress<int>? progress = null; progress = ProgressInput.Unknown; ProgressInput.Report(progress);")]
    [InlineData("System.IProgress<int>? progress = null; ProgressInput.Replace(ref progress); ProgressInput.Report(progress);")]
    [InlineData("ProgressInput.Report(new System.Progress<int>(_ => System.IO.File.Delete(\"async-progress\")));")]
    [InlineData("ProgressInput.Report((System.IProgress<int>)(System.Progress<int>)(ProgressValue?)null);")]
    public void GuildProgressUnknownMutatedEscapedAndConvertedReceiversRetainCallbackObligation(string invocation)
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            invocation + " await Task.CompletedTask;",
            "static class ProgressInput { internal static System.IProgress<int>? Unknown; "
            + "internal static void Report(System.IProgress<int>? progress) => progress?.Report(1); "
            + "internal static void Replace(ref System.IProgress<int>? progress) => progress = Unknown; } "
            + "sealed class ProgressValue { public static implicit operator System.Progress<int>(ProgressValue? value) "
            + "{ System.IO.File.Delete(\"conversion\"); return new System.Progress<int>(); } }");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.IProgress`1.Report");
    }

    [Fact]
    public void GuildProgressParameterReassignmentInvalidatesIncomingNullProof()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "ProgressInput.Report(null); await Task.CompletedTask;",
            "static class ProgressInput { internal static System.IProgress<int>? Unknown; "
            + "internal static void Report(System.IProgress<int>? progress) { progress = Unknown; progress?.Report(1); } }");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.IProgress`1.Report");
    }

    [Fact]
    public void GuildProgressOrdinaryCallDoesNotAcquireConditionalNullExemption()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "ProgressInput.Report(null); await Task.CompletedTask;",
            "static class ProgressInput { internal static void Report(System.IProgress<int>? progress) => progress!.Report(1); }");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.IProgress`1.Report");
    }

    [Fact]
    public void GuildProgressForgedInterfaceDoesNotAcquireNullReportProof()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "ProgressInput.Report(null); await Task.CompletedTask;",
            "namespace Forged { interface IProgress<T> { void Report(T value); } } "
            + "static class ProgressInput { internal static void Report(Forged.IProgress<int>? progress) => progress?.Report(1); }");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
            && diagnostic.Detail == "Forged.IProgress`1.Report");
    }
}
