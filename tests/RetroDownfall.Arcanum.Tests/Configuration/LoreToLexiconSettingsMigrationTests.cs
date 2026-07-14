using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Configuration;

public sealed class LoreToLexiconSettingsMigrationTests
{

    [Fact]
    public void TryMigrateInPlace_RewritesDeleteLoreAndDisablesLexiconWhenLoreWasOff()
    {

        ArcanumSettings settings = new()
        {
            Ward = new WardSettings
            {
                ForbiddenArts = ["execute_command", "delete_lore", "write_file"],
            },
            Intelligence = new IntelligenceSettings
            {
                EnableLoreSystem = false,
                EnableLexiconSystem = true,
            },
        };

        bool changed = LoreToLexiconSettingsMigration.TryMigrateInPlace(
            settings,
            NullLogger.Instance);

        Assert.True(changed);

        Assert.DoesNotContain(
            settings.Ward.ForbiddenArts,
            static a => string.Equals(a, "delete_lore", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            settings.Ward.ForbiddenArts,
            static a => string.Equals(a, "delete_lexicon", StringComparison.OrdinalIgnoreCase));

        Assert.False(settings.Intelligence.EnableLexiconSystem);

    }

    [Fact]
    public void TryMigrateInPlace_IsNoOpWhenAlreadyMigrated()
    {

        ArcanumSettings settings = new()
        {
            Ward = new WardSettings
            {
                ForbiddenArts = ["execute_command", "delete_lexicon"],
            },
            Intelligence = new IntelligenceSettings
            {
                EnableLoreSystem = true,
                EnableLexiconSystem = true,
            },
        };

        bool changed = LoreToLexiconSettingsMigration.TryMigrateInPlace(settings, NullLogger.Instance);

        Assert.False(changed);

    }

}
