using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Repositories;


namespace RetroDownfall.Arcanum.Tests.Repositories;

public sealed class GrimoireLimitsTests
{

    [Fact]
    public void EnforceEntryLimits_WithinBounds_ReturnsNull()
    {

        SessionSettings settings = ArcanumRuntimeDefaults.Sessions;
        int maxEntries = ArcanumSettingClamps.MaxEntriesPerSession(
            settings.MaxEntriesPerSession);

        Error? result = GrimoireLimits.EnforceEntryLimits(
            maxEntries - 2,
            entriesToAdd: 2,
            settings,
            "hello");

        Assert.Null(result);

    }

    [Fact]
    public void EnforceEntryLimits_TooManyEntries_ReturnsError()
    {

        SessionSettings settings = ArcanumRuntimeDefaults.Sessions;
        int maxEntries = ArcanumSettingClamps.MaxEntriesPerSession(
            settings.MaxEntriesPerSession);

        Error? result = GrimoireLimits.EnforceEntryLimits(
            maxEntries,
            entriesToAdd: 1,
            settings,
            "hello");

        Assert.NotNull(result);

        Assert.Equal(ErrorCodes.Session.TooManyEntries, result.Value.Code);

        Assert.StartsWith("Session.TooManyEntries:", result.Value.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void EnforceEntryLimits_EntryTooLarge_ReturnsError()
    {

        SessionSettings settings = ArcanumRuntimeDefaults.Sessions;
        int maxEntryBytes = ArcanumSettingClamps.MaxEntryContentBytes(
            settings.MaxEntryContentBytes);
        string oversized = new('x', maxEntryBytes + 1);

        Error? result = GrimoireLimits.EnforceEntryLimits(0, entriesToAdd: 1, settings, oversized);

        Assert.NotNull(result);

        Assert.Equal(ErrorCodes.Session.EntryTooLarge, result.Value.Code);

        Assert.StartsWith("Session.EntryTooLarge:", result.Value.Message, StringComparison.Ordinal);

    }

}
