namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// The API reference does not describe a boundary the bootstrapper stopped having.
/// </summary>
/// <remarks>
/// The Covenant boundary blockquote was written while nothing was mapped, and the commit that mapped
/// the six inspection routes left the prose in place. A stale "not registered yet" sentence is worse
/// than a missing one: a reader trusts it and concludes the surface is unreachable, so nobody audits
/// the authority on a route that is answering requests.
///
/// <para>Asserted against the bootstrapper rather than as a fixed string, so the check is a
/// contradiction between two files rather than a spelling rule over one. If a later change genuinely
/// unmaps the routes, the sentence becomes true again and this test says so.</para>
/// </remarks>
public sealed class CovenantApiDocumentationTests
{

    [Fact]
    public void The_api_reference_does_not_call_the_inspection_routes_unmapped_while_they_are_mapped()
    {

        string root = RepositoryRoot();

        string bootstrapper = File.ReadAllText(
            Path.Combine(root, "src", "RetroDownfall.Arcanum.Api", "ApiBootstrapper.cs"));

        Assert.Contains("MapCovenantInspectionEndpoints()", bootstrapper, StringComparison.Ordinal);

        string reference = File.ReadAllText(Path.Combine(root, "docs", "Arcanum.API.md"));

        Assert.DoesNotContain("inspection routes remain unmapped", reference, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "those dedicated management routes remain unregistered",
            reference,
            StringComparison.Ordinal);

    }

    private static string RepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {

            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {

                return directory.FullName;

            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException("Could not locate the repository root.");

    }

}
