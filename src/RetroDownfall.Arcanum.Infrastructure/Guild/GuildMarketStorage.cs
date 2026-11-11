using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Checks retained market receipts at their immutable storage boundary.</summary>
internal static class GuildMarketStorage
{
    internal static DateTimeOffset ParseReceiptInstant(string value)
    {
        DateTimeOffset instant = UtcInstantText.Parse(value);

        if (!string.Equals(UtcInstantText.Format(instant), value, StringComparison.Ordinal))
            throw new FormatException("The retained market receipt timestamp is not canonical UTC.");

        return instant;
    }
}
