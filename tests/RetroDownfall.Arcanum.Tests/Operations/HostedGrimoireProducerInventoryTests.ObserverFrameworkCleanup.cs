using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void Observer_exact_interlocked_read_is_scalar_mechanics_without_admission()
    {
        CSharpCompilation compilation = ObserverFrameworkCompilation("long value = 0; _ = System.Threading.Interlocked.Read(ref value);");

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(compilation, production: false);

        Assert.Empty(result.Diagnostics);

        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("interlocked", "_ = counterfeit::System.Threading.Interlocked.Read(ref value);", "System.Threading.Interlocked.Read")]
    [InlineData("registration", "new counterfeit::System.Threading.CancellationTokenRegistration().Dispose();", "System.Threading.CancellationTokenRegistration.Dispose")]
    public void Observer_foreign_mechanical_names_do_not_receive_framework_cleanup_profiles(string shape, string body, string callee)
    {
        const string counterfeit = """
            namespace System.Threading
            {
                public static class Interlocked { public static long Read(ref long value) => value; }
                public readonly struct CancellationTokenRegistration { public void Dispose() { } }
            }
            """;

        CSharpCompilation foreign = CompileWithProductionReferencePack(counterfeit, "RetroDownfall.Arcanum.Api").WithAssemblyName("Foreign.ObserverMechanics");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success);

        CSharpCompilation compilation = CompileWithProductionReferencePack("extern alias counterfeit; " + R2Source("long value = 0; " + body + " await Task.CompletedTask;"), "RetroDownfall.Arcanum.Api")
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"])));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.NotEmpty(shape);

        Assert.True(DispatchRefused(DiscoverUnderRegime(compilation, production: false), callee));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observer_authored_mechanical_names_do_not_hide_their_effects(bool registration)
    {
        string helper = registration
            ? "namespace System.Threading { public readonly struct CancellationTokenRegistration { public void Dispose() { System.IO.File.Delete(\"authored-registration\"); } } }"
            : "namespace System.Threading { public static class Interlocked { public static long Read(ref long value) { System.IO.File.Delete(\"authored-counter\"); return value; } } }";

        string body = registration ? "new System.Threading.CancellationTokenRegistration().Dispose();" : "long value = 0; _ = System.Threading.Interlocked.Read(ref value);";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(ObserverFrameworkCompilation(body, helper), production: false);

        Assert.NotEmpty(EnumerationDeleteSites(result));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observer_explicit_registration_cleanup_retains_callback_traversal(bool callbackEffect)
    {
        string callback = callbackEffect ? "System.IO.File.Delete(\"registered-effect\");" : "_ = 1;";

        string body = "System.Threading.CancellationTokenRegistration registration = token.Register(() => { " + callback
            + " }); try { await Task.CompletedTask; } finally { registration.Dispose(); }";

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(ObserverFrameworkCompilation(body), production: false);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Threading.CancellationTokenRegistration.Dispose");

        if (callbackEffect) Assert.NotEmpty(EnumerationDeleteSites(result));
        else Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Observer_unjoined_registration_callback_remains_unowned()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverUnderRegime(
            ObserverFrameworkCompilation("_ = token.Register(() => System.IO.File.Delete(\"unjoined-observer\"));"), production: false);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.NotEmpty(EnumerationDeleteSites(result));
    }

    [Theory]
    [InlineData("field", false)]
    [InlineData("local-field", false)]
    [InlineData("local-primary", false)]
    [InlineData("helper-primary", false)]
    [InlineData("unknown-monitor", true)]
    [InlineData("authored-monitor", true)]
    [InlineData("forwarded-default", false)]
    [InlineData("forwarded-other-collection", true)]
    [InlineData("forwarded-mutated-parameter", true)]
    public void Observer_options_subscription_cleanup_requires_closed_default_monitor_origin(string shape, bool unresolved)
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(ObserverOptionsSource(shape, callbackEffect: false));

        Assert.Equal(unresolved, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && diagnostic.Detail.StartsWith("System.IDisposable.Dispose;", StringComparison.Ordinal)));
    }

    [Fact]
    public void Observer_options_subscription_disposal_does_not_join_or_hide_the_callback()
    {
        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(ObserverOptionsSource("local-primary", callbackEffect: true));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");

        Assert.NotEmpty(EnumerationDeleteSites(result));
    }

    private static CSharpCompilation ObserverFrameworkCompilation(string body, string helper = "")
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(R2Source(body + " await Task.CompletedTask;", helper), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return compilation;
    }

    private static string ObserverOptionsSource(string shape, bool callbackEffect)
    {
        string registration = "services.Configure<ObserverSettings>(static _ => { }); ";

        if (shape == "authored-monitor") registration += "services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<ObserverSettings>, AuthoredObserverMonitor>(); ";

        string receiver = shape is "local-primary" or "helper-primary" || shape.StartsWith("forwarded-", StringComparison.Ordinal) ? "options" : shape == "unknown-monitor" ? "UnknownObserverMonitor.Value" : "_options";

        string callback = callbackEffect ? "System.IO.File.Delete(\"options-listener-effect\");" : "_ = 1;";

        string subscription = receiver + ".OnChange((_, _) => { " + callback + " })";

        string body = shape == "field"
            ? "_subscription = " + subscription + "; System.IDisposable? subscription = _subscription; _subscription = null; subscription?.Dispose();"
            : "using System.IDisposable? subscription = " + subscription + "; await Task.CompletedTask;";

        string source = R2Source(shape == "helper-primary" ? "await ObserveAsync(token);" : body,
            "public sealed class ObserverSettings { } "
            + "internal sealed class AuthoredObserverMonitor : Microsoft.Extensions.Options.IOptionsMonitor<ObserverSettings> { "
            + "public ObserverSettings CurrentValue => new(); public ObserverSettings Get(string? name) => new(); "
            + "public System.IDisposable? OnChange(System.Action<ObserverSettings, string?> listener) => new AuthoredObserverSubscription(); } "
            + "internal sealed class AuthoredObserverSubscription : System.IDisposable { public void Dispose() { System.IO.File.Delete(\"authored-subscription\"); } } "
            + "internal static class UnknownObserverMonitor { internal static Microsoft.Extensions.Options.IOptionsMonitor<ObserverSettings> Value { get; set; } = null!; }")
            .Replace("services.AddHostedService<Worker>();", registration
                + "services.AddSingleton<Worker>(); services.AddHostedService<Worker>(static sp => sp.GetRequiredService<Worker>());", StringComparison.Ordinal)
            .Replace("public class Worker : IHostedService", "public sealed class Worker(Microsoft.Extensions.Options.IOptionsMonitor<ObserverSettings> options) : IHostedService", StringComparison.Ordinal)
            .Replace("public async Task StartAsync", "private readonly Microsoft.Extensions.Options.IOptionsMonitor<ObserverSettings> _options = options; private System.IDisposable? _subscription; "
                + (shape == "helper-primary" ? "private async Task ObserveAsync(CancellationToken token) { " + body + " } " : "") + "public async Task StartAsync", StringComparison.Ordinal)
            .Replace("static Worker Factory(IServiceProvider provider) => new Worker();", "static Worker Factory(IServiceProvider provider) => new Worker(provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ObserverSettings>>());", StringComparison.Ordinal);

        if (shape.StartsWith("forwarded-", StringComparison.Ordinal))
        {
            const string ownerRegistration = "services.AddSingleton<Worker>(); services.AddHostedService<Worker>(static sp => sp.GetRequiredService<Worker>());";

            source = source.Replace(ownerRegistration, "ObserverOptionsRegistration.Add("
                + (shape == "forwarded-other-collection" ? "new Microsoft.Extensions.DependencyInjection.ServiceCollection()" : "services") + ");", StringComparison.Ordinal);

            source += "internal static class ObserverOptionsRegistration { internal static void Add(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { "
                + (shape == "forwarded-mutated-parameter" ? "services = new Microsoft.Extensions.DependencyInjection.ServiceCollection(); " : "")
                + ownerRegistration + " } }";
        }

        return source + RecoveryServingRoot;
    }
}
