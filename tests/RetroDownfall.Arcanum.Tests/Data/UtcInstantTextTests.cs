using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The one persisted-instant codec must make chronological text ordering, equality, and UTC-day
/// boundaries mean the same thing everywhere the Grimoire reads them.
/// </summary>
public sealed class UtcInstantTextTests
{
    [Fact]
    public void DateTimeOffset_is_rendered_as_fixed_width_utc_with_seven_fractional_digits()
    {
        DateTimeOffset value = new(2026, 9, 8, 23, 59, 58, TimeSpan.FromHours(-4));

        value = value.AddTicks(1_234_567);

        Assert.Equal("2026-09-09T03:59:58.1234567Z", UtcInstantText.Format(value));
    }

    [Theory]
    [InlineData("2026-09-09T03:59:58", "2026-09-09T03:59:58.0000000Z")]
    [InlineData("2026-09-09T03:59:58.1", "2026-09-09T03:59:58.1000000Z")]
    [InlineData("2026-09-09T03:59:58+00:00", "2026-09-09T03:59:58.0000000Z")]
    [InlineData("2026-09-08T23:59:58.1234567-04:00", "2026-09-09T03:59:58.1234567Z")]
    [InlineData("2026-09-08 23:59:58-04:00", "2026-09-09T03:59:58.0000000Z")]
    [InlineData("2026-09-09 03:59:58Z", "2026-09-09T03:59:58.0000000Z")]
    [InlineData("2026-09-09 03:59:58.12Z", "2026-09-09T03:59:58.1200000Z")]
    [InlineData("2026-09-09 03:59:58.1234567", "2026-09-09T03:59:58.1234567Z")]
    [InlineData("2026-09-09T03:59:58Z", "2026-09-09T03:59:58.0000000Z")]
    [InlineData("2026-09-09T03:59:58.1234567Z", "2026-09-09T03:59:58.1234567Z")]
    public void Historical_supported_forms_normalize_without_using_the_machine_time_zone(
        string stored,
        string expected)
    {
        Assert.Equal(expected, UtcInstantText.Normalize(stored));
    }

    [Fact]
    public void Utc_DateTime_is_rendered_canonically_and_unspecified_is_the_historical_utc_contract()
    {
        DateTime utc = new(2026, 9, 9, 3, 59, 58, 123, DateTimeKind.Utc);

        DateTime unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

        Assert.Equal("2026-09-09T03:59:58.1230000Z", UtcInstantText.Format(utc));

        Assert.Equal(UtcInstantText.Format(utc), UtcInstantText.Format(unspecified));
    }

    [Fact]
    public void Local_DateTime_is_refused_at_the_persistence_boundary()
    {
        DateTime local = new(2026, 9, 9, 3, 59, 58, DateTimeKind.Local);

        ArgumentException error = Assert.Throws<ArgumentException>(() => UtcInstantText.Format(local));

        Assert.Contains("Local", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-instant")]
    [InlineData("2026-13-40T25:61:61Z")]
    [InlineData("2026-09-09")]
    [InlineData("09/09/2026 03:59:58")]
    [InlineData("Wed, 09 Sep 2026 03:59:58 GMT")]
    [InlineData(" 2026-09-09T03:59:58Z")]
    [InlineData("2026-09-09T03:59:58Z ")]
    public void Invalid_persisted_text_is_refused(string stored)
    {
        FormatException error = Assert.Throws<FormatException>(() => UtcInstantText.Normalize(stored));

        Assert.Equal("Stored Grimoire instant is not a supported timestamp.", error.Message);
    }

    [Fact]
    public void Canonical_text_orders_in_the_same_order_as_the_instants()
    {
        string earlier = UtcInstantText.Format(
            new DateTimeOffset(2026, 9, 8, 23, 59, 59, TimeSpan.FromHours(-4)));

        string later = UtcInstantText.Format(
            new DateTimeOffset(2026, 9, 9, 4, 0, 0, TimeSpan.Zero));

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }
}
