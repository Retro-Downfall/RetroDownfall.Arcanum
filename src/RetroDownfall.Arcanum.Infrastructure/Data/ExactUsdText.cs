using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Numerics;

using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Exact, culture-independent persistence for authoritative USD amounts.
/// </summary>
/// <remarks>
/// SQLite numeric affinity and aggregate functions use binary floating point for non-integers.
/// Authoritative amounts therefore cross the provider boundary as TEXT and are parsed and summed by
/// checked <see cref="decimal"/> arithmetic in managed code.
/// </remarks>
internal static class ExactUsdText
{
    private const NumberStyles ParseStyles =
        NumberStyles.AllowLeadingSign
        | NumberStyles.AllowDecimalPoint
        | NumberStyles.AllowExponent;

    internal static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    internal static decimal Parse(string stored)
    {
        if (!decimal.TryParse(stored, ParseStyles, CultureInfo.InvariantCulture, out decimal value))
        {
            throw new FormatException("Stored authoritative USD amount is not a supported decimal value.");
        }

        return value;
    }

    internal static decimal Read(DbDataReader reader, int ordinal)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return reader.GetValue(ordinal) switch
        {
            string stored => Parse(stored),

            decimal value => value,

            _ => reader.GetDecimal(ordinal),
        };
    }

    internal static decimal CheckedAdd(decimal left, decimal right)
    {
        (BigInteger leftSignificand, int leftScale) = Decompose(left);
        (BigInteger rightSignificand, int rightScale) = Decompose(right);
        int resultScale = Math.Max(leftScale, rightScale);

        BigInteger exact =
            (leftSignificand * BigInteger.Pow(10, resultScale - leftScale))
            + (rightSignificand * BigInteger.Pow(10, resultScale - rightScale));

        while (resultScale > 0 && exact % 10 == 0)
        {
            exact /= 10;
            resultScale--;
        }

        bool isNegative = exact.Sign < 0;
        BigInteger magnitude = BigInteger.Abs(exact);
        BigInteger maximumSignificand = (BigInteger.One << 96) - 1;

        if (magnitude > maximumSignificand)
        {
            throw new OverflowException(
                "The exact USD sum cannot be represented without losing precision.");
        }

        uint low = (uint)(magnitude & uint.MaxValue);
        uint middle = (uint)((magnitude >> 32) & uint.MaxValue);
        uint high = (uint)((magnitude >> 64) & uint.MaxValue);

        return new decimal(
            unchecked((int)low),
            unchecked((int)middle),
            unchecked((int)high),
            isNegative,
            checked((byte)resultScale));
    }

    internal static DbParameter AddParameter(DbCommand command, string name, decimal value)
    {
        ArgumentNullException.ThrowIfNull(command);

        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.DbType = DbType.String;

        parameter.Value = Format(value);

        _ = command.Parameters.Add(parameter);

        return parameter;
    }

    internal static SqliteParameter AddParameter(SqliteCommand command, string name, decimal value) =>
        (SqliteParameter)AddParameter((DbCommand)command, name, value);

    private static (BigInteger Significand, int Scale) Decompose(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        int flags = bits[3];
        int scale = (flags >> 16) & 0x7F;
        BigInteger significand =
            (uint)bits[0]
            | ((BigInteger)(uint)bits[1] << 32)
            | ((BigInteger)(uint)bits[2] << 64);

        if ((flags & int.MinValue) != 0)
        {
            significand = -significand;
        }

        return (significand, scale);
    }
}
