using System.IO.Compression;
using System.Text;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Test-side builders for ZIP-based document packages (OOXML, OpenDocument, Visio) in two flavours.
/// </summary>
/// <remarks>
/// <see cref="Build"/> and <see cref="BuildStored"/> produce well-formed packages through
/// <see cref="ZipArchive"/> (test use only; production code never reads with it).
/// <see cref="RawZipPackage"/> and <see cref="RawZipEntry"/> write every byte of the container by hand, so
/// a test can make the central directory and the local headers disagree, overlap entries, lie about
/// sizes, flip encryption flags, or corrupt the end-of-central-directory record. Both are generic: nothing
/// here knows about OOXML or ODF part layouts, so later stages build their format fixtures on top by
/// passing entry names and XML text.
/// </remarks>
internal static class ComprehensionPackageBuilder
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Builds a valid, deflated ZIP. A name that ends in '/' becomes a directory entry and its content is
    /// ignored.
    /// </summary>
    public static byte[] Build(params (string Name, string Content)[] entries) =>
        BuildCore(CompressionLevel.Optimal, entries);

    /// <summary>Builds a valid ZIP whose entries are all stored (compression method 0).</summary>
    public static byte[] BuildStored(params (string Name, string Content)[] entries) =>
        BuildCore(CompressionLevel.NoCompression, entries);

    /// <summary>Raw-deflates <paramref name="data"/>, the form ZIP method 8 stores.</summary>
    public static byte[] Deflate(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using MemoryStream output = new();

        using (DeflateStream deflate = new(output, level, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>UTF-8 without a byte-order mark.</summary>
    public static byte[] Utf8Bytes(string text) => Utf8.GetBytes(text);

    /// <summary>The standard CRC-32 ZIP uses. BoundedZipPackage does not verify it; the raw writer stores it.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;

        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];

        for (uint index = 0; index < 256; index++)
        {
            uint value = index;

            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private static byte[] BuildCore(CompressionLevel level, (string Name, string Content)[] entries)
    {
        using MemoryStream stream = new();

        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, level);

                if (name.EndsWith('/'))
                {
                    continue;
                }

                using Stream entryStream = entry.Open();

                entryStream.Write(Utf8.GetBytes(content));
            }
        }

        return stream.ToArray();
    }
}

/// <summary>
/// One entry of a <see cref="RawZipPackage"/>. Every property that has a "central" and a "local" twin can
/// be set independently, which is how a test fabricates a package whose two views of an entry disagree.
/// Unset overrides take the honest value derived from <see cref="Data"/> and <see cref="Method"/>.
/// </summary>
internal sealed class RawZipEntry
{
    private byte[] _data;

    private ushort _method;

    private byte[]? _dataAreaBytes;

    private byte[]? _resolvedDataArea;

    private uint? _resolvedCrc;

    public RawZipEntry(byte[] nameBytes, byte[] data, ushort method)
    {
        NameBytes = nameBytes;

        _data = data;

        _method = method;
    }

    /// <summary>A deflated (method 8) entry whose name is the given text, UTF-8 encoded with no UTF-8 flag.</summary>
    public static RawZipEntry Deflated(string name, string content) =>
        new(ComprehensionPackageBuilder.Utf8Bytes(name), ComprehensionPackageBuilder.Utf8Bytes(content), 8);

    /// <summary>A deflated (method 8) entry over arbitrary bytes.</summary>
    public static RawZipEntry Deflated(string name, byte[] data) =>
        new(ComprehensionPackageBuilder.Utf8Bytes(name), data, 8);

    /// <summary>A stored (method 0) entry whose name is the given text.</summary>
    public static RawZipEntry Stored(string name, string content) =>
        new(ComprehensionPackageBuilder.Utf8Bytes(name), ComprehensionPackageBuilder.Utf8Bytes(content), 0);

    /// <summary>A stored (method 0) entry over arbitrary bytes.</summary>
    public static RawZipEntry Stored(string name, byte[] data) =>
        new(ComprehensionPackageBuilder.Utf8Bytes(name), data, 0);

    /// <summary>The entry name exactly as written to the central directory (and, by default, the local header).</summary>
    public byte[] NameBytes { get; set; }

    /// <summary>The honest uncompressed content.</summary>
    public byte[] Data
    {
        get => _data;

        set
        {
            _data = value;

            _resolvedDataArea = null;

            _resolvedCrc = null;
        }
    }

    /// <summary>The compression method in the central directory (and, by default, the local header).</summary>
    public ushort Method
    {
        get => _method;

        set
        {
            _method = value;

            _resolvedDataArea = null;
        }
    }

    /// <summary>The bytes that actually sit in the data area. Default: Data deflated, or Data itself when stored.</summary>
    public byte[]? DataAreaBytes
    {
        get => _dataAreaBytes;

        set
        {
            _dataAreaBytes = value;

            _resolvedDataArea = null;
        }
    }

    /// <summary>Sets general-purpose flag bit 11 (names are UTF-8) in both headers.</summary>
    public bool Utf8NameFlag { get; set; }

