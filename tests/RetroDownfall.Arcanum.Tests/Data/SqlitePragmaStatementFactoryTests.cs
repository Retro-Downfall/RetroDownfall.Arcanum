using System.Globalization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class SqlitePragmaStatementFactoryTests
{
    [Fact]
    public void BusyTimeout_accepts_only_the_nonnegative_integer_grammar()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo adversarial = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        adversarial.NumberFormat.NegativeSign = "'; DROP TABLE Sessions; --";

        try
        {
            CultureInfo.CurrentCulture = adversarial;

            Assert.Equal(
                "PRAGMA busy_timeout=2147483647;",
                SqlitePragmaStatementFactory.BusyTimeout(int.MaxValue));
            _ = Assert.Throws<ArgumentOutOfRangeException>(
                () => SqlitePragmaStatementFactory.BusyTimeout(-1));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Rekey_accepts_only_one_canonical_32_byte_base64_token()
    {
        string passphrase = Convert.ToBase64String(Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());

        Assert.Equal(
            $"PRAGMA rekey = '{passphrase}';",
            SqlitePragmaStatementFactory.Rekey(passphrase));

        foreach (string invalid in new[]
        {
            string.Empty,
            Convert.ToBase64String(new byte[31]),
            passphrase + "'; DROP TABLE Sessions; --",
            new string('A', 42) + "B=",
        })
        {
            _ = Assert.Throws<ArgumentException>(() => SqlitePragmaStatementFactory.Rekey(invalid));
        }
    }
}
