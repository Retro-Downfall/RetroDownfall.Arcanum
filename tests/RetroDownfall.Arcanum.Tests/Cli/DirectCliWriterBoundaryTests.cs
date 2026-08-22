using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class DirectCliWriterBoundaryTests
{

    [Fact]
    public void SetupCommitter_is_the_only_production_caller_of_the_explicit_configuration_bypass()
    {

        string[] callers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => source.Names(".WriteUnderExclusiveAsync("))
                .Select(static source => source.RelativePath)
                .OrderBy(static path => path, StringComparer.Ordinal),
        ];

        Assert.Equal(
            ["src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupCommitter.cs"],
            callers);

    }

    [Fact]
    public void Both_setup_commit_sites_route_through_the_single_exclusive_commit_helper()
    {

        ProductionSource source = ProductionSourceInventory.Sources().Single(
            static candidate => candidate.IsExactOwner(
                "src/RetroDownfall.Arcanum.Cli/Commands/SetupCommand.cs"));

        Assert.Equal(3, Count(source.Text, "CommitUnderExclusiveAsync("));

        Assert.Equal(1, Count(source.Text, ".CommitAsync("));

        Assert.Equal(1, Count(source.Text, ".RunExclusiveAsync("));

    }

    [Fact]
    public void Active_context_raw_writer_has_only_the_store_and_three_outer_owners()
    {

        string[] owners =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => source.Names("SaveUnderExclusive("))
                .Select(static source => source.RelativePath.Replace('\\', '/'))
                .OrderBy(static path => path, StringComparer.Ordinal),
        ];

        Assert.Equal(
            [
                "src/RetroDownfall.Arcanum.Cli/Services/CliContextService.cs",
                "src/RetroDownfall.Arcanum.Cli/Services/CliContextStore.cs",
                "src/RetroDownfall.Arcanum.Cli/Services/CliSessionManager.cs",
                "src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupCommitter.cs",
            ],
            owners);

    }

    [Fact]
    public void Setup_context_write_uses_the_already_owned_seam_without_nested_admission()
    {

        ProductionSource setup = ProductionSourceInventory.Sources().Single(
            static source => source.IsExactOwner(
                "src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupCommitter.cs"));

        Assert.Equal(1, Count(setup.Text, "contextWriter.SaveUnderExclusive("));

        Assert.DoesNotContain(
            "IArcanumClientMutationBoundary",
            setup.Text,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "mutationBoundary.RunAsync(",
            setup.Text,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Legacy_cli_session_file_is_read_only_fallback_state()
    {

        ProductionSource[] owners =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source =>
                    source.RelativePath.StartsWith(
                        "src/RetroDownfall.Arcanum.Cli/",
                        StringComparison.Ordinal)
                    && source.Names("cli-session.txt")),
        ];

        ProductionSource owner = Assert.Single(owners);

        Assert.True(owner.IsExactOwner(
            "src/RetroDownfall.Arcanum.Cli/Services/CliSessionManager.cs"));

        Assert.Contains("File.ReadAllText(SessionFilePath)", owner.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("File.Write", owner.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("File.Delete", owner.Text, StringComparison.Ordinal);

    }

    [Fact]
    public void Client_mutation_paths_do_not_block_on_async_results()
    {

        string[] mutationPaths =
        [
            "src/RetroDownfall.Arcanum.Cli/CommandCenter/CommandCenterChatRunner.cs",
            "src/RetroDownfall.Arcanum.Cli/CommandCenter/SessionWorkspaceService.cs",
            "src/RetroDownfall.Arcanum.Cli/Commands/AskCommand.cs",
            "src/RetroDownfall.Arcanum.Cli/Commands/ContextCommands.cs",
            "src/RetroDownfall.Arcanum.Cli/Infrastructure/CliCommandTree.Context.cs",
            "src/RetroDownfall.Arcanum.Cli/Services/CliContextService.cs",
            "src/RetroDownfall.Arcanum.Cli/Services/CliInferenceContextResolver.cs",
            "src/RetroDownfall.Arcanum.Cli/Services/CliSessionManager.cs",
            "src/RetroDownfall.Arcanum.Cli/Services/Setup/SetupCommitter.cs",
            "src/RetroDownfall.Arcanum.Cli/UX/RecentResourceStore.cs",
            "src/RetroDownfall.Arcanum.Cli/UX/ResourceSelection.cs",
        ];

        ProductionSource[] sources =
        [
            .. ProductionSourceInventory.Sources()
                .Where(source => mutationPaths.Any(source.IsExactOwner)),
        ];

        Assert.Equal(mutationPaths.Length, sources.Length);

        foreach (ProductionSource source in sources)
        {

            string withoutEventNames = source.Text.Replace(
                "IntelligenceEventType.Result",
                "IntelligenceEventType_Result",
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                ".GetAwaiter().GetResult()",
                withoutEventNames,
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                ".Wait(",
                withoutEventNames,
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                ".Result",
                withoutEventNames,
                StringComparison.Ordinal);

        }

    }

    private static int Count(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length)
        / value.Length;

}
