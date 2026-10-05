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
/// the installation on this machine and do not depend on a running host for that work. "Local-only"
/// names the Infrastructure side and nothing else: <c>doctor</c>, <c>serve quit</c>,
/// <c>completion resolve</c>, <c>data factory-reset</c>, <c>config</c> and <c>setup</c> also call a
/// running host's API when there is one (the last two through <c>ConfigurationCommandService</c>, which
/// asks the host first and falls back to the local bootstrap only when it cannot be reached), and the
/// orientation file says so without a count, because a contributor who read "local-only" as "never
/// calls the host" would treat that call as a violation, and a count of the verbs that do goes false the
/// day one is added. A contributor who believed the old sentence would
/// route a new local verb through a new endpoint, or, worse, treat an Infrastructure reference in a
/// server-backed handler as ordinary.</para>
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
    /// (or the plumbing of a verb) that works on the local installation directly. None of them requires
    /// a running host for that work, and a few also call the host when one is up (see
    /// <see cref="LocalOnlyVerbsThatAlsoReachTheHost"/>). The
    /// server-backed <c>mcp trust</c> is not among them: its confirmation preview is the host's own reading
    /// of its own <c>mcp.json</c> (<c>POST /api/mcp/trust-workspace/preview</c>), so the operator approves
    /// the bytes the host will trust even when the host is on another machine, and the CLI reads no file.
    /// </summary>
    private static readonly string[] LocalOnlyHandlers =
    [
        "BackupCommands.cs",
        "CompletionCommands.cs",
        "Configuration/ConfigCommands.cs",
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

    /// <summary>
    /// The local-only verbs the orientation file says also reach a running host, with the call in the
    /// source that makes it true.
    /// </summary>
    public static TheoryData<string, string> LocalOnlyVerbsThatAlsoReachTheHost => new()
    {
        { "Commands/DoctorCommand.cs", "RequestHttpClientName" },
        { "Commands/ServeCommand.cs", "QuitServerAsync" },
        { "Services/CliCompletionResolver.cs", "apiClient.GetModelsAsync" },
        { "Commands/InstallationFactoryResetCommand.cs", "PlanFactoryResetDataAsync" },

        // config reads, validates and writes through the host when it answers, and setup plans and commits
        // through the same service.
        { "Services/ConfigurationCommandService.cs", ".GetConfigurationAsync(" },
        { "Services/ConfigurationCommandService.cs", ".ValidateConfigurationAsync(" },
        { "Services/ConfigurationCommandService.cs", ".UpdateConfigurationAsync(" },
        { "Services/ConfigurationCommandService.cs", "CanUseLocalBootstrap" },
        { "Services/Setup/SetupPlanner.cs", "IConfigurationCommandService" },
    };

    /// <summary>
    /// What names a client of the running host: the typed API client, the HTTP client factory the
    /// health probe uses, and the configuration service that asks the host first.
    /// </summary>
    private static readonly string[] HostClientTokens =
    [
        "ArcanumApiClient",
        "IHttpClientFactory",
        "IConfigurationCommandService",
    ];

    /// <summary>
    /// The local-only handler sources that name a host client, each with the verb the orientation file
    /// names for it. A handler that reaches the host through a service it calls (<c>completion resolve</c>
    /// and <c>setup</c>) is pinned by <see cref="LocalOnlyVerbsThatAlsoReachTheHost"/> instead.
    /// </summary>
    private static readonly Dictionary<string, string> HostReachingHandlerVerbs = new(StringComparer.Ordinal)
    {
        ["Configuration/ConfigCommands.cs"] = "`config`",
        ["DoctorCommand.cs"] = "`doctor`",
        ["InstallationFactoryResetCommand.cs"] = "`data factory-reset`",
        ["InstallationResetApplyBoundary.cs"] = "`data factory-reset`",
        ["ServeCommand.cs"] = "`serve quit`",
    };

    [Theory]
    [MemberData(nameof(LocalOnlyVerbsThatAlsoReachTheHost))]
    public void A_local_only_verb_the_orientation_file_says_reaches_a_running_host_does(
        string source,
        string call)
    {
        string text = File.ReadAllText(
            Path.Combine(
                TestRepositoryPaths.RepositoryRoot(),
                "src",
                "RetroDownfall.Arcanum.Cli",
                source.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains(call, text, StringComparison.Ordinal);
    }

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

    /// <summary>
    /// The list of local-only verbs that also reach a running host is complete as far as the handlers
    /// show it: a local-only handler that names a host client is one the orientation file names, so a
    /// host call added to <c>backup</c> or <c>key</c> fails here until the file says so.
    /// </summary>
    [Fact]
    public void The_local_only_handlers_that_name_a_host_client_are_the_verbs_the_orientation_file_names()
    {
        string commands = Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Cli",
            "Commands");

        string[] reaching =
        [
            .. LocalOnlyHandlers
                .Where(handler => HostClientTokens.Any(token => File
                    .ReadAllText(Path.Combine(commands, handler.Replace('/', Path.DirectorySeparatorChar)))
                    .Contains(token, StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(
            HostReachingHandlerVerbs.Keys.Order(StringComparer.Ordinal).SequenceEqual(reaching),
            "A local-only handler now names (or no longer names) a client of the running host. Say so in the Cli line of AGENTS.md"
                + " (the verbs that also call a host) and update HostReachingHandlerVerbs in this test. Handlers naming one: "
                + string.Join(", ", reaching));

        string cliLine = Assert.Single(
            File.ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "AGENTS.md")).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'),
            static line => line.StartsWith("- **`Cli`**", StringComparison.Ordinal));

        string afterDependency = cliLine[cliLine.IndexOf("do not depend on a running host", StringComparison.Ordinal)..];

        foreach ((string handler, string verb) in HostReachingHandlerVerbs)
        {
            Assert.True(
                afterDependency.Contains(verb, StringComparison.Ordinal),
                $"{handler} names a host client, and the Cli line of AGENTS.md does not name {verb} among the verbs that also call a host.");
        }
    }

    /// <summary>
    /// Shared helpers that a service needs (the exit-code classifier is one) live in <c>Services</c>, so
    /// a service never reaches up into the handlers that call it.
    /// </summary>
    [Fact]
    public void Services_do_not_reference_the_Commands_namespace()
    {
        string services = Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Cli",
            "Services");

        string[] reaching =
        [
            .. Directory
                .EnumerateFiles(services, "*.cs", SearchOption.AllDirectories)
                .Where(static path => File.ReadAllText(path).Contains("RetroDownfall.Arcanum.Cli.Commands", StringComparison.Ordinal))
                .Select(path => Path.GetRelativePath(services, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Empty(reaching);
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

        // "Local-only" is about reaching Infrastructure, not about never calling the host: the line says
        // the verbs do not depend on a running host, and names the ones that also call one, without a
        // count that the next host call would make false.
        Assert.DoesNotContain("need no host", cliLine, StringComparison.Ordinal);

        Assert.DoesNotMatch(
            new Regex(@"\b(?:one|two|three|four|five|six|seven|eight|nine|ten|\d+) of them\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)),
            cliLine);

        Assert.Contains("do not depend on a running host", cliLine, StringComparison.Ordinal);

        string afterDependency = cliLine[cliLine.IndexOf("do not depend on a running host", StringComparison.Ordinal)..];

        foreach (string reaching in (string[])["`doctor`", "`serve quit`", "`completion resolve`", "`data factory-reset`", "`config`", "`setup`"])
        {
            Assert.Contains(reaching, afterDependency, StringComparison.Ordinal);
        }

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

    /// <summary>
    /// The Forge is an HTTP client with two named Infrastructure uses, and the design documents that
    /// describe it say so instead of calling it HTTP-only, which the orientation file and the project
    /// table already contradict.
    /// </summary>
    [Theory]
    [InlineData("Arcanum.Engineering.md", "`TheForge.Core` / `TheForge.Ux`")]
    [InlineData("Arcanum.Design.Human.md", "`RetroDownfall.TheForge.Core` and `.Ux`")]
    public void A_design_document_row_for_the_Forge_names_its_Infrastructure_uses_and_does_not_call_it_HTTP_only(
        string document,
        string subject)
    {
        string text = File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", document))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        string row = Assert.Single(
            text.Split('\n'),
            line => line.StartsWith('|') && line.Contains(subject, StringComparison.Ordinal));

        Assert.DoesNotContain("HTTP-only", row, StringComparison.Ordinal);

        Assert.Contains("HTTP client", row, StringComparison.Ordinal);

        Assert.Contains("mutation coordination", row, StringComparison.Ordinal);

        Assert.Contains("outbound address policy", row, StringComparison.Ordinal);
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
