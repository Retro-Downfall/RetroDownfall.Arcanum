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

        Error? result = GrimoireLimits.EnforceEntryLimits(
            1_000_000,
            entriesToAdd: 2,
            settings,
            "hello");

        Assert.Null(result);

    }

    [Fact]
    public void EnforceEntryLimits_ManyEntries_ReturnsNull()
    {

        SessionSettings settings = ArcanumRuntimeDefaults.Sessions;

        Error? result = GrimoireLimits.EnforceEntryLimits(
            1_000_000,
            entriesToAdd: 1,
            settings,
            "hello");

        Assert.Null(result);

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