    /// <summary>Sets general-purpose flag bit 3 in both headers, writes zero local sizes and a trailing data descriptor.</summary>
    public bool DataDescriptor { get; set; }

    /// <summary>Extra general-purpose flag bits OR-ed into the central header (and the local one unless overridden).</summary>
    public ushort GeneralFlags { get; set; }

    /// <summary>When set, replaces the whole flag word of the local header (flags 3 and 11 are not added).</summary>
    public ushort? LocalGeneralFlags { get; set; }

    public ushort? LocalMethod { get; set; }

    public byte[]? LocalNameBytes { get; set; }

    public uint? CentralCompressedSize { get; set; }

    public uint? CentralUncompressedSize { get; set; }

    public uint? LocalCompressedSize { get; set; }

    public uint? LocalUncompressedSize { get; set; }

    /// <summary>Sets both the central and the local compressed size.</summary>
    public uint? CompressedSize
    {
        set
        {
            CentralCompressedSize = value;

            LocalCompressedSize = value;
        }
    }

    /// <summary>Sets both the central and the local uncompressed size.</summary>
    public uint? UncompressedSize
    {
        set
        {
            CentralUncompressedSize = value;

            LocalUncompressedSize = value;
        }
    }

    /// <summary>The local-header offset the central directory claims. Default: where the writer put it.</summary>
    public uint? CentralLocalHeaderOffset { get; set; }

    /// <summary>When true the local record is not written at all (a test plants one inside another entry's data).</summary>
    public bool OmitLocalRecord { get; set; }

    public ushort CentralDiskStart { get; set; }

    public byte[] CentralExtra { get; set; } = [];

    public byte[] LocalExtra { get; set; } = [];

    public byte[] CentralComment { get; set; } = [];

    /// <summary>The CRC-32 written to both headers. Default: the real CRC of <see cref="Data"/>.</summary>
    public uint? Crc32 { get; set; }

    /// <summary>The bytes the data area will hold: the override, otherwise the honest encoding of <see cref="Data"/>.</summary>
    public byte[] ResolveDataArea() =>
        _resolvedDataArea ??= DataAreaBytes ?? (Method == 8 ? ComprehensionPackageBuilder.Deflate(Data) : Data);

    internal uint ResolveCrc() =>
        Crc32 ?? (_resolvedCrc ??= ComprehensionPackageBuilder.Crc32(Data));

    internal ushort ResolveCentralFlags()
    {
        int flags = GeneralFlags;

        if (Utf8NameFlag)
        {
            flags |= 0x0800;
        }

        if (DataDescriptor)
        {
            flags |= 0x0008;
        }

        return (ushort)flags;
    }

    internal ushort ResolveLocalFlags()
    {
        if (LocalGeneralFlags is { } overridden)
        {
            return overridden;
        }

        return ResolveCentralFlags();
    }
}

/// <summary>
/// Writes a ZIP container byte by byte: local records, central directory, optional ZIP64 records, and the
/// end-of-central-directory record, each with independent overrides.
/// </summary>
internal sealed class RawZipPackage
{
    public const uint LocalHeaderSignature = 0x04034b50;

    public const uint CentralHeaderSignature = 0x02014b50;

    public const uint EndOfCentralDirectorySignature = 0x06054b50;

    public const uint Zip64LocatorSignature = 0x07064b50;

    public const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;

    public List<RawZipEntry> Entries { get; } = [];

    /// <summary>Total entry count field of the EOCD. Default: <see cref="Entries"/>.Count.</summary>
    public ushort? TotalEntries { get; set; }

    /// <summary>Entries-on-this-disk field of the EOCD. Default: the same as the total.</summary>
    public ushort? EntriesOnDisk { get; set; }

    /// <summary>Central directory size field. Default: the real size (plus <see cref="CentralDirectoryPadding"/>).</summary>
    public uint? CentralDirectorySize { get; set; }

    /// <summary>Central directory offset field. Default: where the writer put it.</summary>
    public uint? CentralDirectoryOffset { get; set; }

    public ushort DiskNumber { get; set; }

    public ushort CentralDirectoryDisk { get; set; }

    /// <summary>Zero bytes written between the last central record and the EOCD (counted in the default size).</summary>
    public int CentralDirectoryPadding { get; set; }

    /// <summary>The archive comment bytes.</summary>
    public byte[] Comment { get; set; } = [];

    /// <summary>The comment-length field. Default: <see cref="Comment"/>.Length.</summary>
    public ushort? CommentLength { get; set; }

    /// <summary>Bytes appended after the EOCD (and its comment).</summary>
    public byte[] TrailingBytes { get; set; } = [];

    /// <summary>Writes a ZIP64 end-of-central-directory record and its locator just before the EOCD.</summary>
    public bool Zip64Records { get; set; }

    /// <summary>Convenience: a package of honest deflated entries.</summary>
    public static RawZipPackage Of(params RawZipEntry[] entries)
    {
        RawZipPackage package = new();

        package.Entries.AddRange(entries);

        return package;
    }

