using System.Buffers.Text;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// A keyed, versioned, 128-bit correlation label for one piece of Covenant content.
/// </summary>
/// <remarks>
/// Diagnostics need to say "these two log lines are about the same entry" without ever saying which
/// entry. A raw <see cref="CovenantDigest"/> would do the first job and fail the second: it is an
/// unkeyed hash of content, so anyone holding a candidate fragment could confirm its presence by
/// recomputing. This value is an HMAC under an installation-private diagnostic key, so a tag is
/// correlatable within an installation and meaningless outside it (§10.12).
///
/// <para>The constructor is internal and only <c>ICovenantDiagnosticTagger.Create</c> reaches it, so
/// no caller can substitute a raw hash at a public metadata boundary. Comparison goes through
/// <see cref="FixedTimeEquals"/>; the default equality of a byte-bearing value would compare with an
/// early exit, and equality of a keyed value is exactly where that leaks.</para>
///
/// <para><see cref="KeyVersion"/> travels with the tag because the key rotates. Two tags carrying
/// different versions describe the same content under different keys and are correctly unequal; a
/// reader that ignored the version would report a rotation as a change of subject.</para>
/// </remarks>
public readonly struct CovenantDiagnosticTag : IEquatable<CovenantDiagnosticTag>
{

    /// <summary>The exact tag length: the first 128 bits of an HMAC-SHA-256.</summary>
    public const int TagBytes = 16;

    private readonly bool _initialized;

    private readonly uint _keyVersion;

    private readonly ulong _high;

    private readonly ulong _low;

    internal CovenantDiagnosticTag(uint keyVersion, ReadOnlySpan<byte> tag)
    {

        if (keyVersion == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(keyVersion), "A diagnostic key version starts at one.");
        }

        if (tag.Length != TagBytes)
        {
            throw new ArgumentException($"A diagnostic tag is exactly {TagBytes} bytes.", nameof(tag));
        }

        _initialized = true;

        _keyVersion = keyVersion;

        _high = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tag);

        _low = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tag[8..]);

    }

    /// <summary>Whether this value came from a tagger rather than being default-initialized.</summary>
    public bool IsValid => _initialized;

    /// <summary>The nonsecret diagnostic key version this tag was produced under.</summary>
    public uint KeyVersion
    {

        get
        {

            EnsureInitialized();

            return _keyVersion;

        }

    }

    /// <summary>
    /// The tag as <c>v{version}.{unpadded base64url}</c>, safe to log and safe to serialize.
    /// </summary>
    public override string ToString()
    {

        EnsureInitialized();

        // 'v' + version digits + '.' + the unpadded base64url tag, and not one character more:
        // string.Create hands back a zero-initialized buffer, so an over-long request would leave a
        // U+0000 inside the one value a reader is ever allowed to correlate two log lines by.
        return string.Create(
            2 + CountDigits(_keyVersion) + Base64Url.GetEncodedLength(TagBytes),
            (Version: _keyVersion, High: _high, Low: _low),
            static (destination, state) =>
            {

                Span<byte> tag = stackalloc byte[TagBytes];

                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(tag, state.High);

                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(tag[8..], state.Low);

                Span<byte> encoded = stackalloc byte[Base64Url.GetEncodedLength(TagBytes)];

                _ = Base64Url.EncodeToUtf8(tag, encoded, out _, out int written);

                destination[0] = 'v';

                int offset = 1;

                _ = state.Version.TryFormat(destination[offset..], out int versionLength);

                offset += versionLength;

                destination[offset++] = '.';

                for (int index = 0; index < written; index++)
                {
                    destination[offset + index] = (char)encoded[index];
                }

                if (offset + written != destination.Length)
                {
                    throw new InvalidOperationException("A rendered diagnostic tag must fill its buffer exactly.");
                }

            });

    }

    /// <summary>Copies the raw 16 tag bytes into <paramref name="destination"/>.</summary>
    public void WriteTo(Span<byte> destination)
    {

        EnsureInitialized();

        if (destination.Length < TagBytes)
        {
            throw new ArgumentException($"A diagnostic tag needs {TagBytes} bytes.", nameof(destination));
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(destination, _high);

        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _low);

    }

    /// <summary>
    /// Compares two tags without an early exit.
    /// </summary>
    public static bool FixedTimeEquals(CovenantDiagnosticTag left, CovenantDiagnosticTag right)
    {

        if (!left._initialized || !right._initialized)
        {
            return false;
        }

        Span<byte> leftBytes = stackalloc byte[TagBytes + 4];

        Span<byte> rightBytes = stackalloc byte[TagBytes + 4];

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(leftBytes, left._keyVersion);

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(rightBytes, right._keyVersion);

        left.WriteTo(leftBytes[4..]);

        right.WriteTo(rightBytes[4..]);

        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);

    }

    /// <inheritdoc/>
    public bool Equals(CovenantDiagnosticTag other) => FixedTimeEquals(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CovenantDiagnosticTag other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_keyVersion, _high, _low);

    public static bool operator ==(CovenantDiagnosticTag left, CovenantDiagnosticTag right) =>
        FixedTimeEquals(left, right);

    public static bool operator !=(CovenantDiagnosticTag left, CovenantDiagnosticTag right) =>
        !FixedTimeEquals(left, right);

    private static int CountDigits(uint value)
    {

        int digits = 1;

        while (value >= 10)
        {

            value /= 10;

            digits++;

        }

        return digits;

    }

    private void EnsureInitialized()
    {

        if (!_initialized)
        {
            throw new InvalidOperationException("An uninitialized diagnostic tag has no value.");
        }

    }

}
