using System.Data;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class ExactUsdTextTests
{
    [Fact]
    public void Authoritative_money_boundaries_use_the_exact_codec_and_new_owners_require_review()
    {
        string[] expectedOwners =
        [
            "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSessionImporter.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BatchAccountingRecoveryStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetAlertRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/BudgetReservationService.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireEntitySql.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/TurnRunWriter.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/GrimoireRepository.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/ProtectedArtifactTransferStore.cs",
            "src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionRepository.cs",
        ];

        IReadOnlyList<ProductionSource> sources = ProductionSourceInventory.Sources();

        string[] actualOwners =
        [
            .. sources
                .Where(static source => source.Names("ExactUsdText."))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(expectedOwners, actualOwners);

        string productionText = string.Join("\n", sources.Select(static source => source.Text));

        Assert.DoesNotContain("ef_add(", productionText, StringComparison.OrdinalIgnoreCase);

        foreach (string column in new[]
                 {
                     "ActualCostUsd",
                     "ReservedUsd",
                     "ReconciledUsd",
                     "AmountUsd",
                     "SpendUsd",
                     "DailyLimitUsd",
                 })
        {
            Assert.DoesNotContain(
                $"SUM(\"{column}\")",
                productionText,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("0.00000001", "0.00000001")]
    [InlineData("1.0e-08", "0.000000010")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    public void Supported_decimal_text_is_read_without_binary_floating_point(
        string stored,
        string expected)
    {
        decimal value = ExactUsdText.Parse(stored);

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Fact]
    public void Parameters_are_bound_as_invariant_text()
    {
        using SqliteCommand command = new();

        SqliteParameter parameter = ExactUsdText.AddParameter(command, "$cost", 0.00000001m);

        Assert.Equal(DbType.String, parameter.DbType);

        Assert.Equal("0.00000001", parameter.Value);
    }

    [Fact]
    public void Checked_sum_refuses_decimal_overflow()
    {
        Assert.Throws<OverflowException>(() => ExactUsdText.CheckedAdd(decimal.MaxValue, 0.00000001m));
    }

    [Theory]
    [InlineData("1.00000000", "0.00000001", "1.00000001")]
    [InlineData("79228162514264337593543950335", "-79228162514264337593543950335", "0")]
    [InlineData("-0.00000001", "0.00000002", "0.00000001")]
    public void Checked_sum_preserves_every_representable_decimal_digit(
        string left,
        string right,
        string expected)
    {
        decimal result = ExactUsdText.CheckedAdd(
            ExactUsdText.Parse(left),
            ExactUsdText.Parse(right));

        Assert.Equal(ExactUsdText.Parse(expected), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,000.00")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("0x10")]
    public void Unsupported_authoritative_money_text_is_refused(string stored)
    {
        FormatException error = Assert.Throws<FormatException>(() => ExactUsdText.Parse(stored));

        Assert.Equal(
            "Stored authoritative USD amount is not a supported decimal value.",
            error.Message);
    }
}
