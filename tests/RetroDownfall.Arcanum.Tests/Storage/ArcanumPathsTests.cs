using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Tests.Storage;

public sealed class ArcanumPathsTests
{

    [Fact]
    public void GrimoireDirectory_IsUnderUserConfigArcanum()
    {

        string profile = global::System.Environment.GetFolderPath(
            global::System.Environment.SpecialFolder.UserProfile);

        string expected = Path.Combine(profile, ".config", "arcanum");

        Assert.Equal(expected, ArcanumPaths.GrimoireDirectory);

    }

    [Fact]
    public void GrimoireDatabaseFile_IsUnderGrimoireDirectory()
    {

        string expected = Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.db");

        Assert.Equal(expected, ArcanumPaths.GrimoireDatabaseFile);

    }

    [Fact]
    public void GlobalSpellsDirectory_IsUnderGrimoireDirectory()
    {

        string expected = Path.Combine(ArcanumPaths.GrimoireDirectory, "spells");

        Assert.Equal(expected, ArcanumPaths.GlobalSpellsDirectory);

    }

}
