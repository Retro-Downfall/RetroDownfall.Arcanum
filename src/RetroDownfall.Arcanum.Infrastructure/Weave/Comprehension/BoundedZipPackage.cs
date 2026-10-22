using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// A first-party, bounds-first reader for the ZIP container of an untrusted document package
/// (OOXML, OpenDocument, Visio). It reads only the bytes it is handed: no file system, no
/// <c>ZipArchive</c>, no threads.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Open"/> validates the whole container before any part is inflated: the end-of-central-directory
/// record, the entry count (decided from the EOCD alone, before any per-entry state exists), the central
/// directory walk (which must end exactly at the directory's end), every entry name, and every entry's local
/// header against its central twin. Entry data ranges must lie before the central directory and must not
/// overlap, which is what defeats the non-recursive overlapping-entry zip bomb.
/// </para>
/// <para>
/// <see cref="ReadEntry"/> then bounds inflation: the declared size, the inflation ratio and the running
/// aggregate are checked before a byte is inflated, and inflation itself is bounded by the bytes actually
/// produced, so an understated declared length cannot make the reader allocate or decompress past it.
/// </para>
/// <para>
/// Every refusal is a <see cref="DocumentPackageRejectedException"/> with a closed
/// <see cref="DocumentPackageRejection"/>; no entry name or framework message is ever carried by one. An
/// instance is not thread safe: the aggregate inflation budget is plain state.
/// </para>
/// </remarks>
internal sealed class BoundedZipPackage
{
    internal const int MaximumEntries = 8192;

    internal const int MaximumEntryNameBytes = 1024;

    internal const long MaximumEntryBytes = 32L * 1024 * 1024;

    internal const long MaximumAggregateBytes = 64L * 1024 * 1024;

    internal const int MaximumCompressionRatio = 200;

    internal const long CompressionRatioFloorBytes = 1024 * 1024;

    private const uint LocalHeaderSignature = 0x04034b50;

    private const uint CentralHeaderSignature = 0x02014b50;

    private const uint EndOfCentralDirectorySignature = 0x06054b50;

    private const uint Zip64LocatorSignature = 0x07064b50;

    private const int EndOfCentralDirectoryLength = 22;

    private const int Zip64LocatorLength = 20;

    private const int CentralHeaderLength = 46;

    private const int LocalHeaderLength = 30;

    private const int MaximumCommentLength = 65535;

    private const int EncryptedFlag = 0x0001;

    private const int DataDescriptorFlag = 0x0008;

    private const int StrongEncryptionFlag = 0x0040;

    private const int Utf8NameFlag = 0x0800;

    private const int CentralDirectoryEncryptedFlag = 0x2000;

    private const int EncryptionFlags = EncryptedFlag | StrongEncryptionFlag | CentralDirectoryEncryptedFlag;

    private const int CancellationEntryInterval = 1024;

    private const int InitialInflateBufferBytes = 64 * 1024;

    private const int InflateChunkBytes = 64 * 1024;

    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _array;

    private readonly int _offset;

    private readonly Dictionary<string, Entry> _entries;

    private long _aggregateBytes;

    private BoundedZipPackage(byte[] array, int offset, Dictionary<string, Entry> entries)
    {
        _array = array;

        _offset = offset;

        _entries = entries;
    }

    /// <summary>Validates <paramref name="bytes"/> as a bounded ZIP package; throws a closed rejection if it is not one.</summary>
    public static BoundedZipPackage Open(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (byte[] array, int offset) = Materialize(bytes);

        ReadOnlySpan<byte> span = array.AsSpan(offset, bytes.Length);

        if (span.StartsWith(CompoundFileSignature))
        {
            // Password-protected OOXML is an OLE2 EncryptedPackage, not a ZIP.
            throw Rejection(DocumentPackageRejection.Encrypted);
        }

        if (span.Length < EndOfCentralDirectoryLength)
        {
            throw Rejection(DocumentPackageRejection.NotZip);
        }

        int eocd = FindEndOfCentralDirectory(span);

        ushort diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(span[(eocd + 4)..]);

        ushort centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(span[(eocd + 6)..]);

        ushort entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(span[(eocd + 8)..]);

        ushort totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(span[(eocd + 10)..]);

        uint centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(span[(eocd + 12)..]);

        uint centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[(eocd + 16)..]);

