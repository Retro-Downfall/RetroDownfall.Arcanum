using Microsoft.CodeAnalysis;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("loop")]
    [InlineData("backward-goto")]
    [InlineData("deferred-iterator")]
    [InlineData("finally-return")]
    public void CampaignModelOwnershipRejectsMutationsBeforeARepeatedRead(string shape)
    {
        const string provider = "RetroDownfall.Arcanum.Core.Configuration.ProviderSettings";

        const string entry = "RetroDownfall.Arcanum.Core.Configuration.ModelEntry";

        const string read = "_ = selected.Models[0].Name; selected.Models = CampaignReentry.Unknown(); ";

        string repeated = shape switch
        {
            "loop" => "for (int i = 0; i < 2; i++) { " + read + " }",
            "deferred-iterator" => "System.Collections.Generic.IEnumerable<string> names = CampaignReentry.Read(selected); selected.Models = CampaignReentry.Unknown(); foreach (string name in names) { }",
            "finally-return" => "_ = CampaignReentry.Capture().Models[0].Name;",
            _ => "int i = 0; again: " + read + "if (++i < 2) goto again;",
        };

        string source = R2Source(R2Admission + provider
            + " selected = System.Text.Json.JsonSerializer.Deserialize(new byte[0], RetroDownfall.Arcanum.Core.Serialization.ConfigurationJsonContext.Default.ProviderSettings)!; "
            + repeated + "System.IO.File.Exists(\"repeated-model-control\");",
            "internal static class CampaignReentry { internal static extern System.Collections.Generic.IReadOnlyList<" + entry + "> Unknown(); "
            + "internal static System.Collections.Generic.IEnumerable<string> Read(" + provider + " selected) { _ = selected.Models[0].Name; yield return \"model\"; } "
            + "internal static " + provider + " Capture() { " + provider + " selected = System.Text.Json.JsonSerializer.Deserialize(new byte[0], RetroDownfall.Arcanum.Core.Serialization.ConfigurationJsonContext.Default.ProviderSettings)!; "
            + "try { return selected; } finally { selected.Models = Unknown(); } } }");

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IReadOnlyList`1.this[]");
    }

    [Theory]
    [InlineData("terminal", false)]
    [InlineData("before-read", true)]
    [InlineData("finally-read", true)]
    [InlineData("out-alias-mutated", true)]
    public void CampaignModelSelectionCanPublishAnOutReferenceOnlyAfterItsFinalOwnedRead(string shape, bool unresolved)
    {
        const string provider = "RetroDownfall.Arcanum.Core.Configuration.ProviderSettings";

        const string settings = "RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings";

        const string entry = "RetroDownfall.Arcanum.Core.Configuration.ModelEntry";

        string read = "_ = models[0].Name; ";

        string body = shape switch
        {
            "before-read" => "selected = candidate; " + read + "resolvedModel = name; return true;",
            "finally-read" => "try { " + read + "selected = candidate; resolvedModel = name; return true; } finally { " + read + " }",
            _ => read + "selected = candidate; resolvedModel = name; return true;",
        };

        string caller = shape == "out-alias-mutated"
            ? " selected.Models = CampaignOwnedModelSelection.Unknown(); _ = frozen.Providers[0].Models[0].Name; "
            : string.Empty;

        string source = R2Source(R2Admission
            + settings + " frozen = System.Text.Json.JsonSerializer.Deserialize(new byte[0], RetroDownfall.Arcanum.Core.Serialization.ConfigurationJsonContext.Default.ArcanumSettings)!; "
            + "CampaignOwnedModelSelection.TryResolve(frozen, out " + provider + " selected, out string name); " + caller + "System.IO.File.Exists(\"out-model-control\");",
            "internal static class CampaignOwnedModelSelection { internal static extern System.Collections.Generic.IReadOnlyList<" + entry + "> Unknown(); internal static bool TryResolve(" + settings + " configuration, out " + provider + " selected, out string resolvedModel) { "
            + provider + " candidate = configuration.Providers[0]; System.Collections.Generic.IReadOnlyList<" + entry + "> models = candidate.Models; string name = \"model\"; "
            + body + " } }");

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        Assert.Equal(unresolved, result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IReadOnlyList`1.this[]"));
    }

    [Theory]
    [InlineData("provider-clone", false)]
    [InlineData("provider-coalesce", false)]
    [InlineData("settings-clone", false)]
    [InlineData("snapshot-record", false)]
    [InlineData("array", false)]
    [InlineData("list", false)]
    [InlineData("opaque", true)]
    [InlineData("replaced-clone", true)]
    [InlineData("effectful", true)]
    [InlineData("custom-typeinfo", true)]
    [InlineData("provider-alias", true)]
    [InlineData("provider-conversion", true)]
    [InlineData("settings-conversion", true)]
    [InlineData("provider-assignment-alias", true)]
    [InlineData("provider-storage-escape", true)]
    [InlineData("settings-assignment-alias", true)]
    [InlineData("snapshot-assignment-alias", true)]
    [InlineData("settings-replaced", true)]
    [InlineData("settings-alias", true)]
    [InlineData("escaped-provider", true)]
    [InlineData("escaped-settings", true)]
    [InlineData("snapshot-replaced", true)]
    [InlineData("snapshot-alias", true)]
    [InlineData("snapshot-escaped", true)]
    [InlineData("snapshot-base", true)]
    [InlineData("snapshot-partial", true)]
    public void CampaignModelIndexerRequiresOwnedCollectionOrTypedJsonClone(string shape, bool unresolved)
    {
        const string provider = "RetroDownfall.Arcanum.Core.Configuration.ProviderSettings";

        const string entry = "RetroDownfall.Arcanum.Core.Configuration.ModelEntry";

        const string context = "RetroDownfall.Arcanum.Core.Serialization.ConfigurationJsonContext.Default";

        string acquisition = shape switch
        {
            "provider-clone" or "provider-coalesce" or "replaced-clone" =>
                provider + " selected = System.Text.Json.JsonSerializer.Deserialize(new byte[0], " + context + ".ProviderSettings)!; "
                + (shape == "replaced-clone" ? "selected.Models = CampaignModelInput.Unknown(); " : string.Empty)
                + "System.Collections.Generic.IReadOnlyList<" + entry + "> models = selected.Models"
                + (shape == "provider-coalesce" ? " ?? []" : string.Empty) + ";",
            "settings-clone" =>
                "RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings settings = System.Text.Json.JsonSerializer.Deserialize(new byte[0], " + context + ".ArcanumSettings)!; "
                + provider + " selected = settings.Providers[0]; System.Collections.Generic.IReadOnlyList<" + entry + "> models = selected.Models;",
            "provider-conversion" => provider + " selected = (" + provider + ")(CampaignProviderWrapper)System.Text.Json.JsonSerializer.Deserialize(new byte[0], " + context + ".ProviderSettings)!; "
                + "System.Collections.Generic.IReadOnlyList<" + entry + "> models = selected.Models;",
            "provider-storage-escape" => provider + " selected = System.Text.Json.JsonSerializer.Deserialize(new byte[0], " + context + ".ProviderSettings)!; "
                + "CampaignProviderHolder holder = new(); holder.Settings = selected; holder.Settings.Models = new EffectfulCampaignModels(); "
                + "System.Collections.Generic.IReadOnlyList<" + entry + "> models = selected.Models;",
            "settings-conversion" =>
                "RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings settings = (RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings)(CampaignSettingsWrapper)System.Text.Json.JsonSerializer.Deserialize(new byte[0], " + context + ".ArcanumSettings)!; "
                + provider + " selected = settings.Providers[0]; System.Collections.Generic.IReadOnlyList<" + entry + "> models = selected.Models;",
            "snapshot-record" or "snapshot-replaced" or "snapshot-alias" or "snapshot-assignment-alias" or "snapshot-escaped" or "snapshot-base" or "snapshot-partial" => "CampaignModelSnapshot snapshot = CampaignModelInput.Capture(); "
                + (shape == "snapshot-replaced" ? "snapshot.Settings.Providers[0].Models = CampaignModelInput.Unknown(); "
                    : shape == "snapshot-alias" ? "RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings alias = snapshot.Settings; alias.Providers[0].Models = CampaignModelInput.Unknown(); "
                    : shape == "snapshot-assignment-alias" ? "CampaignModelSnapshot alias = null!; alias = snapshot; alias.Settings.Providers[0].Models = new EffectfulCampaignModels(); "
                    : shape == "snapshot-escaped" ? "CampaignModelInput.MutateSnapshot(snapshot); " : string.Empty)
                + "RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings settings = snapshot.Settings; "
                + provider + " selected = settings.Providers[0]; System.Collections.Generic.IReadOnlyList<" + entry + "> models = selected.Models ?? [];",
            "custom-typeinfo" => provider + " selected = System.Text.Json.JsonSerializer.Deserialize(new byte[0], CampaignModelInput.Metadata())!; "
                + "System.Collections.Generic.IReadOnlyList<" + entry + "> models = selected.Models;",
            "provider-alias" or "provider-assignment-alias" or "escaped-provider" => provider + " selected = System.Text.Json.JsonSerializer.Deserialize(new byte[0], " + context + ".ProviderSettings)!; "
                + (shape == "provider-alias" ? provider + " alias = selected; alias.Models = CampaignModelInput.Unknown(); "
                    : shape == "provider-assignment-alias" ? provider + " alias = null!; alias = selected; alias.Models = new EffectfulCampaignModels(); "
                    : "CampaignModelInput.Mutate(selected); ")
                + "System.Collections.Generic.IReadOnlyList<" + entry + "> models = selected.Models;",
            "settings-replaced" or "settings-alias" or "settings-assignment-alias" or "escaped-settings" =>
                "RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings settings = System.Text.Json.JsonSerializer.Deserialize(new byte[0], " + context + ".ArcanumSettings)!; "
                + (shape == "settings-replaced" ? "settings.Providers[0].Models = CampaignModelInput.Unknown(); "
                    : shape == "settings-alias" ? provider + " alias = settings.Providers[0]; alias.Models = CampaignModelInput.Unknown(); "
                    : shape == "settings-assignment-alias" ? "RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings alias = null!; alias = settings; alias.Providers[0].Models = new EffectfulCampaignModels(); "
                    : "CampaignModelInput.MutateSettings(settings); ")
                + provider + " selected = settings.Providers[0]; System.Collections.Generic.IReadOnlyList<" + entry + "> models = selected.Models;",
            "array" => "System.Collections.Generic.IReadOnlyList<" + entry + "> models = new " + entry + "[] { new(\"model\") };",
            "list" => "System.Collections.Generic.IReadOnlyList<" + entry + "> models = new System.Collections.Generic.List<" + entry + "> { new(\"model\") };",
            "effectful" => "System.Collections.Generic.IReadOnlyList<" + entry + "> models = new EffectfulCampaignModels();",
            _ => "System.Collections.Generic.IReadOnlyList<" + entry + "> models = CampaignModelInput.Unknown();",
        };

        string record = shape switch
        {
            "snapshot-base" => "internal record CampaignModelBase { protected CampaignModelBase(RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings value) { CampaignModelInput.MutateSettings(value); } } "
                + "internal sealed record CampaignModelSnapshot(RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings Settings) : CampaignModelBase(Settings); ",
            "snapshot-partial" => "internal sealed partial record CampaignModelSnapshot(RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings Settings); "
                + "internal sealed partial record CampaignModelSnapshot { static CampaignModelSnapshot() { CampaignModelInput.Initialize(); } } ",
            _ => "internal sealed record CampaignModelSnapshot(RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings Settings); ",
        };

        string source = R2Source(R2Admission + acquisition + " CampaignModelReader.Read(models); System.IO.File.Exists(\"model-proof-control\");",
            "internal static class CampaignModelInput { internal static extern System.Collections.Generic.IReadOnlyList<" + entry + "> Unknown(); "
            + "internal static extern System.Text.Json.Serialization.Metadata.JsonTypeInfo<" + provider + "> Metadata(); "
            + "internal static extern void Mutate(" + provider + " value); internal static extern void MutateSettings(RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings value); internal static extern void MutateSnapshot(CampaignModelSnapshot value); internal static extern void Initialize(); "
            + "internal static CampaignModelSnapshot Capture() { RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings frozen = System.Text.Json.JsonSerializer.Deserialize(new byte[0], " + context + ".ArcanumSettings)!; return new(frozen); } } "
            + record
            + "internal sealed class CampaignProviderHolder { public " + provider + " Settings { get; set; } = null!; } "
            + "internal sealed class CampaignProviderWrapper { public static explicit operator CampaignProviderWrapper(" + provider + " value) => new(); "
            + "public static explicit operator " + provider + "(CampaignProviderWrapper value) => new() { Models = new EffectfulCampaignModels() }; } "
            + "internal sealed class CampaignSettingsWrapper { public static explicit operator CampaignSettingsWrapper(RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings value) => new(); "
            + "public static explicit operator RetroDownfall.Arcanum.Core.Configuration.ArcanumSettings(CampaignSettingsWrapper value) => new() { Providers = [new " + provider + " { Models = new EffectfulCampaignModels() }] }; } "
            + "internal static class CampaignModelReader { internal static void Read(System.Collections.Generic.IReadOnlyList<" + entry + "> models) { _ = models[0].Name; } } "
            + "internal sealed class EffectfulCampaignModels : System.Collections.Generic.IReadOnlyList<" + entry + "> { public int Count => 1; public " + entry + " this[int index] { get { System.IO.File.Delete(\"opaque-model-index\"); return new(\"model\"); } } public System.Collections.Generic.IEnumerator<" + entry + "> GetEnumerator() => throw new System.NotSupportedException(); System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator(); }");

        var compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        HostedProducerDiscovery<HostedProducerSite> result = HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);

        bool unclassified = result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == "System.Collections.Generic.IReadOnlyList`1.this[]");

        bool effectRetained = result.Items.Any(static site => site.EnclosingType == "EffectfulCampaignModels"
            && site.Callee == "System.IO.File.Delete");

        Assert.Equal(unresolved, unclassified || effectRetained);
    }
}
