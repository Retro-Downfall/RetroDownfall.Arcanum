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
/// </remarks>
public sealed class CliInfrastructureBoundaryTests
{
    /// <summary>
    /// The command-handler sources under <c>Commands</c> that name Infrastructure, each of them a verb
    /// (or the plumbing of a verb) that works on the local installation without a running host.
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
}
