using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Repositories;


namespace RetroDownfall.Arcanum.Tests.Repositories;

public sealed class GrimoireLimitsTests
{

    [Fact]
    public void EnforceEntryLimits_WithinBounds_ReturnsNull()
    {

        SessionSettings settings = new()
        {
            MaxEntriesPerSession = 100,
            MaxEntryContentBytes = 1024,
        };

        Error? result = GrimoireLimits.EnforceEntryLimits(98, entriesToAdd: 2, settings, "hello");

        Assert.Null(result);

    }

    [Fact]
    public void EnforceEntryLimits_TooManyEntries_ReturnsError()
    {

        SessionSettings settings = new()
        {
            MaxEntriesPerSession = 100,
            MaxEntryContentBytes = 1024,
        };

        Error? result = GrimoireLimits.EnforceEntryLimits(100, entriesToAdd: 1, settings, "hello");

        Assert.NotNull(result);

        Assert.Equal(ErrorCodes.Session.TooManyEntries, result.Value.Code);

        Assert.StartsWith("Session.TooManyEntries:", result.Value.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void EnforceEntryLimits_EntryTooLarge_ReturnsError()
    {

        SessionSettings settings = new()
        {
            MaxEntriesPerSession = 100,
            MaxEntryContentBytes = 1024,
        };

        string oversized = new('x', 1025);

        Error? result = GrimoireLimits.EnforceEntryLimits(0, entriesToAdd: 1, settings, oversized);

        Assert.NotNull(result);

        Assert.Equal(ErrorCodes.Session.EntryTooLarge, result.Value.Code);

        Assert.StartsWith("Session.EntryTooLarge:", result.Value.Message, StringComparison.Ordinal);

    }

}
