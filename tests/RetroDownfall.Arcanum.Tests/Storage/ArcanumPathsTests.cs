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

    public void Configuration_and_preset_transaction_files_are_under_grimoire_directory()
    {

        Assert.Equal(
            Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json"),
            ArcanumPaths.ConfigurationFile);

        Assert.Equal(
            Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.preset.json"),
            ArcanumPaths.ConfigurationPresetStateFile);

        Assert.Equal(
            Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.preset.rollback.json"),
            ArcanumPaths.ConfigurationPresetRollbackFile);

        Assert.Equal(
            Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.preset.journal.json"),
            ArcanumPaths.ConfigurationPresetJournalFile);

    }

    [Fact]
    public void GlobalSpellsDirectory_IsUnderGrimoireDirectory()
    {

        string expected = Path.Combine(ArcanumPaths.GrimoireDirectory, "spells");

        Assert.Equal(expected, ArcanumPaths.GlobalSpellsDirectory);

    }

    [Fact]
    public void AttachmentsDirectory_IsUnderGrimoireDirectory()
    {

        string expected = Path.Combine(ArcanumPaths.GrimoireDirectory, "attachments");

        Assert.Equal(expected, ArcanumPaths.AttachmentsDirectory);

    }

    [Fact]
    public void PerplexityApiKeyStoreFile_IsUnderSecretStoreDirectory()
    {

        string expected = Path.Combine(
            ArcanumPaths.SecretStoreDirectory,
            "perplexity-key.dat");

        Assert.Equal(expected, ArcanumPaths.PerplexityApiKeyStoreFile);

    }

}
