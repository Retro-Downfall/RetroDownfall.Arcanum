using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Repositories;

public sealed class GrimoireLimitsTests
{

    [Fact]
    public void EnforceEntryLimits_WithinBounds_DoesNotThrow()
    {

        SessionSettings settings = new()
        {
            MaxEntriesPerSession = 100,
            MaxEntryContentBytes = 1024,
        };

        GrimoireLimits.EnforceEntryLimits(98, entriesToAdd: 2, settings, "hello");

    }

    [Fact]
    public void EnforceEntryLimits_TooManyEntries_Throws()
    {

        SessionSettings settings = new()
        {
            MaxEntriesPerSession = 100,
            MaxEntryContentBytes = 1024,
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            GrimoireLimits.EnforceEntryLimits(100, entriesToAdd: 1, settings, "hello"));

        Assert.StartsWith("Session.TooManyEntries:", ex.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void EnforceEntryLimits_EntryTooLarge_Throws()
    {

        SessionSettings settings = new()
        {
            MaxEntriesPerSession = 100,
            MaxEntryContentBytes = 1024,
        };

        string oversized = new('x', 1025);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            GrimoireLimits.EnforceEntryLimits(0, entriesToAdd: 1, settings, oversized));

        Assert.StartsWith("Session.EntryTooLarge:", ex.Message, StringComparison.Ordinal);

    }

}
