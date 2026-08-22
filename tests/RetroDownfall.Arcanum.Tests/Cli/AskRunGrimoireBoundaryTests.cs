using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class AskRunGrimoireBoundaryTests
{

    [Theory]
    [InlineData("src/RetroDownfall.Arcanum.Cli/Commands/AskCommand.cs")]
    [InlineData("src/RetroDownfall.Arcanum.Cli/Commands/RunCommand.cs")]
    public void Ask_and_run_do_not_name_local_grimoire_or_chronosync_authorities(
        string repositoryRelativePath)
    {

        ProductionSource source = Assert.Single(
            ProductionSourceInventory.Sources(),
            candidate => candidate.IsExactOwner(repositoryRelativePath));

        string[] forbidden =
        [
            "IGrimoireCliInitialization",
            "IChronosyncEngine",
            "IServiceScopeFactory",
            "IGrimoireRepository",
            "ArcanumDbContext",
            ".EnsureInitializedAsync(",
            ".CreateAsyncScope(",
        ];

        foreach (string construct in forbidden)
        {

            Assert.False(
                source.Names(construct),
                $"{repositoryRelativePath} still names local storage authority '{construct}'.");

        }

    }

    [Fact]
    public void Ask_delegates_pattern_synchronization_to_the_authenticated_client()
    {

        ProductionSource source = Assert.Single(
            ProductionSourceInventory.Sources(),
            static candidate => candidate.IsExactOwner(
                "src/RetroDownfall.Arcanum.Cli/Commands/AskCommand.cs"));

        Assert.True(source.Names(".SynchronizePatternAsync("));

    }

}
