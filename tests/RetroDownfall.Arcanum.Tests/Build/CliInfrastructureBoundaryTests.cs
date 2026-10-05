using System.Text.RegularExpressions;
using System.Xml.Linq;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The agent orientation file says which Cli verbs reach Infrastructure directly, and the handlers
/// say the same.
/// </summary>
/// <remarks>
/// <para>The orientation file used to say the Cli "calls the running host's API rather than reaching
/// into Infrastructure directly", which is true of the server-backed verbs and false of the local-only
/// ones: <c>key</c>, <c>config</c>, <c>backup</c>, <c>setup</c>, <c>serve</c> and the rest read or write
/// the installation on this machine and need no running host. A contributor who believed the sentence
/// would route a new local verb through a new endpoint, or, worse, treat an Infrastructure reference
/// in a server-backed handler as ordinary.</para>
/// <para>The inventory is the command handlers that name an Infrastructure type. Server-backed
/// handlers do not appear in it, which is the claim the orientation file makes about them, so a new
/// entry is a decision: either the verb is local-only and belongs in the list and in that file, or it
/// should call the API like its neighbours.</para>
/// <para>The Forge is an HTTP client of the same API, and the orientation file once said its "one direct
/// Infrastructure use" was the installation mutation coordination after the markdown image loader had
/// started judging its pinned sockets by the host's outbound address policy. The Forge's Infrastructure
/// sources are an inventory too, the orientation line names each use, and the design project table
/// names every project reference the Forge projects declare.</para>
/// </remarks>
public sealed class CliInfrastructureBoundaryTests
{
    /// <summary>
    /// The command-handler sources under <c>Commands</c> that name Infrastructure, each of them a verb
    /// (or the plumbing of a verb) that works on the local installation without a running host, or the
    /// local confirmation preview a server-backed verb shows before it calls the host.
    /// </summary>
    private static readonly string[] LocalOnlyHandlers =
    [
        "BackupCommands.cs",
        "CompletionCommands.cs",
        "Configuration/ConfigCommands.cs",

        // The confirmation preview of the server-backed `mcp trust`: it reads the workspace's mcp.json
        // on this machine through the host's own secure, size-capped reader, so the operator approves
        // the bytes the host will trust, before anything reaches the host.
        "Configuration/McpTrustPreview.cs",
        "Configuration/PresetCommands.cs",
        "DataEncryptionCommands.cs",
        "DoctorCommand.cs",
        "FullInstallationResetAttestationFileReader.cs",
        "InstallationFactoryResetCommand.cs",
        "InstallationResetApplyBoundary.cs",
        "KeyCommands.cs",
        "ServeCommand.cs",
        "SetupCommand.cs",
    ];

    /// <summary>
    /// The Forge sources that name Infrastructure, each of them one of the two uses the orientation file
    /// states for the Forge.
    /// </summary>
    private static readonly string[] ForgeInfrastructureUses =
    [
        // The shared outbound address policy: the markdown image loader pins its own sockets and judges
        // each address by OutboundUrlGuard.IsBlockedForUntrustedEgress rather than a copy of its ranges.
        "Markdown/MarkdownImageSsrfPolicy.cs",

        // The installation mutation coordination around local file writes: the composition root
        // registers it and the runner drives it.
        "ServiceCollectionConfigurator.cs",
        "Services/TheForgeLocalMutationRunner.cs",
    ];

    [Fact]
    public void Only_local_only_command_handlers_name_Infrastructure()
    {
        string commands = Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Cli",
            "Commands");

