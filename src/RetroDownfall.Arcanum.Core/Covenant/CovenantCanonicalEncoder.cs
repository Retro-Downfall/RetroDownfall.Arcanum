using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Covenant;

public sealed class CovenantCanonicalEncoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly byte[] _buffer;

    private bool _faulted;

    private int _writtenCount;

    public CovenantCanonicalEncoder(int maximumBytes)
    {
        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        _buffer = new byte[maximumBytes];
    }

    public int WrittenCount => _faulted ? 0 : _writtenCount;

    public ReadOnlySpan<byte> WrittenSpan =>
        _faulted ? [] : _buffer.AsSpan(0, _writtenCount);

    public ReadOnlyMemory<byte> WrittenMemory =>
        _faulted ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, _writtenCount);

    public void WriteByte(byte value)
    {
        Span<byte> destination = GetDestination(sizeof(byte));

        destination[0] = value;

        Advance(sizeof(byte));
    }

    public void WriteSByte(sbyte value) =>
        WriteByte(unchecked((byte)value));

    public void WriteUInt16(ushort value)
    {
        Span<byte> destination = GetDestination(sizeof(ushort));

        BinaryPrimitives.WriteUInt16BigEndian(destination, value);
        Advance(sizeof(ushort));
    }

    public void WriteInt16(short value)
    {
        Span<byte> destination = GetDestination(sizeof(short));

        BinaryPrimitives.WriteInt16BigEndian(destination, value);
        Advance(sizeof(short));
    }

    public void WriteUInt32(uint value)
    {
        Span<byte> destination = GetDestination(sizeof(uint));

        BinaryPrimitives.WriteUInt32BigEndian(destination, value);
        Advance(sizeof(uint));
    }

    public void WriteInt32(int value)
    {
        Span<byte> destination = GetDestination(sizeof(int));

        BinaryPrimitives.WriteInt32BigEndian(destination, value);
        Advance(sizeof(int));
    }

    public void WriteUInt64(ulong value)
    {
        Span<byte> destination = GetDestination(sizeof(ulong));

        BinaryPrimitives.WriteUInt64BigEndian(destination, value);
        Advance(sizeof(ulong));
    }

    public void WriteInt64(long value)
    {
        Span<byte> destination = GetDestination(sizeof(long));

        BinaryPrimitives.WriteInt64BigEndian(destination, value);
        Advance(sizeof(long));
    }

    public void WriteBinary64(double value)
    {
        ThrowIfFaulted();

        if (!double.IsFinite(value))
        {
            Fault();

            throw new ArgumentOutOfRangeException(nameof(value), "Canonical binary64 values must be finite.");
        }

        if (value == 0d)
        {
            value = 0d;
        }

        WriteUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
    }

    public void WriteGuid(Guid value)
    {
        Span<byte> destination = GetDestination(16);

        if (!value.TryWriteBytes(destination, bigEndian: true, out int bytesWritten)
            || bytesWritten != 16)
        {
            Fault();

            throw new InvalidOperationException("The GUID could not be encoded in network byte order.");
        }

        Advance(bytesWritten);
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        int totalBytes = GetLengthPrefixedSize(value.Length);
        Span<byte> destination = GetDestination(totalBytes);

        BinaryPrimitives.WriteUInt32BigEndian(destination, checked((uint)value.Length));
        value.CopyTo(destination[sizeof(uint)..]);
        Advance(totalBytes);
    }

    public void WriteFixed32(ReadOnlySpan<byte> value)
    {
        ThrowIfFaulted();

        if (value.Length != CovenantLimits.DigestBytes)
        {
            Fault();

            throw new ArgumentException($"A fixed Covenant value must contain exactly {CovenantLimits.DigestBytes} bytes.", nameof(value));
        }

        Span<byte> destination = GetDestination(CovenantLimits.DigestBytes);

        value.CopyTo(destination);
        Advance(CovenantLimits.DigestBytes);
    }

    public void WriteUtf8(string value)
    {
        ThrowIfFaulted();

        if (value is null)
        {
            Fault();

            throw new ArgumentNullException(nameof(value));
        }

        int byteCount;

        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch
        {
            Fault();

            throw;
        }

        int totalBytes = GetLengthPrefixedSize(byteCount);
        Span<byte> destination = GetDestination(totalBytes);

        BinaryPrimitives.WriteUInt32BigEndian(destination, checked((uint)byteCount));
        StrictUtf8.GetBytes(value.AsSpan(), destination[sizeof(uint)..]);
        Advance(totalBytes);
    }

    public void WriteDomainTag(CovenantDomainTag domainTag)
    {
        ThrowIfFaulted();

        string value;

        try
        {
            value = CovenantPolicyV1Manifest.GetDomainTag(domainTag);
        }
        catch
        {
            Fault();

            throw;
        }

        Span<byte> destination = GetDestination(checked(value.Length + 1));

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];

            if (character > 0x7f || character == '\0')
            {
                Fault();

                throw new InvalidOperationException("Policy domain tags must contain non-NUL ASCII only.");
            }

            destination[index] = (byte)character;
        }

        destination[value.Length] = 0;
        Advance(value.Length + 1);
    }

    public void WriteCount(ulong count)
    {
        ThrowIfFaulted();

        if (count > uint.MaxValue)
        {
            Fault();

            throw new ArgumentOutOfRangeException(nameof(count));
        }

        WriteUInt32((uint)count);
    }

    public void WriteOptional<T>(T? value, Action<CovenantCanonicalEncoder, T> writeValue)
        where T : struct
    {
        ThrowIfFaulted();

        if (writeValue is null)
        {
            Fault();

            throw new ArgumentNullException(nameof(writeValue));
        }

        try
        {
            WriteByte(value.HasValue ? (byte)1 : (byte)0);

            if (value is { } present)
            {
                writeValue(this, present);
            }
        }
        catch
        {
            Fault();

            throw;
        }
    }

    public void WriteOptionalReference<T>(T? value, Action<CovenantCanonicalEncoder, T> writeValue)
        where T : class
    {
        ThrowIfFaulted();

        if (writeValue is null)
        {
            Fault();

            throw new ArgumentNullException(nameof(writeValue));
        }

        try
        {
            WriteByte(value is null ? (byte)0 : (byte)1);

            if (value is not null)
            {
                writeValue(this, value);
            }
        }
        catch
        {
            Fault();

            throw;
        }
    }

    public void WriteList<T>(IReadOnlyList<T> values, Action<CovenantCanonicalEncoder, T> writeValue)
    {
        ThrowIfFaulted();

        if (values is null)
        {
            Fault();

            throw new ArgumentNullException(nameof(values));
        }

        if (writeValue is null)
        {
            Fault();

            throw new ArgumentNullException(nameof(writeValue));
        }

        try
        {
            WriteCount(checked((uint)values.Count));

            for (int index = 0; index < values.Count; index++)
            {
                writeValue(this, values[index]);
            }
        }
        catch
        {
            Fault();

            throw;
        }
    }

    private int GetLengthPrefixedSize(int payloadBytes)
    {
        ThrowIfFaulted();

        try
        {
            return checked(sizeof(uint) + payloadBytes);
        }
        catch (OverflowException)
        {
            Fault();

            throw new ArgumentOutOfRangeException(nameof(payloadBytes));
        }
    }

    private Span<byte> GetDestination(int byteCount)
    {
        ThrowIfFaulted();

        if (byteCount < 0 || byteCount > _buffer.Length - _writtenCount)
        {
            Fault();

            throw new ArgumentException("The canonical payload exceeds its bounded output allocation.", nameof(byteCount));
        }

        return _buffer.AsSpan(_writtenCount, byteCount);
    }

    private void Advance(int byteCount) =>
        _writtenCount = checked(_writtenCount + byteCount);

    private void ThrowIfFaulted()
    {
        if (_faulted)
        {
            throw new InvalidOperationException("The canonical encoder is faulted and cannot publish or accept more output.");
        }
    }

    private void Fault()
    {
        _buffer.AsSpan(0, _writtenCount).Clear();
        _writtenCount = 0;
        _faulted = true;
    }
}

