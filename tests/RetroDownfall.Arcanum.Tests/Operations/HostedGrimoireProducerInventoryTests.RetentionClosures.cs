using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void InheritedGuardProductionInstallationResetRetainsConstructedGuardEffects()
    {
        string? target = HostedGrimoireProducerInventory.ProbeExactInheritedRegisteredSlot(
            HostedGrimoireProducerInventory.ProductionCompilations,
            "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService", "ReadPruneLabelRefusalAsync", "EnsureUnlabeledAsync",
            "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetExistingGrimoire", "ExecuteUnderStoppedHostAuthorityAsync");

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.CovenantLabeledArtifactGuard.EnsureUnlabeledAsync", target);

        string root = HostedGrimoireProducerInventory.NonHostedCatalog.Single(static candidate =>
            candidate.EnclosingType.EndsWith(".InstallationResetService", StringComparison.Ordinal)
                && candidate.Member == "ApplyFullUnderMaintenanceLockAsync").ChainId;

        HostedProducerDiscovery<HostedProducerSite> discovery = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerSite[] observed = ObserveFullProductionRoot(discovery, "RetroDownfall.Arcanum.Infrastructure.InstallationReset.InstallationResetService", root);

        Assert.DoesNotContain(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
            && diagnostic.Detail.Contains("ICovenantLabeledArtifactGuard.EnsureUnlabeledAsync", StringComparison.Ordinal));

        Assert.Contains(observed, static site => site.EnclosingType.EndsWith(".ArtifactSensitivityLedger", StringComparison.Ordinal)
            && site.Member == "ReadLabelWithinAsync" && site.Kind == HostedProducerSiteKind.DatabaseAccess);
    }

    [Theory]
    [InlineData("fresh", false)]
    [InlineData("local", false)]
    [InlineData("callback", false)]
    [InlineData("no-work", false)]
    [InlineData("base", true)]
    [InlineData("opaque", true)]
    [InlineData("mixed", true)]
    [InlineData("rebound", true)]
    [InlineData("ref-rebound", true)]
    [InlineData("other-method-rebound", true)]
    public void InheritedGuardForwardedConstructionRequiresAnExactReceiver(string shape, bool refused)
    {
        string value = shape switch
        {
            "opaque" => "Input.Unknown()",
            "mixed" => "DateTime.UtcNow.Ticks > 0 ? new Guard() : Input.Unknown()",
            _ => "new Guard()",
        };

        string call = shape switch
        {
            "local" => "ITransactional guard = new Guard(); var owner = new Owner(guard); await owner.Run(token);",
            "callback" => "await Input.Invoke(new Owner(new Guard()), owner => owner.Run(token));",
            _ => "var owner = new Owner(" + value + "); await owner.Run(token);",
        };

        string body = shape switch
        {
            "rebound" => "guard = Input.Unknown(); return guard.CheckAsync(token).AsTask();",
            "ref-rebound" => "Input.Replace(ref guard); return guard.CheckAsync(token).AsTask();",
            _ => "return guard.CheckAsync(token).AsTask();",
        };

        string source = R2Source((shape == "no-work" ? "" : R2Admission) + call + " System.IO.File.Exists(\"guard-control\");", $$"""
            public interface IGuard { ValueTask<int> CheckAsync(CancellationToken token); }
            interface ITransactional : IGuard { }
            sealed class Owner({{(shape == "base" ? "IGuard" : "ITransactional")}} guard)
            {
                // Retention owns disposable state, so its constructed receiver bindings travel
                // through the cleanup-context lane even when this call reaches a guard method.
                private readonly System.IO.MemoryStream state = new();
                internal Task<int> Run(CancellationToken token) { using (state) { {{body}} } }
                {{(shape == "other-method-rebound" ? "internal void Replace() { guard = Input.Unknown(); }" : "")}}
            }
            sealed class Guard : ITransactional
            {
                public ValueTask<int> CheckAsync(CancellationToken token)
                {
                    System.IO.File.ReadAllText("guard-ledger-effect");
                    return ValueTask.FromResult(1);
                }
            }
            static class Input
            {
                internal static extern ITransactional Unknown();
                internal static extern void Replace(ref ITransactional guard);
                internal static Task<int> Invoke(Owner owner, Func<Owner, Task<int>> action) => action(owner);
            }
            """);

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var discovery = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        bool unresolved = discovery.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
            && diagnostic.Detail == "IGuard.CheckAsync");

        Assert.Equal(refused, unresolved);

        Assert.Contains(discovery.Items, static site => site.Callee == "System.IO.File.Exists");

        if (!refused) Assert.Contains(discovery.Items, static site => site.Callee == "System.IO.File.ReadAllText");

        if (shape == "no-work") Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.ReadAllText");
    }

    [Fact]
    public void RetentionClosure_directory_info_lookalike_does_not_gain_framework_classification()
    {
        var foreign = CompileWithProductionReferencePack("""
            namespace System.IO
            {
                public sealed class DirectoryInfo
                {
                    public DirectoryInfo(string path) { }
                    public System.Collections.Generic.IEnumerable<FileInfo> EnumerateFiles() => System.Array.Empty<FileInfo>();
                }
            }
            """).WithAssemblyName("ExternalDirectoryInfo");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success);

        var compilation = CompileWithProductionReferencePack("extern alias counterfeit; " + R2Source(R2Admission
            + "foreach (var file in new counterfeit::System.IO.DirectoryInfo(\"path\").EnumerateFiles()) { _ = file; }"), "RetroDownfall.Arcanum.Api")
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"])));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var discovery = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation],
            new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.IO.DirectoryInfo.EnumerateFiles");

        Assert.DoesNotContain(discovery.Items, static site => site.Callee == "System.IO.DirectoryInfo.EnumerateFiles");
    }

    [Theory]
    [InlineData("foreach (var file in new System.IO.DirectoryInfo(\"path\").EnumerateFiles()) { _ = file; }", true)]
    [InlineData("System.IO.FileInfo[] files = [.. new System.IO.DirectoryInfo(\"path\").EnumerateFiles(\"*.log\")];", true)]
    [InlineData("using var files = new System.IO.DirectoryInfo(\"path\").EnumerateFiles(\"*\", System.IO.SearchOption.TopDirectoryOnly).GetEnumerator(); _ = files.MoveNext();", true)]
    [InlineData("var files = new System.IO.DirectoryInfo(\"path\").EnumerateFiles(); lease.Dispose(); foreach (var file in files) { _ = file; }", false)]
    [InlineData("var files = new System.IO.DirectoryInfo(\"path\").EnumerateFiles(); lease.Dispose(); System.IO.FileInfo[] materialized = [.. files];", false)]
    [InlineData("foreach (var file in new System.IO.DirectoryInfo(\"path\").EnumerateFiles()) { lease.Dispose(); }", false)]
    [InlineData("_ = new System.IO.DirectoryInfo(\"path\").EnumerateFiles();", false)]
    public void RetentionClosure_directory_info_enumeration_keeps_lazy_work_ownership(string body, bool retained)
    {
        var discovery = R2Discover(R2Source(R2Admission + body));

        Assert.DoesNotContain(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.IO.DirectoryInfo.EnumerateFiles");

        Assert.Contains(discovery.Items, static site => site.Callee == "System.IO.DirectoryInfo.EnumerateFiles"
            && site.Kind == HostedProducerSiteKind.FileSystemRead);

        Assert.Equal(retained, !discovery.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"));

        Assert.Equal(retained, !discovery.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"));
    }

    [Theory]
    [InlineData("exact", false)]
    [InlineData("cross-assembly", false)]
    [InlineData("missing", true)]
    [InlineData("ambiguous", true)]
    [InlineData("unknown-provider", true)]
    [InlineData("rebound-provider", true)]
    [InlineData("cycle", true)]
    [InlineData("base-receiver", true)]
    [InlineData("rebound-receiver", true)]
    public void RetentionClosure_inherited_guard_alias_requires_complete_exact_registration(string shape, bool refused)
    {
        string registration = shape == "unknown-provider"
            ? "services.AddScoped<ITransactional>(sp => Unknown.Provider.GetRequiredService<ITransactional>());"
            : shape == "rebound-provider"
                ? "services.AddScoped<ITransactional>(sp => { sp = Unknown.Provider; return sp.GetRequiredService<ITransactional>(); });"
            : shape == "cycle"
            ? "services.AddScoped<ITransactional>(sp => sp.GetRequiredService<IAlias>()); services.AddScoped<IAlias>(sp => (IAlias)sp.GetRequiredService<ITransactional>());"
            : shape == "missing" ? string.Empty : "services.AddScoped<ITransactional>(sp => new Guard());";

        registration += shape switch
        {
            "ambiguous" => "services.AddScoped<ITransactional>(sp => new OtherGuard());",
            _ => string.Empty,
        };

        registration += shape switch
        {
            _ => "services.AddScoped<IGuard>(sp => sp.GetRequiredService<ITransactional>());",
        };

        string source = RegistrationSource(registration + "services.AddHostedService<Worker>();")
            .Replace("public class Worker : IHostedService", "internal sealed class Worker(ITransactional guard) : IHostedService", StringComparison.Ordinal)
            .Replace("new Worker()", "new Worker(null!)", StringComparison.Ordinal)
            .Replace("public Task StartAsync(CancellationToken token) => Task.CompletedTask;",
                "public Task StartAsync(CancellationToken token) => guard.CheckAsync(token).AsTask();", StringComparison.Ordinal) + """
            public interface IGuard { ValueTask<int> CheckAsync(CancellationToken token); }
            internal interface ITransactional : IGuard { }
            internal interface IAlias : ITransactional { }
            sealed class Guard : ITransactional
            {
                public ValueTask<int> CheckAsync(CancellationToken token) => Ledger.ReadAsync(token);
            }
            sealed class OtherGuard : ITransactional
            {
                public ValueTask<int> CheckAsync(CancellationToken token) => ValueTask.FromResult(2);
            }
            static class Ledger
            {
                public static ValueTask<int> ReadAsync(CancellationToken token)
                {
                    _ = System.IO.File.ReadAllText("ledger");
                    return ValueTask.FromResult(1);
                }
            }
            static class Unknown { public static IServiceProvider Provider => null!; }
            """;

        if (shape == "base-receiver") source = source.Replace("Worker(ITransactional guard)", "Worker(IGuard guard)", StringComparison.Ordinal);

        if (shape == "rebound-receiver") source = source.Replace("=> guard.CheckAsync(token).AsTask();", "{ guard = null!; return guard.CheckAsync(token).AsTask(); }", StringComparison.Ordinal);

        const string guardDeclaration = "public interface IGuard { ValueTask<int> CheckAsync(CancellationToken token); }";

        var contracts = CompileWithProductionReferencePack("using System.Threading; using System.Threading.Tasks; " + guardDeclaration)
            .WithAssemblyName("GuardContracts");

        if (shape == "cross-assembly") source = source.Replace(guardDeclaration, string.Empty, StringComparison.Ordinal);

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        if (shape == "cross-assembly") compilation = compilation.AddReferences(contracts.ToMetadataReference());

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        CSharpCompilation[] compilations = shape == "cross-assembly" ? [contracts, compilation] : [compilation];

        var discovery = HostedGrimoireProducerInventory.DiscoverProducerSites(compilations,
            HostedGrimoireProducerInventory.DiscoverApplicationHostedServices(compilations), [], []);

        bool unresolved = discovery.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_CALL_TARGET_UNRESOLVED"
            && diagnostic.Detail.Contains("IGuard.CheckAsync", StringComparison.Ordinal));

        Assert.Equal(refused, unresolved);

        if (!refused)
        {
            Assert.Contains(discovery.Items, static site => site.Callee == "System.IO.File.ReadAllText");
        }
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("encoder.Fallback = new CustomFallback();", true)]
    [InlineData("Escape(encoder);", true)]
    [InlineData("System.Text.Encoder alias = encoder; alias.Fallback = new CustomFallback();", true)]
    [InlineData("encoder = Other();", true)]
    public void RetentionClosure_builtin_utf8_encoder_preserves_owned_replacement_fallback(string mutation, bool refused)
    {
        string source = FixtureSource("EncodingOwner.Run();", $$"""
            static class EncodingOwner
            {
                public static void Run()
                {
                    System.Text.Encoder encoder = System.Text.Encoding.UTF8.GetEncoder();
                    {{mutation}}
                    encoder.Convert("text".AsSpan(), new byte[1024], true, out _, out _, out _);
                }
                static void Escape(System.Text.Encoder encoder) { }
                static System.Text.Encoder Other() => null!;
            }
            sealed class CustomFallback : System.Text.EncoderFallback
            {
                public override int MaxCharCount => 1;
                public override System.Text.EncoderFallbackBuffer CreateFallbackBuffer()
                {
                    System.IO.File.WriteAllText("fallback", "effect");
                    throw new System.NotSupportedException();
                }
            }
            """);

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        var discovery = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation],
            HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]), [], []);

        Assert.Equal(refused, HasCampaignEncodingDiagnostic(discovery, "System.Text.Encoder.Convert"));
    }

    [Fact]
    public void RetentionClosure_LongCount_is_a_terminal_and_keeps_iterator_effects()
    {
        string source = FixtureSource("_ = System.Linq.Enumerable.LongCount(Values.Read());", """
            static class Values
            {
                public static System.Collections.Generic.IEnumerable<int> Read()
                {
                    _ = System.IO.File.ReadAllText("iterator");
                    yield return 1;
                }
            }
            """);

        var discovery = Discover(source);

        Assert.DoesNotContain(discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Linq.Enumerable.LongCount");

        Assert.Contains(discovery.Items, static site => site.Callee == "System.IO.File.ReadAllText");
    }

    [Fact]
    public void RetentionClosure_LongCount_does_not_hide_predicate_effects()
    {
        var discovery = Discover(FixtureSource("_ = System.Linq.Enumerable.LongCount(new int[] { 1 }, value => System.IO.File.Exists(\"predicate\"));"));

        Assert.Contains(discovery.Items, static site => site.Callee == "System.IO.File.Exists");
    }

    [Theory]
    [InlineData("System.Text.Encoding.GetEncoding(65001).GetEncoder()")]
    [InlineData("((System.Text.Encoding)System.Text.Encoding.UTF8.Clone()).GetEncoder()")]
    public void RetentionClosure_encoder_rejects_unproven_encoding_origins(string origin)
    {
        var discovery = Discover(FixtureSource($"var encoder = {origin}; encoder.Convert(\"text\".AsSpan(), new byte[32], true, out _, out _, out _);"));

        Assert.True(HasCampaignEncodingDiagnostic(discovery, "System.Text.Encoder.Convert"));
    }

    [Fact]
    public void RetentionClosure_LongCount_supports_framework_groups_without_hiding_external_namesakes()
    {
        var grouped = Discover(FixtureSource("""
            var groups = System.Linq.Enumerable.GroupBy(new int[] { 1, 1, 2 }, value => value);
            foreach (var group in groups) _ = System.Linq.Enumerable.LongCount(group);
            """));

        Assert.DoesNotContain(grouped.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Linq.Enumerable.LongCount");

        var foreign = CompileWithProductionReferencePack("""
            namespace System.Linq
            {
                public static class Enumerable
                {
                    public static long LongCount<T>(System.Collections.Generic.IEnumerable<T> source) => 0;
                }
            }
            """).WithAssemblyName("ExternalEnumerable");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success);

        var compilation = CompileWithProductionReferencePack("extern alias counterfeit; " + FixtureSource(
            "_ = counterfeit::System.Linq.Enumerable.LongCount(new int[] { 1 });"), "RetroDownfall.Arcanum.Api")
            .AddReferences(MetadataReference.CreateFromImage(image.ToArray(), new MetadataReferenceProperties(aliases: ["counterfeit"])));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var rejected = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation],
            HostedGrimoireProducerInventory.DiscoverApplicationHostedServices([compilation]), [], []);

        Assert.Contains(rejected.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Linq.Enumerable.LongCount");
    }

    [Fact]
    public void RetentionClosure_production_hasher_keeps_UTF8_replacement_at_buffer_boundaries()
    {
        Type type = typeof(DataRetentionService).GetNestedType("RetentionPlanHasher", BindingFlags.NonPublic)!;

        MethodInfo append = type.GetMethod("Append", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string)])!;

        MethodInfo finish = type.GetMethod("Finish", BindingFlags.Instance | BindingFlags.NonPublic)!;

        string[] inputs = [string.Empty, "ordinary text", new string('a', 1023) + char.ConvertFromUtf32(0x1F600),
            new string('a', 1023) + (char)0xD800, new string('a', 1023) + (char)0xDC00,
            new string('a', 1023) + char.ConvertFromUtf32(0x1F600) + (char)0xD800 + "tail"];

        foreach (string input in inputs)
        {
            using IDisposable hasher = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;

            _ = append.Invoke(hasher, [input]);

            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(input)), (byte[])finish.Invoke(hasher, null)!);
        }
    }

    [Fact]
    public void RetentionClosure_production_label_guard_resolves_the_actual_inherited_slot()
    {
        string? target = HostedGrimoireProducerInventory.ProbeExactInheritedRegisteredSlot(
            HostedGrimoireProducerInventory.ProductionCompilations,
            "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService",
            "ReadPruneLabelRefusalAsync", "EnsureUnlabeledAsync");

        Assert.Equal("RetroDownfall.Arcanum.Infrastructure.Data.CovenantLabeledArtifactGuard.EnsureUnlabeledAsync", target);
    }
}
