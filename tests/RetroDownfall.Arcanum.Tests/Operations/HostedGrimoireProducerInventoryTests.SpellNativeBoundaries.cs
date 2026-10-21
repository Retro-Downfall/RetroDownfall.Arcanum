using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("MkdirAt")]
    [InlineData("OpenAtFixedArity")]
    [InlineData("OpenAtAppleArm64")]
    [InlineData("RenameAt")]
    [InlineData("RenameAtMac")]
    [InlineData("RenameAtLinux")]
    [InlineData("UnlinkAt")]
    [InlineData("CreateFileWindows")]
    [InlineData("NtCreateFile")]
    [InlineData("NtSetInformationFile")]
    public void Spell_native_filesystem_boundaries_require_work_and_effect_authority(string member)
    {
        const string DeclaringType = "RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells.SpellDirectory";

        string helper = "namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells { static class SpellDirectory { "
            + "[System.Runtime.InteropServices.DllImport(\"fixture\")] internal static extern int " + member + "(int argument); "
            + "internal static int Run() => " + member + "(0); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(
            "_ = " + DeclaringType + ".Run(); await Task.CompletedTask;", helper));

        string callee = DeclaringType + "." + member;

        Assert.Contains(result.Items, site => site.Callee == callee
            && site.Kind == HostedProducerSiteKind.FileSystemEffect);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == callee);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_WORK_FRONTIER_MISSING"
            && diagnostic.Detail == callee);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_EFFECT_FRONTIER_MISSING"
            && diagnostic.Detail == callee);
    }

    [Theory]
    [InlineData("MkdirAt")]
    [InlineData("OpenAtFixedArity")]
    [InlineData("OpenAtAppleArm64")]
    [InlineData("RenameAt")]
    [InlineData("RenameAtMac")]
    [InlineData("RenameAtLinux")]
    [InlineData("UnlinkAt")]
    [InlineData("CreateFileWindows")]
    [InlineData("NtCreateFile")]
    [InlineData("NtSetInformationFile")]
    public void Spell_native_boundary_names_do_not_classify_an_unreviewed_declaring_type(string member)
    {
        const string DeclaringType = "RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells.OtherSpellDirectory";

        string helper = "namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.Spells { static class OtherSpellDirectory { "
            + "[System.Runtime.InteropServices.DllImport(\"fixture\")] internal static extern int " + member + "(int argument); "
            + "internal static int Run() => " + member + "(0); } }";

        HostedProducerDiscovery<HostedProducerSite> result = R2Discover(R2Source(
            "_ = " + DeclaringType + ".Run(); await Task.CompletedTask;", helper));

        string callee = DeclaringType + "." + member;

        Assert.DoesNotContain(result.Items, site => site.Callee == callee);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == callee);
    }
}
