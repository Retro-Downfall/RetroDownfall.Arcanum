using System.Xml.Linq;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Packaging;

public sealed class ContinuousIntegrationWorkflowTests
{

    [Theory]

    [InlineData("src/RetroDownfall.TheForge.Core/RetroDownfall.TheForge.Core.csproj")]

    [InlineData("src/RetroDownfall.TheForge.Ux/RetroDownfall.TheForge.Ux.csproj")]

    [InlineData("src/RetroDownfall.Compendium.Ux/RetroDownfall.Compendium.Ux.csproj")]

    [InlineData("src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj")]

    public void Ci_compiles_every_project_the_release_workflows_ship(string relativeProjectPath)
    {

        string repositoryRoot = FindRepositoryRoot();

        IReadOnlySet<string> compiled = CompiledProjectClosure(repositoryRoot);

        string expected = FullPath(repositoryRoot, relativeProjectPath);

        Assert.True(
            compiled.Contains(expected),
            $"{relativeProjectPath} is shipped by the release workflows but is never compiled by "
            + ".github/workflows/ci.yml, so a compile break in it merges green.");

    }

    private static IReadOnlySet<string> CompiledProjectClosure(string repositoryRoot)
    {

        string workflow = File.ReadAllText(
            Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));

        HashSet<string> closure = new(StringComparer.Ordinal);

        Queue<string> pending = new();

        foreach (string token in workflow.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {

            if (!token.EndsWith(".csproj", StringComparison.Ordinal))
            {

                continue;

            }

            string projectPath = FullPath(repositoryRoot, token);

            if (File.Exists(projectPath) && closure.Add(projectPath))
            {

                pending.Enqueue(projectPath);

            }

        }

        Assert.NotEmpty(closure);

        while (pending.Count > 0)
        {

            string projectPath = pending.Dequeue();

            string projectDirectory = Path.GetDirectoryName(projectPath)!;

            foreach (XElement element in XDocument.Load(projectPath).Descendants("ProjectReference"))
            {

                string? include = element.Attribute("Include")?.Value;

                if (include is null)
                {

                    continue;

                }

                string referencePath = FullPath(projectDirectory, include);

                if (File.Exists(referencePath) && closure.Add(referencePath))
                {

                    pending.Enqueue(referencePath);

                }

            }

        }

        return closure;

    }

    private static string FullPath(string baseDirectory, string relativePath) =>
        Path.GetFullPath(
            Path.Combine(
                baseDirectory,
                relativePath.Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
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
