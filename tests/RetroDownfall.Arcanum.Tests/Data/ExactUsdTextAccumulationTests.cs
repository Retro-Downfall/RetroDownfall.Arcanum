using System.Diagnostics;
using System.Globalization;
using System.Numerics;

using RetroDownfall.Arcanum.Infrastructure.Data;

using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class ExactUsdTextAccumulationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10000)]
    [InlineData(30000)]
    public void Ordinary_bill_folds_do_not_allocate_per_addition(int count)
    {
        decimal[] costs = [0.00000001m, 0.00003597m, 0.03125000m, 1.75000000m];

        decimal expected = 0m;

        for (int i = 0; i < count; i++) expected += costs[i % costs.Length];

        for (int i = 0; i < 1000; i++) _ = ExactUsdText.CheckedAdd(7.125m, costs[i % costs.Length]);

        long started = Stopwatch.GetTimestamp();

        long before = GC.GetAllocatedBytesForCurrentThread();

        decimal actual = 0m;

        for (int i = 0; i < count; i++) actual = ExactUsdText.CheckedAdd(actual, costs[i % costs.Length]);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"Bills={count}; allocated_bytes={allocated}; elapsed_ticks={Stopwatch.GetTimestamp() - started}; timestamp_frequency={Stopwatch.Frequency}");

        Assert.Equal(expected, actual);

        Assert.True(allocated <= 1024, $"The {count}-bill exact fold allocated {allocated} managed bytes.");
    }

    [Theory]
    [InlineData("79228162514264337593543950335", "0.0000000000000000000000000001")]
    [InlineData("-79228162514264337593543950335", "-0.1")]
    [InlineData("79228162514264337593543950335", "1")]
    [InlineData("10000000000000000000000000000", "1.0000000000000000000000000000")]
    [InlineData("-10000000000000000000000000000", "-1.0000000000000000000000000000")]
    [InlineData("79228162514264337593543950335", "-79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001", "-0.0000000000000000000000000001")]
    [InlineData("7.9228162514264337593543950335", "-7.9228162514264337593543950334")]
    [InlineData("-0.0000", "0.00000000")]
    [InlineData("17014118346046923173168730371", "1000000000000000000.0000000000")]
    [InlineData("-17014118346046923173168730371", "-1000000000000000000.0000000000")]
    public void Boundary_sums_match_independent_exact_arithmetic(string left, string right) =>
        AssertExact(decimal.Parse(left, CultureInfo.InvariantCulture), decimal.Parse(right, CultureInfo.InvariantCulture));

    [Fact]
    public void Every_scale_and_sign_matches_independent_exact_arithmetic()
    {
        Random random = new(219);

        for (byte leftScale = 0; leftScale <= 28; leftScale++)
        {
            for (byte rightScale = 0; rightScale <= 28; rightScale++)
            {
                for (int signs = 0; signs < 4; signs++)
                {
                    decimal left = new(random.Next(), random.Next(), random.Next(), (signs & 1) != 0, leftScale);

                    decimal right = new(random.Next(), random.Next(), random.Next(), (signs & 2) != 0, rightScale);

                    AssertExact(left, right);
                }
            }
        }
    }

    [Fact]
    public void A_later_credit_does_not_conceal_an_unrepresentable_prefix()
    {
        decimal[] amounts = [decimal.MaxValue, 0.00000001m, -0.00000001m];

        decimal total = 0m;

        int applied = 0;

        Assert.Throws<OverflowException>(() =>
        {
            foreach (decimal amount in amounts)
            {
                total = ExactUsdText.CheckedAdd(total, amount);

                applied++;
            }
        });

        Assert.Equal(1, applied);

        Assert.Equal(decimal.MaxValue, total);
    }

    private static void AssertExact(decimal left, decimal right)
    {
        decimal expected;

        try
        {
            expected = ReferenceSum(left, right);
        }
        catch (OverflowException)
        {
            Assert.Throws<OverflowException>(() => ExactUsdText.CheckedAdd(left, right));

            return;
        }

        decimal actual = ExactUsdText.CheckedAdd(left, right);

        Assert.Equal(decimal.GetBits(expected), decimal.GetBits(actual));
    }

    private static decimal ReferenceSum(decimal left, decimal right)
    {
        // Independent decimal-text rational oracle: never uses production decomposition or arithmetic.
        (BigInteger coefficient, int scale) first = FromText(left);

        (BigInteger coefficient, int scale) second = FromText(right);

        int scale = Math.Max(first.scale, second.scale);

        BigInteger exact = first.coefficient * BigInteger.Pow(10, scale - first.scale)
            + second.coefficient * BigInteger.Pow(10, scale - second.scale);

        while (scale > 0 && exact % 10 == 0)
        {
            exact /= 10;

            scale--;
        }

        BigInteger magnitude = BigInteger.Abs(exact);

        if (magnitude > (BigInteger.One << 96) - 1) throw new OverflowException();

        string digits = magnitude.ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');

        string text = scale == 0 ? digits : digits.Insert(digits.Length - scale, ".");

        return decimal.Parse((exact.Sign < 0 ? "-" : "") + text, CultureInfo.InvariantCulture);
    }

    private static (BigInteger Coefficient, int Scale) FromText(decimal value)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);

        int separator = text.IndexOf('.');

        int scale = separator < 0 ? 0 : text.Length - separator - 1;

        return (BigInteger.Parse(text.Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture), scale);
    }
}
