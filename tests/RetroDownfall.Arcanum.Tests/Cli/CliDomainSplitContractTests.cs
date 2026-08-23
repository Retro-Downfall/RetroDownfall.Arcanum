using RetroDownfall.Arcanum.Cli.Commands.Conclave;
using RetroDownfall.Arcanum.Cli.Commands.Tower;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The CLI command handlers are filed by the domain they drive: <c>Cli.Commands.Conclave</c> for the
/// Apprentice and Conclave families, <c>Cli.Commands.Tower</c> for the authored resources. The verbs
/// themselves did not move — <c>docs/Arcanum.CommandMap.json</c> is the contract that proves it, and
/// <c>CliSurfaceTests.Committed_command_map_matches_the_live_tree</c> is what enforces it.
/// </summary>
/// <remarks>
/// What the command map cannot see is the vocabulary. The Forge is the desktop application; a server
/// capability described as one is the confusion this epic exists to remove, and it reads as ordinary
/// help text rather than as a defect. So the rule is asserted directly against the live tree: only the
/// verbs that actually launch the desktop application may name it.
/// </remarks>
public sealed class CliDomainSplitContractTests
{

    private const string RetiredNamespace = "RetroDownfall.Arcanum.Cli.Commands.TheForge";

    /// <summary>The one verb family that starts a desktop process, and may therefore name one.</summary>
    private const string ApplicationLaunchVerb = "open";

    [Fact]
    public void Cli_declares_no_type_in_the_retired_namespace()
    {

        string[] strays = typeof(PromptCommands).Assembly
            .GetTypes()
            .Where(static type => type.Namespace is string ns
                && (string.Equals(ns, RetiredNamespace, StringComparison.Ordinal)
                    || ns.StartsWith(RetiredNamespace + ".", StringComparison.Ordinal)))
            .Select(static type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(strays);

    }

    [Fact]
    public void No_cli_type_is_named_for_the_desktop_application()
    {

        string[] offenders = typeof(PromptCommands).Assembly
            .GetTypes()
            .Where(static type => !type.Name.StartsWith('<'))
            .Where(static type => type.Name.Contains("Forge", StringComparison.Ordinal))
            .Select(static type => type.FullName ?? type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);

    }

    [Fact]
    public void The_apprentice_and_conclave_handlers_are_declared_together()
    {

        Assert.Equal(
            "RetroDownfall.Arcanum.Cli.Commands.Conclave",
            typeof(ApprenticeCommands).Namespace);

        Assert.Equal(
            typeof(ApprenticeCommands).Namespace,
            typeof(ConclaveCommands).Namespace);

    }

    /// <summary>
    /// Only <c>arcanum open …</c> starts a desktop process, so only <c>open</c> may describe one. Every
    /// other command describes a server capability, and naming the desktop application there is what
    /// made an operator read Prompts and Apprentices as desktop features.
    /// </summary>
    [Fact]
    public void Only_the_application_launch_verbs_name_the_desktop_application()
    {

        string[] offenders = [.. CliSurfaceTests
            .Walk(CliSurfaceTests.BuildMap())
            .Where(static command =>
                command.Description.Contains("The Forge", StringComparison.OrdinalIgnoreCase))
            .Where(static command => !IsApplicationLaunch(command.Path))
            .Select(static command => $"{command.Path}: {command.Description}")
            .Order(StringComparer.Ordinal)];

        Assert.Empty(offenders);

    }

    private static bool IsApplicationLaunch(string path) =>
        string.Equals(path, ApplicationLaunchVerb, StringComparison.Ordinal)
        || path.StartsWith(ApplicationLaunchVerb + " ", StringComparison.Ordinal);

}
