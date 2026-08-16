using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

public readonly record struct CovenantKey
{
    private readonly bool _initialized;

    private readonly string? _value;

    [JsonConstructor]
    public CovenantKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!IsValid(value))
        {
            throw new ArgumentException("A Covenant key must match [a-z0-9][a-z0-9._-]{0,127}.", nameof(value));
        }

        _value = value;
        _initialized = true;
    }

    public string Value =>
        _initialized
            ? _value!
            : throw new InvalidOperationException("An uninitialized Covenant key has no value.");

    public override string ToString() => Value;

    /// <summary>
    /// Whether a candidate matches the key grammar, without constructing one.
    /// </summary>
    /// <remarks>
    /// A public wire request has to answer "is this a key" as a typed refusal, and a constructor that
    /// throws is the wrong shape for that: catching an exception to decide a 400 makes malformed
    /// input an exceptional path and costs a stack capture per bad request.
    /// </remarks>
    public static bool IsWellFormed(string value) => value is not null && IsValid(value);

    private static bool IsValid(string value)
    {
        if (value.Length is < 1 or > CovenantLimits.MaxKeyCharacters)
        {
            return false;
        }

        if (!IsLowerAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];

            if (!IsLowerAsciiLetterOrDigit(character)
                && character is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';
}

public readonly record struct CovenantGenerationId
{
    private readonly bool _initialized;

    private readonly Guid _value;

    [JsonConstructor]
    public CovenantGenerationId(Guid value)
    {
        CovenantValidation.RequireNonEmpty(value, nameof(value));

        _value = value;
        _initialized = true;
    }

    public Guid Value =>
        _initialized
            ? _value
            : throw new InvalidOperationException("An uninitialized Covenant generation ID has no value.");
}

public readonly record struct CovenantRevision
{
    [JsonConstructor]
    public CovenantRevision(ulong value)
    {
        Value = value;
    }

    public ulong Value { get; }
}

public readonly record struct CovenantRowCount
{
    private readonly bool _initialized;

    private readonly uint _maximum;

    private readonly uint _value;

    [JsonConstructor]
    public CovenantRowCount(uint value, uint maximum)
    {
        if (maximum == 0 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        _value = value;
        _maximum = maximum;
        _initialized = true;
    }

    public uint Value =>
        _initialized
            ? _value
            : throw new InvalidOperationException("An uninitialized Covenant row count has no value.");

    public uint Maximum =>
        _initialized
            ? _maximum
            : throw new InvalidOperationException("An uninitialized Covenant row count has no maximum.");
}

public readonly record struct CovenantByteCount
{
    private readonly bool _initialized;

    private readonly uint _maximum;

    private readonly uint _value;

    [JsonConstructor]
    public CovenantByteCount(uint value, uint maximum)
    {
        if (maximum == 0 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        _value = value;
        _maximum = maximum;
        _initialized = true;
    }

    public uint Value =>
        _initialized
            ? _value
            : throw new InvalidOperationException("An uninitialized Covenant byte count has no value.");

    public uint Maximum =>
        _initialized
            ? _maximum
            : throw new InvalidOperationException("An uninitialized Covenant byte count has no maximum.");
}

public readonly record struct CovenantDigest
{
    private readonly byte[]? _bytes;

    [JsonConstructor]
    public CovenantDigest(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length != CovenantLimits.DigestBytes)
        {
            throw new ArgumentException($"A Covenant digest must contain exactly {CovenantLimits.DigestBytes} bytes.", nameof(bytes));
        }

        _bytes = bytes.ToArray();
    }

    public byte[] Bytes => _bytes?.ToArray() ?? [];

    [JsonIgnore]
    public bool IsValid => _bytes is { Length: CovenantLimits.DigestBytes };

    public bool Equals(CovenantDigest other) =>
        (_bytes ?? []).AsSpan().SequenceEqual(other._bytes ?? []);

    public override int GetHashCode()
    {
        HashCode hash = new();

        foreach (byte value in _bytes ?? [])
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    public override string ToString() =>
        IsValid ? Convert.ToHexString(_bytes!) : string.Empty;
}

internal static class CovenantValidation
{
    public static Guid RequireNonEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("The identifier cannot be empty.", parameterName);
        }

        return value;
    }

    public static ulong RequirePositive(ulong value, string parameterName)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static long RequirePositive(long value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static uint RequirePositive(uint value, string parameterName)
    {
        if (value == 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return value;
    }

    public static CovenantDigest RequireDigest(CovenantDigest value, string parameterName)
    {
        if (!value.IsValid)
        {
            throw new ArgumentException("The Covenant digest is uninitialized.", parameterName);
        }

        return value;
    }
}