public sealed class CovenantCanonicalHashWriter : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    private bool _disposed;

    private bool _faulted;

    private bool _finalized;

    public void WriteByte(byte value)
    {
        Span<byte> bytes = stackalloc byte[1];

        bytes[0] = value;
        Append(bytes);
    }

    public void WriteSByte(sbyte value) =>
        WriteByte(unchecked((byte)value));

    public void WriteUInt16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];

        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        Append(bytes);
    }

    public void WriteInt16(short value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(short)];

        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        Append(bytes);
    }

    public void WriteUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];

        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        Append(bytes);
    }

    public void WriteInt32(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];

        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        Append(bytes);
    }

    public void WriteUInt64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        Append(bytes);
    }

    public void WriteInt64(long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];

        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        Append(bytes);
    }

    public void WriteBinary64(double value)
    {
        ThrowIfUnavailable();

        if (!double.IsFinite(value))
        {
            Fault();

            throw new ArgumentOutOfRangeException(nameof(value), "Canonical binary64 values must be finite.");
        }

        WriteUInt64(unchecked((ulong)BitConverter.DoubleToInt64Bits(value == 0d ? 0d : value)));
    }

    public void WriteGuid(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];

        ThrowIfUnavailable();

        if (!value.TryWriteBytes(bytes, bigEndian: true, out int bytesWritten)
            || bytesWritten != bytes.Length)
        {
            Fault();

            throw new InvalidOperationException("The GUID could not be encoded in network byte order.");
        }

        Append(bytes);
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteCount(checked((ulong)value.Length));
        Append(value);
    }

    public void WriteFixed32(ReadOnlySpan<byte> value)
    {
        ThrowIfUnavailable();

        if (value.Length != CovenantLimits.DigestBytes)
        {
            Fault();

            throw new ArgumentException($"A fixed Covenant value must contain exactly {CovenantLimits.DigestBytes} bytes.", nameof(value));
        }

        Append(value);
    }

    public void WriteUtf8(string value)
    {
        ThrowIfUnavailable();

        if (value is null)
        {
            Fault();

            throw new ArgumentNullException(nameof(value));
        }

        int byteCount;

        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch
        {
            Fault();

            throw;
        }

        WriteCount(checked((ulong)byteCount));

        if (byteCount == 0)
        {
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Min(byteCount, 4096));

        try
        {
            Encoder encoder = StrictUtf8.GetEncoder();
            ReadOnlySpan<char> remaining = value.AsSpan();

            while (!remaining.IsEmpty)
            {
                encoder.Convert(remaining, rented, true, out int charsUsed, out int bytesUsed, out _);
                Append(rented.AsSpan(0, bytesUsed));
                remaining = remaining[charsUsed..];
            }
        }
        catch
        {
            Fault();

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    public void WriteDomainTag(CovenantDomainTag domainTag)
    {
        ThrowIfUnavailable();

        string value;

        try
        {
            value = CovenantPolicyV1Manifest.GetDomainTag(domainTag);
        }
        catch
        {
            Fault();

            throw;
        }

        Span<byte> bytes = value.Length + 1 <= 256
            ? stackalloc byte[value.Length + 1]
            : new byte[value.Length + 1];

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];

            if (character > 0x7f || character == '\0')
            {
                Fault();

                throw new InvalidOperationException("Policy domain tags must contain non-NUL ASCII only.");
            }

            bytes[index] = (byte)character;
        }

        bytes[value.Length] = 0;
        Append(bytes);
    }

    public void WriteCount(ulong count)
    {
        ThrowIfUnavailable();

        if (count > uint.MaxValue)
        {
            Fault();

            throw new ArgumentOutOfRangeException(nameof(count));
        }

        WriteUInt32((uint)count);
    }

    public void WriteOptional<T>(T? value, Action<CovenantCanonicalHashWriter, T> writeValue)
        where T : struct
    {
        ThrowIfUnavailable();

        try
        {
            ArgumentNullException.ThrowIfNull(writeValue);
            WriteByte(value.HasValue ? (byte)1 : (byte)0);

            if (value is { } present)
            {
                writeValue(this, present);
            }
        }
        catch
        {
            Fault();

            throw;
        }
    }

    public void WriteOptionalReference<T>(T? value, Action<CovenantCanonicalHashWriter, T> writeValue)
        where T : class
    {
        ThrowIfUnavailable();

        try
        {
            ArgumentNullException.ThrowIfNull(writeValue);
            WriteByte(value is null ? (byte)0 : (byte)1);

            if (value is not null)
            {
                writeValue(this, value);
            }
        }
        catch
        {
            Fault();

            throw;
        }
    }

    public void WriteList<T>(IReadOnlyList<T> values, Action<CovenantCanonicalHashWriter, T> writeValue)
    {
        ThrowIfUnavailable();

        try
        {
            ArgumentNullException.ThrowIfNull(values);
            ArgumentNullException.ThrowIfNull(writeValue);
            WriteCount(checked((ulong)values.Count));

            for (int index = 0; index < values.Count; index++)
            {
                writeValue(this, values[index]);
            }
        }
        catch
        {
            Fault();

            throw;
        }
    }

    public CovenantDigest FinalizeDigest()
    {
        ThrowIfUnavailable();
        _finalized = true;

        return new CovenantDigest(_hash.GetHashAndReset());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _hash.Dispose();
        _disposed = true;
    }

    private void Append(ReadOnlySpan<byte> value)
    {
        ThrowIfUnavailable();
        _hash.AppendData(value);
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_faulted)
        {
            throw new InvalidOperationException("The canonical hash writer is faulted and cannot publish or accept more output.");
        }

        if (_finalized)
        {
            throw new InvalidOperationException("The canonical hash writer has already been finalized.");
        }
    }

    private void Fault() =>
        _faulted = true;
}