        string[] naming =
        [
            .. Directory
                .EnumerateFiles(commands, "*.cs", SearchOption.AllDirectories)
                .Where(static path => File.ReadAllText(path).Contains("RetroDownfall.Arcanum.Infrastructure", StringComparison.Ordinal))
                .Select(path => Path.GetRelativePath(commands, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(LocalOnlyHandlers.Order(StringComparer.Ordinal), naming);
    }

    [Fact]
    public void The_orientation_file_states_the_boundary_the_projects_actually_have()
    {
        string agents = File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "AGENTS.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.DoesNotContain("HTTP-only clients", agents, StringComparison.Ordinal);

        Assert.DoesNotContain("rather than reaching into Infrastructure directly", agents, StringComparison.Ordinal);

        string cliLine = Assert.Single(
            agents.Split('\n'),
            static line => line.StartsWith("- **`Cli`**", StringComparison.Ordinal));

        Assert.Contains("local-only verbs", cliLine, StringComparison.Ordinal);

        foreach (string verb in (string[])["`key`", "`config`", "`doctor`", "`backup`", "`setup`"])
        {
            Assert.Contains(verb, cliLine, StringComparison.Ordinal);
        }

        string compendiumLine = Assert.Single(
            agents.Split('\n'),
            static line => line.StartsWith("- **`Compendium.Ux`**", StringComparison.Ordinal));

        Assert.Contains("arcanum.json", compendiumLine, StringComparison.Ordinal);

        string forgeLine = Assert.Single(
            agents.Split('\n'),
            static line => line.StartsWith("- **`TheForge.Ux`**", StringComparison.Ordinal));

        Assert.Contains("HTTP client", forgeLine, StringComparison.Ordinal);

        // Every Infrastructure use the Forge has (the inventory above) is named, and none is hidden
        // behind a claim that there is only one.
        Assert.Contains("mutation coordination", forgeLine, StringComparison.Ordinal);

        Assert.Contains("outbound address policy", forgeLine, StringComparison.Ordinal);

        Assert.DoesNotContain("one direct Infrastructure use", forgeLine, StringComparison.Ordinal);

        // The references the sentences rest on.
        string root = TestRepositoryPaths.RepositoryRoot();

        Assert.Contains(
            "RetroDownfall.Arcanum.Infrastructure.csproj",
            File.ReadAllText(Path.Combine(root, "src", "RetroDownfall.Arcanum.Cli", "RetroDownfall.Arcanum.Cli.csproj")),
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "RetroDownfall.Arcanum.Api.csproj",
            File.ReadAllText(Path.Combine(root, "src", "RetroDownfall.Compendium.Ux", "RetroDownfall.Compendium.Ux.csproj")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_stated_Forge_sources_name_Infrastructure()
    {
        string forge = Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.TheForge.Ux");

        string[] naming =
        [
            .. Directory
                .EnumerateFiles(forge, "*.cs", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(forge, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(static relative => !relative.StartsWith("bin/", StringComparison.Ordinal)
                    && !relative.StartsWith("obj/", StringComparison.Ordinal))
                .Where(relative => File.ReadAllText(Path.Combine(forge, relative)).Contains("RetroDownfall.Arcanum.Infrastructure", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(ForgeInfrastructureUses.Order(StringComparer.Ordinal), naming);
    }

    [Theory]
    [InlineData("src", "RetroDownfall.TheForge.Core")]
    [InlineData("src", "RetroDownfall.TheForge.Ux")]
    [InlineData("tests", "RetroDownfall.TheForge.Tests")]
    public void The_design_project_table_names_every_Forge_project_reference(string folder, string project)
    {
        string root = TestRepositoryPaths.RepositoryRoot();

        string design = File
            .ReadAllText(Path.Combine(root, "docs", "Arcanum.DESIGN.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        string row = Assert.Single(
            design.Split('\n'),
            line => line.StartsWith($"| `{project}` |", StringComparison.Ordinal));

        string dependencyCell = row.Split('|')[3];

        string[] named =
        [
            .. Regex
                .Matches(dependencyCell, @"`(RetroDownfall\.[A-Za-z.]+)`")
                .Select(static match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        string[] referenced =
        [
            .. XDocument
                .Load(Path.Combine(root, folder, project, project + ".csproj"))
                .Descendants("ProjectReference")
                .Select(static reference => Path.GetFileNameWithoutExtension(
                    ((string?)reference.Attribute("Include") ?? string.Empty).Replace('\\', '/')))
                .Order(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(referenced);

        Assert.Equal(referenced, named);
    }
}
