using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed partial class HostedGrimoireProducerInventoryTests
{
    [Theory]
    [InlineData("DirectoryPathConf", 4)]
    [InlineData("GetDirectoryCaseSensitiveInfo", 4)]
    [InlineData("MkdirDirectoryAt", 5)]
    [InlineData("CreateDirectoryNt", 5)]
    public void Guild_workspace_native_imports_have_exact_physical_effect_kinds(string member, int expectedKind)
    {
        string helper = $$"""
            namespace RetroDownfall.Arcanum.Infrastructure.Security
            {
                internal static class FileHandleIdentityInterop
                {
                    [System.Runtime.InteropServices.DllImport("fixture")]
                    internal static extern int {{member}}(int value);

                    internal static int Run() => {{member}}(0);
                }
            }
            """;

        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource(
            "_ = RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.Run();", helper));

        Assert.Contains(result.Items, site => site.Callee == "RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop." + member
            && site.Kind == (HostedProducerSiteKind)expectedKind);

        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED");
    }

    [Theory]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Security", "UnreviewedFileHandleIdentityInterop")]
    [InlineData("RetroDownfall.Arcanum.Infrastructure.Unreviewed", "FileHandleIdentityInterop")]
    public void Guild_workspace_boundary_names_on_unreviewed_declaring_types_stay_unclassified(string containingNamespace, string type)
    {
        string helper = $$"""
            namespace {{containingNamespace}}
            {
                internal static class {{type}}
                {
                    [System.Runtime.InteropServices.DllImport("fixture")]
                    internal static extern int DirectoryPathConf(int value);

                    [System.Runtime.InteropServices.DllImport("fixture")]
                    internal static extern int GetDirectoryCaseSensitiveInfo(int value);

                    [System.Runtime.InteropServices.DllImport("fixture")]
                    internal static extern int MkdirDirectoryAt(int value);

                    [System.Runtime.InteropServices.DllImport("fixture")]
                    internal static extern int CreateDirectoryNt(int value);

                    internal static int Run() => DirectoryPathConf(0) + GetDirectoryCaseSensitiveInfo(0)
                        + MkdirDirectoryAt(0) + CreateDirectoryNt(0);
                }
            }
            """;

        string qualified = containingNamespace + "." + type;

        HostedProducerDiscovery<HostedProducerSite> result = Discover(FixtureSource("_ = " + qualified + ".Run();", helper));

        foreach (string member in new[] { "DirectoryPathConf", "GetDirectoryCaseSensitiveInfo", "MkdirDirectoryAt", "CreateDirectoryNt" })
        {
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "HOSTED_SITE_UNCLASSIFIED"
                && diagnostic.Detail.EndsWith(qualified + "." + member, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(result.Items, site => site.Callee.StartsWith(qualified + ".", StringComparison.Ordinal));
    }
}