        if (eocd >= Zip64LocatorLength && BinaryPrimitives.ReadUInt32LittleEndian(span[(eocd - Zip64LocatorLength)..]) == Zip64LocatorSignature)
        {
            throw Rejection(DocumentPackageRejection.UnsupportedContainer);
        }

        if (diskNumber != 0
            || centralDirectoryDisk != 0
            || entriesOnDisk != totalEntries
            || totalEntries == ushort.MaxValue
            || centralDirectorySize == uint.MaxValue
            || centralDirectoryOffset == uint.MaxValue)
        {
            throw Rejection(DocumentPackageRejection.UnsupportedContainer);
        }

        if (totalEntries > MaximumEntries)
        {
            // Decided from the EOCD alone: no per-entry state has been allocated and no record has been walked.
            throw Rejection(DocumentPackageRejection.EntryBounds);
        }

        long centralDirectoryEnd = (long)centralDirectoryOffset + centralDirectorySize;

        if (centralDirectoryEnd > eocd || (long)totalEntries * CentralHeaderLength > centralDirectorySize)
        {
            throw Rejection(DocumentPackageRejection.NotZip);
        }

        Dictionary<string, Entry> entries = WalkCentralDirectory(span, totalEntries, centralDirectoryOffset, centralDirectoryEnd, cancellationToken);

        ValidateLocalRecords(span, entries, centralDirectoryOffset, cancellationToken);

