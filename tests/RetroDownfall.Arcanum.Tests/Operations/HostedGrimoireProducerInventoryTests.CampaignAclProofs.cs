using Microsoft.CodeAnalysis;

using Microsoft.CodeAnalysis.CSharp;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Fact]
    public void CampaignAclFreshDescriptorConstructionRetainsDirectoryCreationEffect()
    {
        HostedProducerDiscovery<HostedProducerSite> result = CampaignAclDiscover(
            CampaignAclSid
                + "System.Security.AccessControl.DirectorySecurity security = new(); "
                + "security.SetOwner(sid); "
                + "security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); "
                + "security.AddAccessRule(" + CampaignAclRule("sid") + "); "
                + "new System.IO.DirectoryInfo(\"acl-directory\").Create(security);");

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && IsCampaignAclBoundary(diagnostic.Detail));

        Assert.Contains(result.Items, static site =>
            site.Kind == HostedProducerSiteKind.FileSystemEffect
            && site.Callee == "System.IO.FileSystemAclExtensions.Create");
    }

    [Theory]
    [InlineData("path")]
    [InlineData("opaque")]
    [InlineData("reassigned")]
    public void CampaignAclMutationsRequireFreshConcreteDescriptorProvenance(string shape)
    {
        string initializer = shape switch
        {
            "path" => "new System.Security.AccessControl.DirectorySecurity(\"existing-path\", System.Security.AccessControl.AccessControlSections.All)",
            "opaque" => "CampaignAclInput.UnknownSecurity()",
            _ => "new System.Security.AccessControl.DirectorySecurity()",
        };

        string reassignment = shape == "reassigned"
            ? "security = CampaignAclInput.UnknownSecurity(); "
            : string.Empty;

        HostedProducerDiscovery<HostedProducerSite> result = CampaignAclDiscover(
            CampaignAclSid
                + "System.Security.AccessControl.DirectorySecurity security = " + initializer + "; "
                + reassignment
                + "security.SetOwner(sid); "
                + "security.SetAccessRuleProtection(true, false); "
                + "security.AddAccessRule(" + CampaignAclRule("sid") + ");");

        AssertCampaignAclUnclassified(result, "System.Security.AccessControl.ObjectSecurity.SetOwner");

        AssertCampaignAclUnclassified(result, "System.Security.AccessControl.ObjectSecurity.SetAccessRuleProtection");

        AssertCampaignAclUnclassified(result, "System.Security.AccessControl.FileSystemSecurity.AddAccessRule");

        if (shape == "path")
        {
            AssertCampaignAclUnclassified(result, "System.Security.AccessControl.DirectorySecurity..ctor");
        }
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("account")]
    public void CampaignAclIdentityTranslationRequiresExactSealedSecurityIdentifier(string shape)
    {
        string identity = shape == "opaque"
            ? "CampaignAclInput.UnknownIdentity()"
            : "new System.Security.Principal.NTAccount(\"fixture-account\")";

        HostedProducerDiscovery<HostedProducerSite> result = CampaignAclDiscover(
            "System.Security.Principal.IdentityReference identity = " + identity + "; "
                + "System.Security.AccessControl.DirectorySecurity security = new(); "
                + "security.SetOwner(identity); "
                + "security.AddAccessRule(" + CampaignAclRule("identity") + ");");

        AssertCampaignAclUnclassified(result, "System.Security.AccessControl.ObjectSecurity.SetOwner");

        AssertCampaignAclUnclassified(result, "System.Security.AccessControl.FileSystemAccessRule..ctor");

        AssertCampaignAclUnclassified(result, "System.Security.AccessControl.FileSystemSecurity.AddAccessRule");
    }

    [Fact]
    public void CampaignAclOpaqueObjectSecurityAndUnknownRulesRemainUnclassified()
    {
        HostedProducerDiscovery<HostedProducerSite> result = CampaignAclDiscover(
            CampaignAclSid
                + "System.Security.AccessControl.ObjectSecurity opaque = CampaignAclInput.UnknownObjectSecurity(); "
                + "opaque.SetOwner(sid); opaque.SetAccessRuleProtection(true, false); "
                + "System.Security.AccessControl.DirectorySecurity security = new(); "
                + "security.AddAccessRule(CampaignAclInput.UnknownRule());");

        AssertCampaignAclUnclassified(result, "System.Security.AccessControl.ObjectSecurity.SetOwner");

        AssertCampaignAclUnclassified(result, "System.Security.AccessControl.ObjectSecurity.SetAccessRuleProtection");

        AssertCampaignAclUnclassified(result, "System.Security.AccessControl.FileSystemSecurity.AddAccessRule");
    }

    [Theory]
    [InlineData("false", "false")]
    [InlineData("true", "true")]
    [InlineData("CampaignAclInput.UnknownFlag()", "false")]
    public void CampaignAclProtectionProofRequiresExactOwnerOnlyFlags(string isProtected, string preserveInheritance)
    {
        HostedProducerDiscovery<HostedProducerSite> result = CampaignAclDiscover(
            "System.Security.AccessControl.DirectorySecurity security = new(); "
                + "security.SetAccessRuleProtection(" + isProtected + ", " + preserveInheritance + ");");

        AssertCampaignAclUnclassified(result, "System.Security.AccessControl.ObjectSecurity.SetAccessRuleProtection");
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("descriptor")]
    [InlineData("rule")]
    public void CampaignAclProvenanceRejectsAuthoredConversionReplacement(string shape)
    {
        string identity = "(System.Security.Principal.IdentityReference)(System.Security.Principal.NTAccount)(CampaignAclIdentityWrapper)sid";

        string body = shape switch
        {
            "identity" => CampaignAclSid
                + "System.Security.AccessControl.DirectorySecurity security = new(); "
                + "security.SetOwner(" + identity + "); "
                + "security.AddAccessRule(" + CampaignAclRule(identity) + "); ",
            "descriptor" => CampaignAclSid
                + "System.Security.AccessControl.DirectorySecurity security = "
                + "(System.Security.AccessControl.DirectorySecurity)(CampaignAclDescriptorWrapper)new System.Security.AccessControl.DirectorySecurity(); "
                + "security.SetOwner(sid); security.SetAccessRuleProtection(true, false); "
                + "security.AddAccessRule(" + CampaignAclRule("sid") + "); ",
            _ => CampaignAclSid
                + "System.Security.AccessControl.DirectorySecurity security = new(); "
                + "security.AddAccessRule((System.Security.AccessControl.FileSystemAccessRule)(CampaignAclRuleWrapper)"
                + CampaignAclRule("sid") + "); ",
        };

        const string replacements = """
            internal sealed class CampaignAclIdentityWrapper
            {
                public static explicit operator CampaignAclIdentityWrapper(System.Security.Principal.SecurityIdentifier value) => new();

                public static explicit operator System.Security.Principal.NTAccount(CampaignAclIdentityWrapper value) =>
                    new("fixture-account");
            }

            internal sealed class CampaignAclDescriptorWrapper
            {
                public static explicit operator CampaignAclDescriptorWrapper(System.Security.AccessControl.DirectorySecurity value) => new();

                public static explicit operator System.Security.AccessControl.DirectorySecurity(CampaignAclDescriptorWrapper value) =>
                    CampaignAclInput.UnknownSecurity();
            }

            internal sealed class CampaignAclRuleWrapper
            {
                public static explicit operator CampaignAclRuleWrapper(System.Security.AccessControl.FileSystemAccessRule value) => new();

                public static explicit operator System.Security.AccessControl.FileSystemAccessRule(CampaignAclRuleWrapper value) =>
                    CampaignAclInput.UnknownRule();
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = CampaignAclDiscover(
            body + "System.IO.File.Exists(\"acl-conversion-control\");", replacements);

        Assert.Single(result.Items, static site => site.Callee == "System.IO.File.Exists");

        if (shape == "identity")
        {
            AssertCampaignAclUnclassified(result, "System.Security.AccessControl.ObjectSecurity.SetOwner");

            AssertCampaignAclUnclassified(result, "System.Security.AccessControl.FileSystemAccessRule..ctor");
        }
        else if (shape == "descriptor")
        {
            AssertCampaignAclUnclassified(result, "System.Security.AccessControl.ObjectSecurity.SetOwner");

            AssertCampaignAclUnclassified(result, "System.Security.AccessControl.ObjectSecurity.SetAccessRuleProtection");
        }

        AssertCampaignAclUnclassified(result, "System.Security.AccessControl.FileSystemSecurity.AddAccessRule");
    }

    private static HostedProducerDiscovery<HostedProducerSite> CampaignAclDiscover(string body, string additionalHelpers = "")
    {
        const string helpers = "internal static class CampaignAclInput { "
            + "internal static extern System.Security.AccessControl.DirectorySecurity UnknownSecurity(); "
            + "internal static extern System.Security.AccessControl.ObjectSecurity UnknownObjectSecurity(); "
            + "internal static extern System.Security.Principal.IdentityReference UnknownIdentity(); "
            + "internal static extern System.Security.AccessControl.FileSystemAccessRule UnknownRule(); "
            + "internal static extern bool UnknownFlag(); }";

        string source = "using System.IO;\n" + R2Source(R2Admission + body, helpers + additionalHelpers);

        CSharpCompilation compilation = CompileWithProductionReferencePack(source, "RetroDownfall.Arcanum.Api");

        Assert.Empty(compilation.GetDiagnostics().Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        return HostedGrimoireProducerInventory.DiscoverProducerSites(
            [compilation], new(["Worker"], []), [new("Worker", [OrdinaryRoot()])], []);
    }

    private static void AssertCampaignAclUnclassified(HostedProducerDiscovery<HostedProducerSite> result, string member) =>
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
            && diagnostic.Detail == member);

    private static bool IsCampaignAclBoundary(string member) => member is
        "System.Security.AccessControl.DirectorySecurity..ctor"
        or "System.Security.AccessControl.ObjectSecurity.SetOwner"
        or "System.Security.AccessControl.ObjectSecurity.SetAccessRuleProtection"
        or "System.Security.AccessControl.FileSystemSecurity.AddAccessRule"
        or "System.Security.AccessControl.FileSystemAccessRule..ctor";

    private const string CampaignAclSid = "System.Security.Principal.SecurityIdentifier sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!; ";

    private static string CampaignAclRule(string identity) =>
        "new System.Security.AccessControl.FileSystemAccessRule(" + identity
            + ", System.Security.AccessControl.FileSystemRights.FullControl, "
            + "System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit, "
            + "System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow)";
}
