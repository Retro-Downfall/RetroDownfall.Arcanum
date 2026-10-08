using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// The DevHost is a development convenience that prints the master API key it generates, so it does not
/// run anywhere else.
/// </summary>
/// <remarks>
/// Nothing here starts the DevHost. These tests once launched it in Production and Staging to watch it
/// refuse; the day the guard regressed, that would start a real host that creates or reads a master-key item
/// in the developer's operating-system credential store, which a redirected home directory does not reach.
/// The decision is a pure function, tested in-process, and the wiring is pinned by reading Program.cs.
/// </remarks>
public sealed class DevHostEnvironmentGuardTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    [InlineData("Developer")]
    public void DevHost_refuses_any_environment_but_Development_or_Testing(string environment)
    {
        string? refusal = Program.RefusalForEnvironment(environment);

        Assert.NotNull(refusal);

        Assert.Contains("Development or Testing", refusal, StringComparison.Ordinal);

        Assert.Contains($"'{environment}'", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    [InlineData("Testing")]
    [InlineData("TESTING")]
    public void DevHost_starts_in_Development_and_Testing(string environment) =>
        Assert.Null(Program.RefusalForEnvironment(environment));

    /// <summary>
    /// Program.cs consults the guard on the host's own environment before it reads configuration or builds
    /// anything, and exits 2 with the refusal.
    /// </summary>
    [Fact]
    public void Program_consults_the_guard_before_anything_else_and_exits_2()
    {
        string program = File.ReadAllText(Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Api.DevHost",
            "Program.cs"));

        int guard = program.IndexOf(
            "Program.RefusalForEnvironment(builder.Environment.EnvironmentName)",
            StringComparison.Ordinal);

        Assert.True(guard > 0, "Program.cs no longer consults the environment guard.");

        Assert.True(
            guard < program.IndexOf("AddArcanumConfiguration", StringComparison.Ordinal),
            "The guard must run before configuration is read.");

        Assert.True(
            guard < program.IndexOf("builder.Build()", StringComparison.Ordinal),
            "The guard must run before the host is built.");

        string afterGuard = program[guard..];

        int returnTwo = afterGuard.IndexOf("return 2;", StringComparison.Ordinal);

        Assert.True(
            returnTwo > 0 && returnTwo < afterGuard.IndexOf("AddArcanumConfiguration", StringComparison.Ordinal),
            "A refused environment must exit 2 before anything else runs.");
    }
}