        return new BoundedZipPackage(array, offset, entries);
    }

    /// <summary>True when the package has an entry (file or directory) of this name, compared case-insensitively.</summary>
    public bool Contains(string entryName) => _entries.ContainsKey(entryName);

    /// <summary>
    /// Returns an entry's bytes. A stored entry is a slice of the original bytes; a deflated entry is inflated into a
    /// buffer bounded by its declared length, and the bytes produced must equal that declared length exactly.
    /// </summary>
    public ReadOnlyMemory<byte> ReadEntry(string entryName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(entryName, out Entry? entry) || entry.IsDirectory)
        {
            throw Rejection(DocumentPackageRejection.MissingPart);
        }

        long declared = entry.UncompressedSize;

        if (declared > MaximumEntryBytes)
        {
            throw Rejection(DocumentPackageRejection.ExpansionBounds);
        }

        if (declared > CompressionRatioFloorBytes && declared > entry.CompressedSize * MaximumCompressionRatio)
        {
            throw Rejection(DocumentPackageRejection.ExpansionBounds);
        }

        if (_aggregateBytes + declared > MaximumAggregateBytes)
        {
            throw Rejection(DocumentPackageRejection.ExpansionBounds);
        }

        _aggregateBytes += declared;

        if (entry.Method == 0)
        {
            return new ReadOnlyMemory<byte>(_array, _offset + (int)entry.DataOffset, (int)declared);
        }

        return Inflate(entry, (int)declared, cancellationToken);
    }

    private static (byte[] Array, int Offset) Materialize(ReadOnlyMemory<byte> bytes)
    {
        if (MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            return (segment.Array, segment.Offset);
        }

        return (bytes.ToArray(), 0);
    }

    private static int FindEndOfCentralDirectory(ReadOnlySpan<byte> span)
    {
        int lowest = Math.Max(0, span.Length - (EndOfCentralDirectoryLength + MaximumCommentLength));

        for (int position = span.Length - EndOfCentralDirectoryLength; position >= lowest; position--)
        {
            if (span[position] != (byte)'P' || BinaryPrimitives.ReadUInt32LittleEndian(span[position..]) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            // The first signature found scanning backward is the record every other reader would pick; it must
            // account for the file exactly, otherwise the package is ambiguous and is refused.
            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(span[(position + 20)..]);

            if (position + EndOfCentralDirectoryLength + commentLength != span.Length)
            {
                throw Rejection(DocumentPackageRejection.NotZip);
            }

            return position;
        }

        throw Rejection(DocumentPackageRejection.NotZip);
    }

    private static Dictionary<string, Entry> WalkCentralDirectory(
        ReadOnlySpan<byte> span,
        int totalEntries,
        long centralDirectoryOffset,
        long centralDirectoryEnd,
        CancellationToken cancellationToken)
    {
        Dictionary<string, Entry> entries = new(totalEntries, StringComparer.OrdinalIgnoreCase);

        long position = centralDirectoryOffset;

        for (int index = 0; index < totalEntries; index++)
        {
            if (index % CancellationEntryInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (position + CentralHeaderLength > centralDirectoryEnd)
            {
                throw Rejection(DocumentPackageRejection.NotZip);
            }

            ReadOnlySpan<byte> header = span.Slice((int)position, CentralHeaderLength);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralHeaderSignature)
            {
                throw Rejection(DocumentPackageRejection.NotZip);
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);

            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);

            uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);

            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);

            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);

            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);

            int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);

            ushort diskStart = BinaryPrimitives.ReadUInt16LittleEndian(header[34..]);

            uint localOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);

            long next = position + CentralHeaderLength + nameLength + extraLength + commentLength;

            if (next > centralDirectoryEnd)
            {
                throw Rejection(DocumentPackageRejection.NotZip);
            }

            if ((flags & EncryptionFlags) != 0)
            {
                throw Rejection(DocumentPackageRejection.Encrypted);
            }

            if (method != 0 && method != 8)
            {
                throw Rejection(DocumentPackageRejection.UnsupportedContainer);
            }

            if (compressedSize == uint.MaxValue || uncompressedSize == uint.MaxValue || localOffset == uint.MaxValue || diskStart != 0)
            {
                throw Rejection(DocumentPackageRejection.UnsupportedContainer);
            }

            int nameOffset = (int)position + CentralHeaderLength;

            string name = DecodeName(span.Slice(nameOffset, nameLength), (flags & Utf8NameFlag) != 0);

            Entry entry = new(name.EndsWith('/'), method, compressedSize, uncompressedSize, localOffset, nameOffset, nameLength);

            if (!entries.TryAdd(name, entry))
            {
                throw Rejection(DocumentPackageRejection.UnsafeEntryName);
            }

            position = next;
        }

        if (position != centralDirectoryEnd)
        {
            throw Rejection(DocumentPackageRejection.NotZip);
        }

        return entries;
    }

    private static string DecodeName(ReadOnlySpan<byte> nameBytes, bool utf8)
    {
        if (nameBytes.Length == 0 || nameBytes.Length > MaximumEntryNameBytes)
        {
            throw Rejection(DocumentPackageRejection.UnsafeEntryName);
        }

        string name;

        if (utf8)
        {
            try
            {
                name = StrictUtf8.GetString(nameBytes);
            }
            catch (DecoderFallbackException)
            {
                throw Rejection(DocumentPackageRejection.UnsafeEntryName);
            }
        }
        else
        {
            name = Encoding.Latin1.GetString(nameBytes);
        }

        if (!IsSafeName(name))
        {
            throw Rejection(DocumentPackageRejection.UnsafeEntryName);
        }

        return name;
    }

    private static bool IsSafeName(string name)
    {
        if (name[0] == '/')
        {
            return false;
        }

        foreach (char character in name)
        {
            if (character < 0x20 || character == 0x7F || character == '\\' || character == ':')
            {
                return false;
            }
        }

        // A single trailing '/' marks a directory entry; every remaining segment must be a real name.
        ReadOnlySpan<char> path = name.EndsWith('/') ? name.AsSpan(0, name.Length - 1) : name;

        while (true)
        {
            int slash = path.IndexOf('/');

            ReadOnlySpan<char> segment = slash < 0 ? path : path[..slash];

            if (segment.IsEmpty || segment.SequenceEqual(".") || segment.SequenceEqual(".."))
            {
                return false;
            }

            if (slash < 0)
            {
                return true;
            }

            path = path[(slash + 1)..];
        }
    }

    private static void ValidateLocalRecords(
        ReadOnlySpan<byte> span,
        Dictionary<string, Entry> entries,
        long centralDirectoryOffset,
        CancellationToken cancellationToken)
    {
        List<(long Start, long End)> ranges = new(entries.Count);

        int visited = 0;

        foreach (Entry entry in entries.Values)
        {
            if (visited++ % CancellationEntryInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            long start = entry.LocalOffset;

            if (start + LocalHeaderLength > centralDirectoryOffset)
            {
                throw Rejection(DocumentPackageRejection.NotZip);
            }

            ReadOnlySpan<byte> local = span.Slice((int)start, LocalHeaderLength);

            if (BinaryPrimitives.ReadUInt32LittleEndian(local) != LocalHeaderSignature)
            {
                throw Rejection(DocumentPackageRejection.NotZip);
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(local[6..]);

            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(local[8..]);

            uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(local[18..]);

            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(local[22..]);

            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(local[26..]);

            int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(local[28..]);

            if ((flags & EncryptionFlags) != 0)
            {
                throw Rejection(DocumentPackageRejection.Encrypted);
            }

            if (method != entry.Method || nameLength != entry.NameLength)
            {
                throw Rejection(DocumentPackageRejection.NotZip);
            }

            long dataStart = start + LocalHeaderLength + nameLength + extraLength;

            if (dataStart > centralDirectoryOffset)
            {
                throw Rejection(DocumentPackageRejection.NotZip);
            }

            if (!span.Slice((int)start + LocalHeaderLength, nameLength).SequenceEqual(span.Slice(entry.NameOffset, entry.NameLength)))
            {
                throw Rejection(DocumentPackageRejection.NotZip);
            }

            if ((flags & DataDescriptorFlag) == 0 && (compressedSize != entry.CompressedSize || uncompressedSize != entry.UncompressedSize))
            {
                throw Rejection(DocumentPackageRejection.NotZip);
            }

            long dataEnd = dataStart + entry.CompressedSize;

            if (dataEnd > centralDirectoryOffset || (entry.Method == 0 && entry.CompressedSize != entry.UncompressedSize))
            {
                throw Rejection(DocumentPackageRejection.NotZip);
            }

            entry.DataOffset = dataStart;

            ranges.Add((start, dataEnd));
        }

        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));

        for (int index = 1; index < ranges.Count; index++)
        {
            if (ranges[index - 1].End > ranges[index].Start)
            {
                throw Rejection(DocumentPackageRejection.NotZip);
            }
        }
    }

    private ReadOnlyMemory<byte> Inflate(Entry entry, int declared, CancellationToken cancellationToken)
    {
        // One byte past the declared length is the most this reader will ever produce or hold: reading it proves
        // the stream outruns its header, and it is all an understated length can make the buffer grow to.
        long limit = (long)declared + 1;

        byte[] buffer = new byte[Math.Min(declared, InitialInflateBufferBytes)];

        int used = 0;

        using MemoryStream source = new(_array, _offset + (int)entry.DataOffset, (int)entry.CompressedSize, writable: false);

        using DeflateStream inflater = new(source, CompressionMode.Decompress);

        try
        {
            while (used < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (used == buffer.Length)
                {
                    Array.Resize(ref buffer, (int)Math.Min(Math.Max((long)buffer.Length * 2, InitialInflateBufferBytes), limit));
                }

                int read = inflater.Read(buffer, used, Math.Min(buffer.Length - used, InflateChunkBytes));

                if (read == 0)
                {
                    break;
                }

                used += read;
            }
        }
        catch (InvalidDataException)
        {
            throw Rejection(DocumentPackageRejection.NotZip);
        }

        if (used != declared)
        {
            // Short (the stream ended before its declared length) or long (it reached the extra byte).
            throw Rejection(DocumentPackageRejection.NotZip);
        }

        return new ReadOnlyMemory<byte>(buffer, 0, used);
    }

    private static DocumentPackageRejectedException Rejection(DocumentPackageRejection rejection) => new(rejection);

    private sealed class Entry(
        bool isDirectory,
        ushort method,
        long compressedSize,
        long uncompressedSize,
        long localOffset,
        int nameOffset,
        int nameLength)
    {
        public bool IsDirectory { get; } = isDirectory;

        public ushort Method { get; } = method;

        public long CompressedSize { get; } = compressedSize;

        public long UncompressedSize { get; } = uncompressedSize;

        public long LocalOffset { get; } = localOffset;

        public int NameOffset { get; } = nameOffset;

        public int NameLength { get; } = nameLength;

        public long DataOffset { get; set; }
    }
}
