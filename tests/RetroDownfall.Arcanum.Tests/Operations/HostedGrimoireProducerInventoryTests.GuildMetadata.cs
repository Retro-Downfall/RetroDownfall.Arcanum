using Microsoft.CodeAnalysis;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void GuildMetadataConcreteSdkDictionaryEnumerationDoesNotInvokeComparerSlots()
    {
        HostedProducerDiscovery<HostedProducerSite> result = DiscoverGuildA2A(
            "MetadataRead.Run(MetadataInput.Task.Metadata); await Task.CompletedTask;",
            "static class MetadataInput { internal static A2A.AgentTask Task = null!; } "
            + "static class MetadataRead { internal static void Run(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>? metadata) { if (metadata is null) return; foreach (var pair in metadata) { if (string.Equals(pair.Key, \"usage\", StringComparison.Ordinal)) _ = pair.Value.ValueKind; } } } "
            + "sealed class UnrelatedComparer : System.Collections.Generic.IEqualityComparer<string> { public bool Equals(string left, string right) { System.IO.File.Delete(\"unused-comparer\"); return false; } public int GetHashCode(string value) => 0; }");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code is "HOSTED_SITE_UNCLASSIFIED" or "HOSTED_DISPOSAL_TARGET_UNRESOLVED"
            && (diagnostic.Detail.Contains("Dictionary", StringComparison.Ordinal) || diagnostic.Detail.Contains("Enumerator", StringComparison.Ordinal)));

        Assert.DoesNotContain(result.Items, static site => site.Callee == "System.IO.File.Delete");
    }

    [Theory]
    [InlineData("sdk-task", false)]
    [InlineData("sdk-message", false)]
    [InlineData("sdk-status-message", false)]
    [InlineData("explicit", true)]
    [InlineData("unrelated-payload", false)]
    [InlineData("unknown", false)]
    [InlineData("unknown-setter", false)]
    [InlineData("subclass-setter", false)]
    [InlineData("comparer-setter", false)]
    public void GuildMetadataLookupRequiresAnExplicitClosedDictionaryOrigin(string shape, bool reviewed)
    {
        string expression = shape switch
        {
            "sdk-message" => "MetadataInput.Message.Metadata",
            "sdk-status-message" => "MetadataInput.Task?.Status.Message?.Metadata",
            "explicit" => "new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>()",
            "unknown" => "MetadataInput.Unknown",
            _ => "MetadataInput.Task.Metadata",
        };

        string extra = shape switch
        {
            "unrelated-payload" => "sealed class UnrelatedMetadataDictionary : System.Collections.Generic.Dictionary<string, object>, System.Collections.Generic.IDictionary<string, object> { bool System.Collections.Generic.IDictionary<string, object>.TryGetValue(string key, out object value) { System.IO.File.Delete(\"unrelated-dictionary-slot\"); value = null!; return false; } }",
            "unknown-setter" => "static class MetadataInjection { internal static void Set(A2A.AgentTask task, System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement> value) { task.Metadata = value; } }",
            "subclass-setter" => "static class MetadataInjection { internal static void Set(A2A.AgentTask task) { task.Metadata = new MetadataSubclass(); } } "
                + "sealed class MetadataSubclass : System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>, System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement> { bool System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement>.TryGetValue(string key, out System.Text.Json.JsonElement value) { System.IO.File.Delete(\"dictionary-slot\"); value = default; return false; } }",
            "comparer-setter" => "static class MetadataInjection { internal static void Set(A2A.AgentTask task) { task.Metadata = new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>(new MetadataComparer()); } } "
                + "sealed class MetadataComparer : System.Collections.Generic.IEqualityComparer<string> { public bool Equals(string left, string right) { System.IO.File.Delete(\"dictionary-comparer\"); return false; } public int GetHashCode(string value) => 0; }",
            _ => "",
        };

        string source = R2Source(R2Admission + "MetadataRead.Run(" + expression + "); await Task.CompletedTask;",
            "static class MetadataInput { internal static A2A.AgentTask Task = null!; internal static A2A.Message Message = null!; internal static System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement> Unknown = null!; } "
            + "static class MetadataRead { internal static void Run(System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement>? metadata) { if (metadata is null) return; _ = metadata.TryGetValue(\"usage\", out _); } } " + extra);

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Equal(!reviewed, result.Diagnostics.Any(static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IDictionary`2.TryGetValue"));

        if (shape == "subclass-setter") Assert.Contains(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == "MetadataSubclass");

        if (shape == "unrelated-payload") Assert.DoesNotContain(result.Items, static site => site.Callee == "System.IO.File.Delete"
            && site.EnclosingType == "UnrelatedMetadataDictionary");
    }

    [Fact]
    public void GuildMetadataReferencedDictionaryReimplementationKeepsItsOpaqueObligation()
    {
        var foreign = CompileWithProductionReferencePack(
            "public sealed class ExternalDictionary : System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>, System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement> { bool System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement>.TryGetValue(string key, out System.Text.Json.JsonElement value) { System.IO.File.Delete(\"external-dictionary-slot\"); value = default; return false; } }",
            "RetroDownfall.Arcanum.Api").WithAssemblyName("ExternalMetadataDictionary");

        using MemoryStream bytes = new();

        Assert.True(foreign.Emit(bytes).Success);

        var compilation = CompileWithProductionReferencePack(R2Source(R2Admission
            + "MetadataRead.Run(MetadataInput.Task.Metadata); await Task.CompletedTask;",
            "static class MetadataInput { internal static A2A.AgentTask Task = null!; } "
            + "static class MetadataRead { internal static void Run(System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement>? metadata) { if (metadata is null) return; _ = metadata.TryGetValue(\"usage\", out _); } }"),
            "RetroDownfall.Arcanum.Api").AddReferences(MetadataReference.CreateFromImage(bytes.ToArray()));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IDictionary`2.TryGetValue");
    }

    [Fact]
    public void GuildMetadataReferencedStringComparerKeepsItsOpaqueObligation()
    {
        var foreign = CompileWithProductionReferencePack(
            "public sealed class ExternalComparer : System.Collections.Generic.IEqualityComparer<string> { public bool Equals(string left, string right) { System.IO.File.Delete(\"external-comparer\"); return false; } public int GetHashCode(string value) => 0; } "
            + "public static class ExternalTask { public static A2A.AgentTask Create() => new() { Metadata = new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>(new ExternalComparer()) }; }",
            "RetroDownfall.Arcanum.Api").WithAssemblyName("ExternalMetadataComparer");

        using MemoryStream bytes = new();

        Assert.True(foreign.Emit(bytes).Success);

        var compilation = CompileWithProductionReferencePack(R2Source(R2Admission
            + "MetadataRead.Run(MetadataInput.Task.Metadata); await Task.CompletedTask;",
            "static class MetadataInput { internal static A2A.AgentTask Task = null!; } "
            + "static class MetadataRead { internal static void Run(System.Collections.Generic.IDictionary<string, System.Text.Json.JsonElement>? metadata) { if (metadata is null) return; _ = metadata.TryGetValue(\"usage\", out _); } }"),
            "RetroDownfall.Arcanum.Api").AddReferences(MetadataReference.CreateFromImage(bytes.ToArray()));

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IDictionary`2.TryGetValue");
    }
}
