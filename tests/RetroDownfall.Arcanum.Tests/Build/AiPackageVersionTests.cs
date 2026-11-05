using System.Reflection;
using System.Xml.Linq;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class AiPackageVersionTests
{
    [Fact]
    public void All_four_first_party_references_use_the_reviewed_ai_package_version()
    {
        string root = TestRepositoryPaths.RepositoryRoot();

        string[] references = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(project => XDocument.Load(project).Descendants("PackageReference")
                .Where(static reference => ((string?)reference.Attribute("Include")) is
                    "Microsoft.Extensions.AI" or "Microsoft.Extensions.AI.Abstractions" or "Microsoft.Extensions.AI.OpenAI")
                .Select(reference => $"{Path.GetFileNameWithoutExtension(project)}: {reference.Attribute("Include")!.Value} {reference.Attribute("Version")?.Value}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "RetroDownfall.Arcanum.Api: Microsoft.Extensions.AI 10.9.0",
                "RetroDownfall.Arcanum.Api: Microsoft.Extensions.AI.OpenAI 10.9.0",
                "RetroDownfall.Arcanum.Core: Microsoft.Extensions.AI.Abstractions 10.9.0",
                "RetroDownfall.Arcanum.Infrastructure: Microsoft.Extensions.AI 10.9.0",
            ],
            references);
    }

    [Fact]
    public void All_three_resolved_ai_packages_match_the_reviewed_version()
    {
        Assembly[] assemblies =
        [
            typeof(IChatClient).Assembly,
            typeof(ChatClientBuilder).Assembly,
            typeof(OpenAIClientExtensions).Assembly,
        ];

        Assert.Equal(3, assemblies.Distinct().Count());

        foreach (Assembly assembly in assemblies)
        {
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

            Assert.True(version.Split('+')[0] == "10.9.0", $"{assembly.GetName().Name} resolved {version}; expected reviewed version 10.9.0.");
        }
    }
}
