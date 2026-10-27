using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using Microsoft.CodeAnalysis.CSharp.Syntax;

using RetroDownfall.Arcanum.Tests.Support;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedClosureActualRetentionRecoveryRetainsTheLoggedCheckpointZeroTerminalProof()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerInventoryDiagnostic[] failures = [.. result.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "HOSTED_AGGREGATE_PROOF_MISSING"
                && diagnostic.Identity.Contains("/recovery@data-retention-mutation:0/", StringComparison.Ordinal)
                && diagnostic.Identity.Contains("/DataRetentionMutationRecoveryHandler.cs:", StringComparison.Ordinal)
            || diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Identity.Contains("/DataRetentionService.cs:", StringComparison.Ordinal)
                && diagnostic.Detail == "Microsoft.Extensions.Logging.LoggerExtensions.Log")];

        Assert.True(failures.Length == 0, string.Join(global::System.Environment.NewLine, failures));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedClosureActualRetentionWeakTableFactoryCompletesSynchronously()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        HostedProducerInventoryDiagnostic[] failures = [.. result.Diagnostics.Where(static diagnostic =>
            diagnostic.Identity.Contains("/DataRetentionService.Pruning.cs:", StringComparison.Ordinal)
                && diagnostic.Detail.StartsWith("System.Runtime.CompilerServices.ConditionalWeakTable`2.GetValue", StringComparison.Ordinal)
                && diagnostic.Code is "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_SITE_UNCLASSIFIED")];

        Assert.True(failures.Length == 0, string.Join(global::System.Environment.NewLine, failures));
    }

    [Fact]
    [Trait("Category", "HostedProducerProductionAnalysis")]
    public void HostedClosureActualScalarOperationsRetainClosedFrameworkSemantics()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.ProductionSiteDiscovery;

        string[] ownedMembers =
        [
            "System.Collections.Generic.LinkedList`1.First",
            "System.Collections.Generic.LinkedList`1.RemoveFirst",
            "System.Collections.Generic.LinkedList`1.AddLast",
            "System.TimeProvider.GetElapsedTime",
            "System.ClientModel.ClientResultException.Status",
            "System.Net.Http.HttpRequestException.StatusCode",
            "System.FormattableString.Invariant",
        ];

        HostedProducerInventoryDiagnostic[] failures = [.. result.Diagnostics.Where(diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && ownedMembers.Contains(diagnostic.Detail, StringComparer.Ordinal))];

        Assert.True(failures.Length == 0, string.Join(global::System.Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("direct", true)]
    [InlineData("wrapped", true)]
    [InlineData("replaced", false)]
    [InlineData("assigned", false)]
    [InlineData("incremented", false)]
    public void HostedClosureReferenceRecoveryTerminalRequiresTheActualReturnedResult(string shape, bool proved)
    {
        string source = HostedClosureRecoverySource(shape);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static item => item.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture([compilation], includeOwnerRoot: false);

        Assert.Equal(!proved, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_AGGREGATE_PROOF_MISSING"
            && diagnostic.Identity.Contains("/recovery@data-retention-mutation:0/", StringComparison.Ordinal)));
    }

    [Fact]
    public void HostedClosureReferenceRecoveryWrapperDoesNotHideItsExternalEffect()
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(HostedClosureRecoverySource("effect"), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static item => item.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = DiscoverRecoveryMatrixFixture([compilation], includeOwnerRoot: false);

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == "RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService");

        Assert.Null(effect.EffectFrontierCapsuleId);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
    }

    private static string HostedClosureRecoverySource(string shape)
    {
        string source = RecoveryMatrixFixture().Replace("public const string Db = \"db\";", "public const string Db = \"data-retention-mutation\";", StringComparison.Ordinal)
            .Replace("LongRunningOperationKinds.External, 0);", "LongRunningOperationKinds.Db, 0);", StringComparison.Ordinal);

        SyntaxNode root = CSharpSyntaxTree.ParseText(source).GetRoot();

        MethodDeclarationSyntax recover = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(static type => type.Identifier.ValueText == "DbRecoveryHandler").Members.OfType<MethodDeclarationSyntax>()
            .Single(static method => method.Identifier.ValueText == "RecoverAsync");

        root = root.ReplaceNode(recover, recover.WithBody(SyntaxFactory.Block(SyntaxFactory.ParseStatement(
            "return new RetroDownfall.Arcanum.Infrastructure.Data.DataRetentionService().RecoverMutationAsync(operation, cancellationToken);"))));

        string returned = shape == "direct" ? "LongRunningOperationRecoveryResult.Abandoned(\"never-started\")"
            : "Wrap(LongRunningOperationRecoveryResult.Abandoned(\"never-started\"))";

        string wrapper = shape switch
        {
            "replaced" => "return LongRunningOperationRecoveryResult.Abandoned(\"different\");",
            "assigned" => "result = LongRunningOperationRecoveryResult.Abandoned(\"different\"); return result;",
            "incremented" => "result++; return result;",
            "effect" => "System.IO.File.Delete(\"recovery-wrapper-effect\"); return result;",
            _ => "return result;",
        };

        return root.ToFullString() + $$"""
            namespace RetroDownfall.Arcanum.Core.Operations
            {
                public sealed record LongRunningOperationRecoveryResult(string Reason)
                {
                    public static LongRunningOperationRecoveryResult Abandoned(string reason) => new(reason);

                    public static LongRunningOperationRecoveryResult operator ++(LongRunningOperationRecoveryResult result) => new("incremented");
                }
            }
            namespace RetroDownfall.Arcanum.Infrastructure.Data
            {
                using RetroDownfall.Arcanum.Core.Operations;
                internal sealed class DataRetentionService
                {
                    internal async Task<LongRunningOperationRecoveryResult> RecoverMutationAsync(LongRunningOperation operation, CancellationToken cancellationToken)
                    {
                        if (operation.CheckpointVersion == 0)
                        {
                            return {{returned}};
                        }
                        await Task.CompletedTask;
                        return LongRunningOperationRecoveryResult.Abandoned("other-version");
                    }
                    private static LongRunningOperationRecoveryResult Wrap(LongRunningOperationRecoveryResult result)
                    {
                        {{wrapper}}
                    }
                }
            }
            """;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostedClosureReferenceStructuredLogAcceptsOnlyClosedScalarArguments(bool hostile)
    {
        string statement = "Microsoft.Extensions.Logging.ILogger logger = null!; Microsoft.Extensions.Logging.LoggerExtensions.Log(logger, "
            + "Microsoft.Extensions.Logging.LogLevel.Information, \"closed {Value}\", " + (hostile ? "new DangerousFormatter()" : "(int?)42, System.Guid.Empty, \"safe\"") + ");";

        const string helper = "internal sealed class DangerousFormatter : System.IFormattable { public string ToString(string? format, System.IFormatProvider? provider) { System.IO.File.Delete(\"log-format\"); return \"formatted\"; } }";

        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover(statement, helper);

        Assert.Equal(hostile, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "Microsoft.Extensions.Logging.LoggerExtensions.Log"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostedClosureReferenceWeakTableFactoryRetainsTheCallersWorkAndEffect(bool admitted)
    {
        string body = (admitted ? R2Admission : string.Empty)
            + "var table = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>(); "
            + "_ = table.GetValue(new object(), key => { System.IO.File.Delete(\"weak-factory\"); return new object(); });";

        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover(body);

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Equal(admitted, effect.WorkFrontierCapsuleId is not null);

        Assert.Equal(admitted, effect.EffectFrontierCapsuleId is not null);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN"
            && diagnostic.Detail.StartsWith("System.Runtime.CompilerServices.ConditionalWeakTable`2.GetValue", StringComparison.Ordinal));
    }

    [Fact]
    public void HostedClosureReferenceWeakTableDoesNotJoinATaskStoredAsItsValue()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover(R2Admission
            + "var table = new System.Runtime.CompilerServices.ConditionalWeakTable<object, Task>(); "
            + "_ = table.GetValue(new object(), key => Task.Run(() => System.IO.File.Delete(\"weak-detached-task\"))); ");

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Null(effect.WorkFrontierCapsuleId);

        Assert.Null(effect.EffectFrontierCapsuleId);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN" or "HOSTED_SITE_WORK_FRONTIER_MISSING");
    }

    [Fact]
    public void HostedClosureReferenceWeakTableUnknownFactoryRemainsRefused()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover(R2Admission
            + "var table = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>(); "
            + "System.Runtime.CompilerServices.ConditionalWeakTable<object, object>.CreateValueCallback callback = null!; _ = table.GetValue(new object(), callback);");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Theory]
    [InlineData("var list = new System.Collections.Generic.LinkedList<System.Guid>(); list.AddLast(System.Guid.Empty); _ = list.First; list.RemoveFirst();")]
    [InlineData("System.ClientModel.ClientResultException error = null!; _ = error.Status;")]
    [InlineData("System.Net.Http.HttpRequestException error = null!; _ = error.StatusCode;")]
    [InlineData("_ = System.FormattableString.Invariant($\"{(int?)42}: {System.Guid.Empty} / {System.TimeSpan.Zero}\");")]
    [InlineData("_ = System.TimeProvider.System.GetElapsedTime(1, 2);")]
    public void HostedClosureReferenceExactScalarOperationsNeedNoUnknownBoundary(string body)
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover(body);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");
    }

    [Fact]
    public void HostedClosureReferenceLinkedListValueEqualityRemainsAnUnknownBoundary()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover(
            "var list = new System.Collections.Generic.LinkedList<object>(); _ = list.Remove(new object());");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.LinkedList`1.Remove");
    }

    [Fact]
    public void HostedClosureReferenceElapsedTimeDoesNotHideAnAuthoredFrequencyGetter()
    {
        const string helper = "internal sealed class DangerousClock : System.TimeProvider { public override long TimestampFrequency { get { global::System.IO.File.Delete(\"clock-frequency\"); return 1; } } }";

        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover(R2Admission
            + "System.TimeProvider clock = new DangerousClock(); _ = clock.GetElapsedTime(1, 2);", helper);

        Assert.True(result.Items.Any(static site => site.Callee == "System.IO.File.Delete")
            || result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail is "System.TimeProvider.GetElapsedTime" or "System.TimeProvider.TimestampFrequency"));
    }

    [Theory]
    [InlineData("System.FormattableString.Invariant($\"{new DangerousFormatter()}\")")]
    [InlineData("System.FormattableString.Invariant(new DangerousFormattable())")]
    public void HostedClosureReferenceInvariantFormattingDoesNotHideAuthoredFormatting(string expression)
    {
        const string helper = "internal sealed class DangerousFormatter : System.IFormattable { public string ToString(string? format, System.IFormatProvider? provider) { System.IO.File.Delete(\"invariant-hole\"); return \"formatted\"; } } "
            + "internal sealed class DangerousFormattable : System.FormattableString { public override string Format => \"format\"; public override object[] GetArguments() => []; public override int ArgumentCount => 0; public override object GetArgument(int index) => null!; public override string ToString(System.IFormatProvider? provider) { System.IO.File.Delete(\"invariant-override\"); return \"formatted\"; } }";

        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover(R2Admission + "_ = " + expression + ";", helper);

        Assert.True(result.Items.Any(static site => site.Callee == "System.IO.File.Delete")
            || result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "System.FormattableString.Invariant"));
    }

    private static HostedProducerDiscovery<HostedProducerSite> HostedClosureReferenceDiscover(string body, string extra = "", string prefix = "")
    {
        CSharpCompilation compilation = CompileWithProductionReferencePack(prefix + R2Source(body + " await Task.CompletedTask;", extra), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static item => item.Severity == DiagnosticSeverity.Error));

        return HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);
    }

    [Fact]
    public void HostedClosureReferenceElapsedTimeUnknownReceiverRemainsRefused()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover("System.TimeProvider clock = null!; _ = clock.GetElapsedTime(1, 2);");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail is "System.TimeProvider.GetElapsedTime" or "System.TimeProvider.TimestampFrequency");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostedClosureReferenceScalarArgumentsRetainTheirOwnGetterEffects(bool logging)
    {
        string call = logging
            ? "Microsoft.Extensions.Logging.ILogger logger = null!; Microsoft.Extensions.Logging.LoggerExtensions.Log(logger, Microsoft.Extensions.Logging.LogLevel.Information, \"value {Value}\", PrimitiveSource.Value);"
            : "_ = System.FormattableString.Invariant($\"{PrimitiveSource.Value}\");";

        const string helper = "internal static class PrimitiveSource { internal static int Value { get { System.IO.File.Delete(\"scalar-getter\"); return 42; } } }";

        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover(R2Admission + call, helper);

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.NotNull(effect.WorkFrontierCapsuleId);

        Assert.NotNull(effect.EffectFrontierCapsuleId);
    }

    [Fact]
    public void HostedClosureReferenceStructuredLogDoesNotTrustAnAuthoredLogger()
    {
        const string helper = "internal sealed class DangerousLogger : Microsoft.Extensions.Logging.ILogger { public System.IDisposable? BeginScope<TState>(TState state) where TState : notnull => null; public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true; public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state, System.Exception? exception, System.Func<TState, System.Exception?, string> formatter) { System.IO.File.Delete(\"logger-effect\"); } }";

        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover(R2Admission
            + "Microsoft.Extensions.Logging.LoggerExtensions.Log(new DangerousLogger(), Microsoft.Extensions.Logging.LogLevel.Information, \"value {Value}\", 42);", helper);

        Assert.True(result.Items.Any(static site => site.Callee == "System.IO.File.Delete")
            || result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail == "Microsoft.Extensions.Logging.LoggerExtensions.Log"));
    }

    [Theory]
    [InlineData("status", "counterfeit::System.ClientModel.ClientResultException error = null!; _ = error.Status;", "System.ClientModel.ClientResultException.Status")]
    [InlineData("clock", "_ = counterfeit::System.TimeProvider.System.GetElapsedTime(1, 2);", "System.TimeProvider.GetElapsedTime")]
    [InlineData("list", "var list = new counterfeit::System.Collections.Generic.LinkedList<int>(); list.RemoveFirst();", "System.Collections.Generic.LinkedList`1.RemoveFirst")]
    [InlineData("weak", "var table = new counterfeit::System.Runtime.CompilerServices.ConditionalWeakTable<object, object>(); _ = table.GetValue(new object(), key => new object());", "System.Runtime.CompilerServices.ConditionalWeakTable`2.GetValue")]
    public void HostedClosureReferenceForeignLookalikesRemainUnclassified(string kind, string body, string boundary)
    {
        string foreignSource = kind switch
        {
            "status" => "namespace System.ClientModel { public sealed class ClientResultException { public int Status => 503; } }",
            "clock" => "namespace System { public sealed class TimeProvider { public static TimeProvider System => new(); public TimeSpan GetElapsedTime(long start, long end) => default; } }",
            "weak" => "namespace System.Runtime.CompilerServices { public sealed class ConditionalWeakTable<TKey, TValue> where TKey : class where TValue : class { public delegate TValue CreateValueCallback(TKey key); public TValue GetValue(TKey key, CreateValueCallback callback) => callback(key); } }",
            _ => "namespace System.Collections.Generic { public sealed class LinkedList<T> { public void RemoveFirst() { } } }",
        };

        CSharpCompilation foreign = CompileWithProductionReferencePack(foreignSource, "RetroDownfall.Arcanum.Api").WithAssemblyName("Unreviewed.ScalarLookalikes");

        using MemoryStream image = new();

        Assert.True(foreign.Emit(image).Success);

        MetadataReference reference = MetadataReference.CreateFromImage(image.ToArray(),
            MetadataReferenceProperties.Assembly.WithAliases(System.Collections.Immutable.ImmutableArray.Create("counterfeit")));

        CSharpCompilation compilation = CompileWithProductionReferencePack("extern alias counterfeit; " + R2Source(body + " await Task.CompletedTask;"), "RetroDownfall.Arcanum.Api").AddReferences(reference);

        Assert.Empty(compilation.GetDiagnostics().Where(static item => item.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED" && diagnostic.Detail == boundary);
    }

    [Fact]
    public void HostedClosureReferenceWeakTableTaskValueStillOwnsImmediateFactoryExecution()
    {
        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover(R2Admission
            + "var table = new System.Runtime.CompilerServices.ConditionalWeakTable<object, Task>(); "
            + "_ = table.GetValue(new object(), key => { System.IO.File.Delete(\"weak-immediate-effect\"); return Task.CompletedTask; });");

        HostedProducerSite effect = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.NotNull(effect.WorkFrontierCapsuleId);

        Assert.NotNull(effect.EffectFrontierCapsuleId);
    }

    [Theory]
    [InlineData("system", true)]
    [InlineData("missing", false)]
    [InlineData("unknown", false)]
    [InlineData("competing", false)]
    [InlineData("explicit-unknown", false)]
    [InlineData("factory", false)]
    [InlineData("descriptor", false)]
    [InlineData("reassigned", false)]
    public void HostedClosureReferenceDiClockRequiresAnExactNonemptyActivationWitness(string shape, bool proved)
    {
        string registrations = shape == "missing" ? "" : shape == "unknown"
            ? "services.AddSingleton<System.TimeProvider>(new DangerousClock());"
            : "services.AddSingleton(System.TimeProvider.System);";

        if (shape == "competing") registrations += "services.AddSingleton<System.TimeProvider>(new DangerousClock());";

        if (shape == "factory") registrations += "services.AddSingleton<System.TimeProvider>(static _ => new DangerousClock());";

        if (shape == "descriptor") registrations += "services.Add(Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<System.TimeProvider>(new DangerousClock()));";

        string body = shape == "explicit-unknown" ? "new ClockConsumer(null!).Run();" : "consumer.Run();";

        string source = R2Source(body + " await Task.CompletedTask;",
            "public sealed class ClockConsumer(System.TimeProvider clock) { public void Run() { "
            + (shape == "reassigned" ? "clock = new DangerousClock(); " : "") + "_ = clock.GetElapsedTime(1, 2); } } "
            + "internal sealed class DangerousClock : System.TimeProvider { public override long TimestampFrequency { get { global::System.IO.File.Delete(\"di-clock-frequency\"); return 1; } } }")
            .Replace("public class Worker : IHostedService", "public class Worker(ClockConsumer consumer) : IHostedService", StringComparison.Ordinal)
            .Replace("=> new Worker();", "=> new Worker(null!);", StringComparison.Ordinal)
            .Replace("services.AddHostedService<Worker>();", registrations + "services.AddSingleton<ClockConsumer>(); services.AddHostedService<Worker>();", StringComparison.Ordinal);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static item => item.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Equal(!proved, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.TimeProvider.GetElapsedTime"));
    }

    [Theory]
    [InlineData("clean", true)]
    [InlineData("assigned", false)]
    [InlineData("reference", false)]
    public void HostedClosureReferencePrimaryClockMustRemainStableAcrossPartialDeclarations(string shape, bool proved)
    {
        string source = R2Source("consumer.Reset(); consumer.Run(); await Task.CompletedTask;",
            "public sealed partial class ClockConsumer(System.TimeProvider clock) { public void Run() { _ = clock.GetElapsedTime(1, 2); } } "
            + "internal sealed class DangerousClock : System.TimeProvider { public override long TimestampFrequency { get { global::System.IO.File.Delete(\"partial-clock-frequency\"); return 1; } } }")
            .Replace("public class Worker : IHostedService", "public class Worker(ClockConsumer consumer) : IHostedService", StringComparison.Ordinal)
            .Replace("=> new Worker();", "=> new Worker(null!);", StringComparison.Ordinal)
            .Replace("services.AddHostedService<Worker>();", "services.AddSingleton(System.TimeProvider.System); services.AddSingleton<ClockConsumer>(); services.AddHostedService<Worker>();", StringComparison.Ordinal);

        string reset = shape switch
        {
            "assigned" => "clock = new DangerousClock();",
            "reference" => "Replace(ref clock);",
            _ => "",
        };

        SyntaxTree other = CSharpSyntaxTree.ParseText("public sealed partial class ClockConsumer { public void Reset() { "
            + reset + " } private static extern void Replace(ref System.TimeProvider value); }", path: "src/OtherPart.cs");

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api").AddSyntaxTrees(other);

        Assert.Empty(compilation.GetDiagnostics().Where(static item => item.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Equal(!proved, result.Items.Any(static site => site.Callee == "System.IO.File.Delete")
            || result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail is "System.TimeProvider.GetElapsedTime" or "System.TimeProvider.TimestampFrequency"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostedClosureReferenceReducedStructuredLogKeepsTheScalarBoundary(bool hostile)
    {
        string body = "Microsoft.Extensions.Logging.ILogger logger = null!; logger.Log(Microsoft.Extensions.Logging.LogLevel.Information, \"value {Value}\", "
            + (hostile ? "new DangerousFormatter()" : "42") + ");";

        const string helper = "internal sealed class DangerousFormatter : System.IFormattable { public string ToString(string? format, System.IFormatProvider? provider) { System.IO.File.Delete(\"reduced-format\"); return \"formatted\"; } }";

        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover(body, helper, "using Microsoft.Extensions.Logging; ");

        Assert.Equal(hostile, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "Microsoft.Extensions.Logging.LoggerExtensions.Log"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostedClosureReferenceElapsedTimeParameterMustRetainItsSystemOrigin(bool reassigned)
    {
        string helper = "internal static class ClockConsumer { internal static void Run(System.TimeProvider clock) { "
            + (reassigned ? "clock = new DangerousClock(); " : "") + "_ = clock.GetElapsedTime(1, 2); } } "
            + "internal sealed class DangerousClock : System.TimeProvider { public override long TimestampFrequency { get { global::System.IO.File.Delete(\"reassigned-frequency\"); return 1; } } }";

        HostedProducerDiscovery<HostedProducerSite> result = HostedClosureReferenceDiscover("ClockConsumer.Run(System.TimeProvider.System);", helper);

        bool refused = result.Items.Any(static site => site.Callee == "System.IO.File.Delete")
            || result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail is "System.TimeProvider.GetElapsedTime" or "System.TimeProvider.TimestampFrequency");

        Assert.Equal(reassigned, refused);
    }

    [Fact]
    public void HostedClosureReferenceActualWarningClockKeepsItsSystemOrigin()
    {
        string path = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src/RetroDownfall.Arcanum.Api/Intelligence/CampaignRollupDeferralWarnings.cs");

        string actual = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(static declaration => declaration.Identifier.ValueText == "CampaignRollupDeferralWarnings").ToFullString();

        string source = "using System.Collections.Generic; " + R2Source(
            "var warnings = new CampaignRollupDeferralWarnings(System.TimeProvider.System); _ = warnings.TryAcquire(System.Guid.Empty); await Task.CompletedTask;", actual);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static item => item.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites([compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.TimeProvider.GetElapsedTime");
    }
}