    /// <summary>The honest local record (header, name, extra, data, optional descriptor) for an entry, for planting inside another entry's data.</summary>
    public static byte[] LocalRecord(RawZipEntry entry)
    {
        using MemoryStream stream = new();

        WriteLocalRecord(stream, entry);

        return stream.ToArray();
    }

    public byte[] ToBytes()
    {
        using MemoryStream stream = new();

        long[] offsets = new long[Entries.Count];

        for (int index = 0; index < Entries.Count; index++)
        {
            offsets[index] = stream.Position;

            if (!Entries[index].OmitLocalRecord)
            {
                WriteLocalRecord(stream, Entries[index]);
            }
        }

        long centralStart = stream.Position;

        for (int index = 0; index < Entries.Count; index++)
        {
            WriteCentralRecord(stream, Entries[index], offsets[index]);
        }

        stream.Write(new byte[CentralDirectoryPadding]);

        long centralSize = stream.Position - centralStart;

        if (Zip64Records)
        {
            WriteZip64Records(stream, centralStart, centralSize);
        }

        WriteEndOfCentralDirectory(stream, centralStart, centralSize);

        stream.Write(TrailingBytes);

        return stream.ToArray();
    }

    private static void WriteLocalRecord(Stream stream, RawZipEntry entry)
    {
        byte[] area = entry.ResolveDataArea();

        byte[] name = entry.LocalNameBytes ?? entry.NameBytes;

        ushort localFlags = entry.ResolveLocalFlags();

        bool descriptor = (localFlags & 0x0008) != 0;

        uint crc = entry.ResolveCrc();

        uint compressed = entry.LocalCompressedSize ?? (descriptor ? 0u : (uint)area.Length);

        uint uncompressed = entry.LocalUncompressedSize ?? (descriptor ? 0u : (uint)entry.Data.Length);

        BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(LocalHeaderSignature);

        writer.Write((ushort)20);

        writer.Write(localFlags);

        writer.Write(entry.LocalMethod ?? entry.Method);

        writer.Write((ushort)0);

        writer.Write((ushort)0x0021);

        writer.Write(descriptor ? 0u : crc);

        writer.Write(compressed);

        writer.Write(uncompressed);

        writer.Write((ushort)name.Length);

        writer.Write((ushort)entry.LocalExtra.Length);

        writer.Write(name);

        writer.Write(entry.LocalExtra);

        writer.Write(area);

        if (descriptor)
        {
            writer.Write(0x08074b50u);

            writer.Write(crc);

            writer.Write((uint)area.Length);

            writer.Write((uint)entry.Data.Length);
        }

        writer.Flush();
    }

    private static void WriteCentralRecord(Stream stream, RawZipEntry entry, long localOffset)
    {
        byte[] area = entry.ResolveDataArea();

        uint crc = entry.ResolveCrc();

        BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(CentralHeaderSignature);

        writer.Write((ushort)20);

        writer.Write((ushort)20);

        writer.Write(entry.ResolveCentralFlags());

        writer.Write(entry.Method);

        writer.Write((ushort)0);

        writer.Write((ushort)0x0021);

        writer.Write(crc);

        writer.Write(entry.CentralCompressedSize ?? (uint)area.Length);

        writer.Write(entry.CentralUncompressedSize ?? (uint)entry.Data.Length);

        writer.Write((ushort)entry.NameBytes.Length);

        writer.Write((ushort)entry.CentralExtra.Length);

        writer.Write((ushort)entry.CentralComment.Length);

        writer.Write(entry.CentralDiskStart);

        writer.Write((ushort)0);

        writer.Write(0u);

        writer.Write(entry.CentralLocalHeaderOffset ?? (uint)localOffset);

        writer.Write(entry.NameBytes);

        writer.Write(entry.CentralExtra);

        writer.Write(entry.CentralComment);

        writer.Flush();
    }

    private void WriteZip64Records(Stream stream, long centralStart, long centralSize)
    {
        BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        long zip64Offset = stream.Position;

        writer.Write(Zip64EndOfCentralDirectorySignature);

        writer.Write(44UL);

        writer.Write((ushort)45);

        writer.Write((ushort)45);

        writer.Write(0u);

        writer.Write(0u);

        writer.Write((ulong)Entries.Count);

        writer.Write((ulong)Entries.Count);

        writer.Write((ulong)centralSize);

        writer.Write((ulong)centralStart);

        writer.Write(Zip64LocatorSignature);

        writer.Write(0u);

        writer.Write((ulong)zip64Offset);

        writer.Write(1u);

        writer.Flush();
    }

    private void WriteEndOfCentralDirectory(Stream stream, long centralStart, long centralSize)
    {
        BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);

        ushort total = TotalEntries ?? (ushort)Entries.Count;

        writer.Write(EndOfCentralDirectorySignature);

        writer.Write(DiskNumber);

        writer.Write(CentralDirectoryDisk);

        writer.Write(EntriesOnDisk ?? total);

        writer.Write(total);

        writer.Write(CentralDirectorySize ?? (uint)centralSize);

        writer.Write(CentralDirectoryOffset ?? (uint)centralStart);

        writer.Write(CommentLength ?? (ushort)Comment.Length);

        writer.Write(Comment);

        writer.Flush();
    }
}
