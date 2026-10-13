using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("admitted")]
    [InlineData("missing")]
    [InlineData("disposed")]
    [InlineData("group-disposed")]
    public void SelectedConversionOperatorRetainsCallerFrontiersAndCallableBoundary(string lifetime)
    {
        string admission = lifetime == "missing" ? "" : R2Admission;

        if (lifetime == "disposed") admission += "lease.Dispose(); ";

        if (lifetime == "group-disposed") admission += "held.Dispose(); ";

        string source = R2Source(admission
            + "Func<long> unused = () => new ConversionInput(); int selected = ConversionBridge.Forward(new ConversionInput()); _ = selected;",
            "sealed class ConversionInput { public static implicit operator int(ConversionInput value) { System.IO.File.Delete(\"selected-conversion\"); return 1; } "
            + "public static implicit operator long(ConversionInput value) { System.IO.File.WriteAllText(\"unselected-conversion\", \"must-not-execute\"); return 2; } } "
            + "static class ConversionBridge { internal static int Forward(ConversionInput value) => (value); }");

        Assert.Empty(Compile(source).GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        HostedProducerSite selected = Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Delete");

        Assert.Equal("ConversionInput", selected.EnclosingType);

        Assert.Equal("op_Implicit", selected.Member);

        Assert.DoesNotContain(result.Items, static site => site.Callee == "System.IO.File.WriteAllText");

        Assert.Equal(lifetime is "missing" or "disposed", result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete"));

        Assert.Equal(lifetime is "missing" or "group-disposed", result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING" && diagnostic.Detail == "System.IO.File.Delete"));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is
            "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_CALL_TARGET_UNRESOLVED" or "HOSTED_CALLBACK_OWNERSHIP_UNPROVEN");
    }

    [Fact]
    public void ConversionOperatorReuseKeepsDistinctEffectFrontiers()
    {
        string source = R2Source(R2Admission
            + "int first = new ConversionInput(); held.Dispose(); int second = new ConversionInput(); _ = first; _ = second;",
            "sealed class ConversionInput { public static implicit operator int(ConversionInput value) { System.IO.File.Delete(\"conversion-reuse\"); return 1; } }");

        Assert.Empty(Compile(source).GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(source);

        HostedProducerSite[] effects = result.Items.Where(static site => site.Callee == "System.IO.File.Delete").ToArray();

        Assert.Equal(2, effects.Length);

        Assert.Single(effects, static site => site.EffectFrontierCapsuleId is not null);

        Assert.Single(effects, static site => site.EffectFrontierCapsuleId is null);

        Assert.All(effects, static site => Assert.NotNull(site.WorkFrontierCapsuleId));

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == "System.IO.File.Delete");
    }
}
