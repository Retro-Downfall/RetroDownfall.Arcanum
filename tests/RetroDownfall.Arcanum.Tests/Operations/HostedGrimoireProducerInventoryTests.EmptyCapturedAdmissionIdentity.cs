using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("unused", false, 1)]
    [InlineData("unused", true, 1)]
    [InlineData("lambda-unused", false, 1)]
    [InlineData("lambda-unused", true, 1)]
    [InlineData("lambda-captured", false, 2)]
    [InlineData("lambda-captured", true, 2)]
    [InlineData("lambda-ref", false, 2)]
    [InlineData("lambda-ref", true, 2)]
    [InlineData("captured", false, 2)]
    [InlineData("captured", true, 2)]
    [InlineData("forwarded", false, 2)]
    [InlineData("forwarded", true, 2)]
    [InlineData("alias", false, 2)]
    [InlineData("alias", true, 2)]
    [InlineData("alias-assigned", false, 2)]
    [InlineData("alias-assigned", true, 2)]
    [InlineData("nested-capture", false, 2)]
    [InlineData("nested-capture", true, 2)]
    [InlineData("sibling-capture", false, 2)]
    [InlineData("sibling-capture", true, 2)]
    [InlineData("bound-callable", false, 2)]
    [InlineData("bound-callable", true, 2)]
    [InlineData("ref", false, 2)]
    [InlineData("ref", true, 2)]
    [InlineData("nonempty", false, 2)]
    [InlineData("nonempty", true, 2)]
    [InlineData("future-own", false, 2)]
    [InlineData("future-own", true, 2)]
    [InlineData("constructor", false, 2)]
    [InlineData("constructor", true, 2)]
    [InlineData("constructor-only", false, 2)]
    [InlineData("constructor-only", true, 2)]
    [InlineData("recovery", false, 2)]
    [InlineData("recovery", true, 2)]
    [InlineData("different-target", false, 2)]
    [InlineData("different-target", true, 2)]
    public void Empty_unused_enclosing_admission_entries_do_not_split_parent_callable_identity(string variant, bool emptyFirst, int expected)
        => AssertCapturedAdmissionIdentity(variant, emptyFirst, expected);

    [Theory]
    [InlineData("context-owner-kind", false, 1)]
    [InlineData("context-owner-kind", true, 1)]
    [InlineData("context-owner-version", false, 1)]
    [InlineData("context-owner-version", true, 1)]
    [InlineData("context-null", false, 2)]
    [InlineData("context-null", true, 2)]
    [InlineData("context-disposition", false, 2)]
    [InlineData("context-disposition", true, 2)]
    [InlineData("context-db-kind", false, 2)]
    [InlineData("context-db-kind", true, 2)]
    [InlineData("context-db-version", false, 2)]
    [InlineData("context-db-version", true, 2)]
    [InlineData("context-live-kind", false, 2)]
    [InlineData("context-live-kind", true, 2)]
    [InlineData("context-live-version", false, 2)]
    [InlineData("context-live-version", true, 2)]
    public void Owner_bound_ambient_recovery_identity_preserves_semantic_distinctions(string variant, bool firstContextFirst, int expected)
        => AssertCapturedAdmissionIdentity(variant, firstContextFirst, expected);

    private void AssertCapturedAdmissionIdentity(string variant, bool emptyFirst, int expected)
    {
        string callbackBody = variant switch
        {
            "captured" or "lambda-captured" => "System.IO.File.Delete(unused.ToString());",
            "forwarded" => "Forward(unused);",
            "alias" or "alias-assigned" => "System.IO.File.Delete(alias.ToString());",
            "nested-capture" => "System.Action nested = () => System.IO.File.Delete(unused.ToString()); nested();",
            "sibling-capture" => "ReadCaptured();",
            "bound-callable" => "other();",
            "context-live-kind" or "context-live-version" => "System.IO.File.Delete(operation.Kind);",
            _ => "System.IO.File.Delete(\"captured-identity-effect\");",
        };

        string helper = """
            public sealed class CaptureHarness
            {
                public CaptureHarness() { }
                public CaptureHarness(int marker) { }
                public static void Outer(/* REF */ object unused/* OPERATION */)
                {
                    /* CAPTURE */
                    /* CALLBACK */
                    void Alternative(/* OWN */) { System.IO.File.Delete("alternative-effect"); }
                }
                public static void Parent(/* TYPE */ work) { /* INVOKE */ }
                private static void Forward(object value) { System.IO.File.Delete(value.ToString()); }
            }
            """
            .Replace("/* CALLBACK */", variant.StartsWith("lambda-", StringComparison.Ordinal)
                ? "System.Action Callback = () => " + callbackBody
                : "void Callback(/* OWN */) { /* BODY */ }", StringComparison.Ordinal)
            .Replace("/* REF */", variant is "ref" or "lambda-ref" ? "ref" : string.Empty, StringComparison.Ordinal)
            .Replace("/* OPERATION */", variant is "context-live-kind" or "context-live-version"
                ? ", RetroDownfall.Arcanum.Core.Operations.LongRunningOperation operation" : string.Empty, StringComparison.Ordinal)
            .Replace("/* OWN */", variant == "future-own" ? "object future" : string.Empty, StringComparison.Ordinal)
            .Replace("/* BODY */", callbackBody, StringComparison.Ordinal)
            .Replace("/* CAPTURE */", variant switch
            {
                "alias" => "object alias = unused;",
                "alias-assigned" => "object alias = new(); alias = unused;",
                "sibling-capture" => "void ReadCaptured() { System.IO.File.Delete(unused.ToString()); }",
                "bound-callable" => "System.Action other = ReadCaptured; void ReadCaptured() { System.IO.File.Delete(unused.ToString()); }",
                _ => string.Empty,
            }, StringComparison.Ordinal)
            .Replace("/* TYPE */", variant == "future-own" ? "System.Action<object>" : "System.Action", StringComparison.Ordinal)
            .Replace("/* INVOKE */", variant == "future-own" ? "work(new object());" : "work();", StringComparison.Ordinal);

        var compilation = CompileWithProductionReferencePack(FixtureSource(string.Empty, helper), "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        var probe = HostedGrimoireProducerInventory.ProbeEmptyCapturedAdmissionIdentity(compilation, OrdinaryRoot(), variant, emptyFirst);

        Assert.True(probe.Complete);

        Assert.Equal(expected, probe.Tokens);

        Assert.Equal(expected, probe.Fingerprints);

        Assert.Equal(expected, probe.CacheKeys);

        Assert.Equal(expected, probe.ParentStates);

        Assert.Contains(probe.Discovery.Items, static site => site.Callee == "System.IO.File.Delete");

        if (variant is "unused" or "lambda-unused" or "context-owner-kind" or "context-owner-version")
        {
            Assert.True(probe.SameEvidence);

            HostedProducerSite effect = Assert.Single(probe.Discovery.Items, static site => site.Callee == "System.IO.File.Delete");

            Assert.Null(effect.WorkFrontierCapsuleId);

            Assert.Null(effect.EffectFrontierCapsuleId);

            Assert.Contains(probe.Discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING");

            Assert.Contains(probe.Discovery.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING");
        }
    }
}
