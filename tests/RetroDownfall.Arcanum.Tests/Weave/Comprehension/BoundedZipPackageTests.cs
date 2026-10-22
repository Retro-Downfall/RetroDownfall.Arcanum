using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

public sealed class BoundedZipPackageTests
{
    private const string Marker = "secretmarker";

    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static readonly Lazy<byte[]> LargePackage = new(() =>
    {
        byte[] data = new byte[(int)BoundedZipPackage.MaximumEntryBytes];

        new Random(42).NextBytes(data);

        RawZipEntry big = new(ComprehensionPackageBuilder.Utf8Bytes("big.bin"), data, 8)
        {
            DataAreaBytes = ComprehensionPackageBuilder.Deflate(data, CompressionLevel.Fastest),
        };

        return RawZipPackage.Of(big, RawZipEntry.Deflated("tiny.txt", "x")).ToBytes();
    });

    // ---------------------------------------------------------------- valid packages

    [Fact]
    public void Open_DeflatedPackage_ReadsEveryEntryBack()
    {
        byte[] bytes = ComprehensionPackageBuilder.Build(
            ("[Content_Types].xml", "<Types/>"),
            ("word/document.xml", "<w:document>hello</w:document>"));

        BoundedZipPackage package = Open(bytes);

        Assert.True(package.Contains("word/document.xml"));

        Assert.Equal(
            "<w:document>hello</w:document>",
            Encoding.UTF8.GetString(package.ReadEntry("word/document.xml", CancellationToken.None).Span));

        Assert.Equal("<Types/>", Encoding.UTF8.GetString(package.ReadEntry("[Content_Types].xml", CancellationToken.None).Span));
    }

    [Fact]
    public void Open_EntryLookup_IsCaseInsensitive()
    {
        BoundedZipPackage package = Open(ComprehensionPackageBuilder.Build(("Word/Document.xml", "<d/>")));

        Assert.True(package.Contains("word/document.XML"));

        Assert.Equal("<d/>", Encoding.UTF8.GetString(package.ReadEntry("WORD/DOCUMENT.XML", CancellationToken.None).Span));

        Assert.False(package.Contains("word/other.xml"));
    }

    [Fact]
    public void ReadEntry_StoredEntry_ReturnsASliceOfTheOriginalBytesWithoutCopying()
    {
        byte[] bytes = ComprehensionPackageBuilder.BuildStored(("mimetype", "application/test"), ("content.xml", "<c/>"));

        BoundedZipPackage package = Open(bytes);

        ReadOnlyMemory<byte> content = package.ReadEntry("content.xml", CancellationToken.None);

        Assert.Equal("<c/>", Encoding.UTF8.GetString(content.Span));

        Assert.True(MemoryMarshal.TryGetArray(content, out ArraySegment<byte> segment));

        Assert.Same(bytes, segment.Array);
    }

    [Fact]
    public void Open_PackageWithDirectoryEntry_ListsTheDirectoryButNeverReadsItAsAPart()
    {
        byte[] bytes = ComprehensionPackageBuilder.Build(("word/", string.Empty), ("word/document.xml", "<d/>"));

        BoundedZipPackage package = Open(bytes);

        Assert.True(package.Contains("word/"));

        Assert.True(package.Contains("word/document.xml"));

        Rejected(DocumentPackageRejection.MissingPart, () => package.ReadEntry("word/", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_AbsentEntry_IsMissingPartAndDoesNotEchoTheRequestedName()
    {
        BoundedZipPackage package = Open(ComprehensionPackageBuilder.Build(("a.xml", "<a/>")));

        DocumentPackageRejectedException exception = Rejected(
            DocumentPackageRejection.MissingPart,
            () => package.ReadEntry($"{Marker}.xml", CancellationToken.None));

        Assert.DoesNotContain(Marker, exception.ToString());
    }

    [Fact]
    public void ReadEntry_EmptyEntry_ReturnsEmptyMemory()
    {
        BoundedZipPackage package = Open(ComprehensionPackageBuilder.Build(("empty.xml", string.Empty)));

        Assert.True(package.Contains("empty.xml"));

        Assert.True(package.ReadEntry("empty.xml", CancellationToken.None).IsEmpty);
    }

    [Fact]
    public void Open_RawBuilderBaseline_OpensAndRoundTrips()
    {
        BoundedZipPackage package = Open(Baseline().ToBytes());

        Assert.Equal("<w:document/>", Read(package, "word/document.xml"));

        Assert.Equal("<w:styles/>", Read(package, "word/styles.xml"));
    }

    [Fact]
    public void Open_EntryWithDataDescriptor_AcceptsZeroedLocalSizes()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[0].DataDescriptor = true;

        Assert.Equal("<w:document/>", Read(Open(raw.ToBytes()), "word/document.xml"));
    }

    [Fact]
    public void Open_ArchiveCommentWithConsistentLength_Opens()
    {
        RawZipPackage raw = Baseline();

        raw.Comment = Encoding.ASCII.GetBytes("a perfectly ordinary archive comment");

        Assert.True(Open(raw.ToBytes()).Contains("word/document.xml"));
    }

    [Fact]
    public void Open_EmptyArchive_OpensWithNoEntries()
    {
        BoundedZipPackage package = Open(new RawZipPackage().ToBytes());

        Assert.False(package.Contains("anything"));

        Rejected(DocumentPackageRejection.MissingPart, () => package.ReadEntry("anything", CancellationToken.None));
    }

    [Fact]
    public void Open_MemoryThatIsNotArrayBacked_StillOpensAndReads()
    {
        byte[] bytes = ComprehensionPackageBuilder.Build(("a.xml", "<a/>"));

        using CopyingMemoryManager manager = new(bytes);

        ReadOnlyMemory<byte> memory = manager.Memory;

        Assert.False(MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> _));

        Assert.Equal("<a/>", Read(BoundedZipPackage.Open(memory, CancellationToken.None), "a.xml"));
    }

    [Fact]
    public void Open_ArraySegmentWithOffset_ResolvesAllOffsetsRelativeToTheSegment()
    {
        byte[] inner = ComprehensionPackageBuilder.Build(("a.xml", "<a/>"), ("b.xml", "<b/>"));

        byte[] padded = new byte[inner.Length + 100];

        inner.CopyTo(padded, 37);

        BoundedZipPackage package = BoundedZipPackage.Open(new ReadOnlyMemory<byte>(padded, 37, inner.Length), CancellationToken.None);

        Assert.Equal("<b/>", Read(package, "b.xml"));
    }

    [Fact]
    public void Open_CancelledToken_ThrowsOperationCanceled()
    {
        byte[] bytes = Baseline().ToBytes();

        Assert.Throws<OperationCanceledException>(() => BoundedZipPackage.Open(bytes, new CancellationToken(canceled: true)));
    }

    // ---------------------------------------------------------------- container identification

    [Fact]
    public void Open_CompoundFileSignature_IsEncrypted()
    {
        // Password-protected OOXML is an OLE2 EncryptedPackage, not a ZIP.
        byte[] bytes = new byte[4096];

        CompoundFileSignature.CopyTo(bytes, 0);

        Rejected(DocumentPackageRejection.Encrypted, () => Open(bytes));
    }

    [Fact]
    public void Open_CompoundFileSignatureAloneShorterThanAnEocd_IsStillEncrypted()
    {
        Rejected(DocumentPackageRejection.Encrypted, () => Open(CompoundFileSignature));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(21)]
    public void Open_TooShortToHoldAnEocd_IsNotZip(int length)
    {
        Rejected(DocumentPackageRejection.NotZip, () => Open(new byte[length]));
    }

    [Fact]
    public void Open_ArbitraryBytes_IsNotZip()
    {
        byte[] bytes = new byte[5000];

        new Random(7).NextBytes(bytes);

        Rejected(DocumentPackageRejection.NotZip, () => Open(bytes));
    }

    [Fact]
    public void Open_PlainText_IsNotZip()
    {
        Rejected(DocumentPackageRejection.NotZip, () => Open(Encoding.UTF8.GetBytes("<html><body>not a package at all, just text</body></html>")));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(40)]
    public void Open_PackageTruncatedFromTheEnd_IsNotZip(int cut)
    {
        byte[] bytes = Baseline().ToBytes();

        Rejected(DocumentPackageRejection.NotZip, () => Open(bytes.AsMemory(0, bytes.Length - cut)));
    }

    [Fact]
    public void Open_EocdCommentLengthLargerThanTheRemainder_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.Comment = Encoding.ASCII.GetBytes("short");

        raw.CommentLength = 50;

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_BytesAppendedAfterTheEocd_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.TrailingBytes = Encoding.ASCII.GetBytes("trailing garbage");

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    // ---------------------------------------------------------------- unsupported container features

    [Fact]
    public void Open_Zip64EndOfCentralDirectoryLocator_IsUnsupportedContainer()
    {
        RawZipPackage raw = Baseline();

        raw.Zip64Records = true;

        Rejected(DocumentPackageRejection.UnsupportedContainer, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_Zip64MarkerInTheEntryCount_IsUnsupportedContainer()
    {
        RawZipPackage raw = Baseline();

        raw.TotalEntries = 0xFFFF;

        raw.EntriesOnDisk = 0xFFFF;

        Rejected(DocumentPackageRejection.UnsupportedContainer, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_Zip64MarkerInTheCentralDirectorySize_IsUnsupportedContainer()
    {
        RawZipPackage raw = Baseline();

        raw.CentralDirectorySize = 0xFFFFFFFF;

        Rejected(DocumentPackageRejection.UnsupportedContainer, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_Zip64MarkerInTheCentralDirectoryOffset_IsUnsupportedContainer()
    {
        RawZipPackage raw = Baseline();

        raw.CentralDirectoryOffset = 0xFFFFFFFF;

        Rejected(DocumentPackageRejection.UnsupportedContainer, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_NonZeroDiskNumber_IsUnsupportedContainer()
    {
        RawZipPackage raw = Baseline();

        raw.DiskNumber = 1;

        Rejected(DocumentPackageRejection.UnsupportedContainer, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_NonZeroCentralDirectoryDisk_IsUnsupportedContainer()
    {
        RawZipPackage raw = Baseline();

        raw.CentralDirectoryDisk = 1;

        Rejected(DocumentPackageRejection.UnsupportedContainer, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_EntriesOnDiskDifferentFromTotal_IsUnsupportedContainer()
    {
        RawZipPackage raw = Baseline();

        raw.EntriesOnDisk = 1;

        Rejected(DocumentPackageRejection.UnsupportedContainer, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_EntryWithZip64SizeMarker_IsUnsupportedContainer()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[0].CentralCompressedSize = 0xFFFFFFFF;

        Rejected(DocumentPackageRejection.UnsupportedContainer, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_EntryWithZip64UncompressedSizeMarker_IsUnsupportedContainer()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[0].CentralUncompressedSize = 0xFFFFFFFF;

        Rejected(DocumentPackageRejection.UnsupportedContainer, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_EntryWithZip64LocalOffsetMarker_IsUnsupportedContainer()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[0].CentralLocalHeaderOffset = 0xFFFFFFFF;

        Rejected(DocumentPackageRejection.UnsupportedContainer, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_EntryStartingOnAnotherDisk_IsUnsupportedContainer()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[0].CentralDiskStart = 1;

        Rejected(DocumentPackageRejection.UnsupportedContainer, () => Open(raw.ToBytes()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(93)]
    public void Open_CompressionMethodOtherThanStoredOrDeflate_IsUnsupportedContainer(ushort method)
    {
        RawZipPackage raw = Baseline();

        raw.Entries[1].Method = method;

        raw.Entries[1].DataAreaBytes = [1, 2, 3];

        Rejected(DocumentPackageRejection.UnsupportedContainer, () => Open(raw.ToBytes()));
    }

    // ---------------------------------------------------------------- encryption

    [Theory]
    [InlineData((ushort)0x0001)]
    [InlineData((ushort)0x0040)]
    [InlineData((ushort)0x2000)]
    public void Open_EncryptionFlagInTheCentralDirectory_IsEncrypted(ushort flag)
    {
        RawZipPackage raw = Baseline();

        raw.Entries[1].GeneralFlags = flag;

        raw.Entries[1].LocalGeneralFlags = 0;

        Rejected(DocumentPackageRejection.Encrypted, () => Open(raw.ToBytes()));
    }

    [Theory]
    [InlineData((ushort)0x0001)]
    [InlineData((ushort)0x0040)]
    [InlineData((ushort)0x2000)]
    public void Open_EncryptionFlagOnlyInALocalHeader_IsEncrypted(ushort flag)
    {
        RawZipPackage raw = Baseline();

        raw.Entries[1].LocalGeneralFlags = flag;

        Rejected(DocumentPackageRejection.Encrypted, () => Open(raw.ToBytes()));
    }

    // ---------------------------------------------------------------- entry-count bound

    [Fact]
    public void Open_ExactlyTheMaximumEntryCount_Opens()
    {
        RawZipPackage raw = ManyEntries(BoundedZipPackage.MaximumEntries);

        BoundedZipPackage package = Open(raw.ToBytes());

        Assert.True(package.Contains("e8191.txt"));
    }

    [Fact]
    public void Open_OneMoreThanTheMaximumEntryCount_IsEntryBounds()
    {
        // Catches removing the EOCD-count guard: the walk would otherwise succeed over 8193 valid records.
        RawZipPackage raw = ManyEntries(BoundedZipPackage.MaximumEntries + 1);

        Rejected(DocumentPackageRejection.EntryBounds, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_HugeDeclaredEntryCountOverATinyDirectory_IsEntryBoundsDecidedBeforeAnyWalk()
    {
        // The EOCD count field is 16 bits wide, so 60000 is the largest honest-looking oversized claim that is not a
        // ZIP64 marker. The central directory holds one record: a walk would fail NotZip, the guard must fire first.
        RawZipPackage raw = Baseline();

        raw.TotalEntries = 60000;

        raw.EntriesOnDisk = 60000;

        Rejected(DocumentPackageRejection.EntryBounds, () => Open(raw.ToBytes()));
    }

    // ---------------------------------------------------------------- entry names

    [Theory]
    [InlineData("../secretmarker.xml")]
    [InlineData("a/../../secretmarker.xml")]
    [InlineData("a/../secretmarker.xml")]
    [InlineData("a/./secretmarker.xml")]
    [InlineData("./secretmarker.xml")]
    [InlineData("a/secretmarker/..")]
    [InlineData("/secretmarker.xml")]
    [InlineData("a\\secretmarker.xml")]
    [InlineData("C:secretmarker.xml")]
    [InlineData("a//secretmarker.xml")]
    [InlineData("secretmarker//")]
    [InlineData("a\u0001secretmarker.xml")]
    [InlineData("a\u007Fsecretmarker.xml")]
    [InlineData("a\nsecretmarker.xml")]
    [InlineData("")]
    public void Open_UnsafeEntryName_IsRejectedWithoutEchoingTheName(string name)
    {
        RawZipPackage raw = Baseline();

        raw.Entries.Add(RawZipEntry.Deflated(name, "<x/>"));

        DocumentPackageRejectedException exception = Rejected(DocumentPackageRejection.UnsafeEntryName, () => Open(raw.ToBytes()));

        Assert.DoesNotContain(Marker, exception.ToString());
    }

    [Fact]
    public void Open_EntryNameAtTheByteLimit_Opens()
    {
        RawZipPackage raw = Baseline();

        raw.Entries.Add(RawZipEntry.Deflated(new string('n', BoundedZipPackage.MaximumEntryNameBytes), "<x/>"));

        Assert.True(Open(raw.ToBytes()).Contains(new string('n', BoundedZipPackage.MaximumEntryNameBytes)));
    }

    [Fact]
    public void Open_EntryNameOverTheByteLimit_IsUnsafeEntryName()
    {
        RawZipPackage raw = Baseline();

        raw.Entries.Add(RawZipEntry.Deflated(new string('n', BoundedZipPackage.MaximumEntryNameBytes + 1), "<x/>"));

        Rejected(DocumentPackageRejection.UnsafeEntryName, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_NamesDifferingOnlyInCase_AreDuplicates()
    {
        RawZipPackage raw = Baseline();

        raw.Entries.Add(RawZipEntry.Deflated("WORD/Document.XML", "<evil/>"));

        DocumentPackageRejectedException exception = Rejected(DocumentPackageRejection.UnsafeEntryName, () => Open(raw.ToBytes()));

        Assert.DoesNotContain("word/", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_ExactDuplicateNames_AreRejected()
    {
        RawZipPackage raw = Baseline();

        raw.Entries.Add(RawZipEntry.Deflated("word/document.xml", "<again/>"));

        Rejected(DocumentPackageRejection.UnsafeEntryName, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_Utf8FlaggedName_DecodesAsUtf8()
    {
        RawZipPackage raw = Baseline();

        RawZipEntry entry = new(Encoding.UTF8.GetBytes("café.xml"), Encoding.UTF8.GetBytes("<c/>"), 8) { Utf8NameFlag = true };

        raw.Entries.Add(entry);

        BoundedZipPackage package = Open(raw.ToBytes());

        Assert.Equal("<c/>", Read(package, "café.xml"));
    }

    [Fact]
    public void Open_UnflaggedName_DecodesAsLatin1()
    {
        RawZipPackage raw = Baseline();

        RawZipEntry entry = new([(byte)'c', (byte)'a', (byte)'f', 0xE9, (byte)'.', (byte)'x', (byte)'m', (byte)'l'], Encoding.UTF8.GetBytes("<c/>"), 8);

        raw.Entries.Add(entry);

        BoundedZipPackage package = Open(raw.ToBytes());

        Assert.Equal("<c/>", Read(package, "café.xml"));
    }

    [Fact]
    public void Open_Utf8FlaggedNameWithInvalidUtf8_IsUnsafeEntryName()
    {
        RawZipPackage raw = Baseline();

        raw.Entries.Add(new RawZipEntry([(byte)'a', 0xFF, 0xFE, (byte)'.', (byte)'x'], Encoding.UTF8.GetBytes("<c/>"), 8) { Utf8NameFlag = true });

        Rejected(DocumentPackageRejection.UnsafeEntryName, () => Open(raw.ToBytes()));
    }

    // ---------------------------------------------------------------- central directory structure

    [Fact]
    public void Open_CentralDirectoryOffsetPlusSizeBeyondTheEocd_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.CentralDirectorySize = 100_000;

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_CentralDirectoryShorterThanItsRecords_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        byte[] honest = raw.ToBytes();

        raw.CentralDirectorySize = (uint)(CentralDirectorySize(honest) - 10);

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_WalkEndingBeforeTheCentralDirectoryEnd_IsNotZip()
    {
        // Catches removing the "walk must end exactly at cdOffset + cdSize" check: slack after the records is ambiguous.
        RawZipPackage raw = Baseline();

        raw.CentralDirectoryPadding = 12;

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_FewerEntriesDeclaredThanPresent_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.TotalEntries = 1;

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_MoreEntriesDeclaredThanPresent_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.TotalEntries = 3;

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_CentralDirectoryOffsetPointingAtNonCentralBytes_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.CentralDirectoryOffset = 0;

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    // ---------------------------------------------------------------- local header validation

    [Fact]
    public void Open_LocalNameDifferingFromCentralName_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[1].LocalNameBytes = Encoding.UTF8.GetBytes("word/stylez.xml");

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_LocalNameOfADifferentLength_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[1].LocalNameBytes = Encoding.UTF8.GetBytes("word/styles.xml.extra");

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_LocalMethodDifferingFromCentralMethod_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[0].LocalMethod = 0;

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_LocalCompressedSizeDifferingFromCentral_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[0].LocalCompressedSize = 3;

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_LocalUncompressedSizeDifferingFromCentral_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[0].LocalUncompressedSize = 3;

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_CentralOffsetNotPointingAtALocalHeader_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[1].CentralLocalHeaderOffset = 3;

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_LocalHeaderLyingAtOrBeyondTheCentralDirectory_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[1].CentralLocalHeaderOffset = (uint)CentralDirectoryOffset(raw.ToBytes());

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_StoredEntryWhoseSizesDisagree_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        RawZipEntry stored = RawZipEntry.Stored("stored.xml", new byte[10]);

        stored.DataAreaBytes = new byte[5];

        raw.Entries.Add(stored);

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_EntryPlantedInsideAnotherEntrysData_IsNotZip()
    {
        // The classic non-recursive zip bomb: many central records whose data overlaps a shared region. Every
        // name, size and method here is individually consistent, so only the overlap check can refuse it.
        RawZipEntry inner = RawZipEntry.Deflated("b.xml", "<b/>");

        RawZipEntry outer = RawZipEntry.Stored("a.xml", RawZipPackage.LocalRecord(inner));

        inner.OmitLocalRecord = true;

        inner.CentralLocalHeaderOffset = (uint)(30 + "a.xml".Length);

        RawZipPackage raw = RawZipPackage.Of(outer, inner);

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_EntryDataRunningIntoTheNextEntry_IsNotZip()
    {
        RawZipPackage raw = Baseline();

        raw.Entries[0].CompressedSize = (uint)(raw.Entries[0].ResolveDataArea().Length + 20);

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    [Fact]
    public void Open_EntryDataRunningIntoTheCentralDirectory_IsNotZip()
    {
        // Catches removing the "entry range must end at or before cdOffset" check; nothing overlaps another entry.
        RawZipPackage raw = Baseline();

        raw.Entries[1].CompressedSize = (uint)(raw.Entries[1].ResolveDataArea().Length + 10_000);

        Rejected(DocumentPackageRejection.NotZip, () => Open(raw.ToBytes()));
    }

    // ---------------------------------------------------------------- inflation bounds

    [Fact]
    public void ReadEntry_HighInflationRatio_IsExpansionBoundsDecidedBeforeInflating()
    {
        // 20 MiB of 'a' deflates to ~20 KiB (ratio ~1000), declared honestly.
        byte[] data = new byte[20 * 1024 * 1024];

        Array.Fill(data, (byte)'a');

        BoundedZipPackage package = Open(RawZipPackage.Of(RawZipEntry.Deflated("bomb.bin", data)).ToBytes());

        Rejected(DocumentPackageRejection.ExpansionBounds, () => package.ReadEntry("bomb.bin", CancellationToken.None));

        long allocated = Allocated(() => Assert.Throws<DocumentPackageRejectedException>(() => package.ReadEntry("bomb.bin", CancellationToken.None)));

        Assert.True(allocated < 256 * 1024, $"Ratio refusal allocated {allocated} bytes; it must be decided before inflating.");
    }

    [Fact]
    public void ReadEntry_HighRatioAtTheFloorSize_IsAllowed()
    {
        byte[] data = new byte[(int)BoundedZipPackage.CompressionRatioFloorBytes];

        Array.Fill(data, (byte)'a');

        BoundedZipPackage package = Open(RawZipPackage.Of(RawZipEntry.Deflated("floor.bin", data)).ToBytes());

        Assert.Equal(data.Length, package.ReadEntry("floor.bin", CancellationToken.None).Length);
    }

    [Fact]
    public void ReadEntry_HighRatioOneByteOverTheFloor_IsExpansionBounds()
    {
        byte[] data = new byte[(int)BoundedZipPackage.CompressionRatioFloorBytes + 1];

        Array.Fill(data, (byte)'a');

        BoundedZipPackage package = Open(RawZipPackage.Of(RawZipEntry.Deflated("floor.bin", data)).ToBytes());

        Rejected(DocumentPackageRejection.ExpansionBounds, () => package.ReadEntry("floor.bin", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_RatioExactlyAtTheLimit_ProceedsToInflationButOneByteMoreIsRefused()
    {
        // 10,000 junk bytes declared as exactly 200x: the ratio check passes and inflation then finds the junk corrupt.
        // 200x + 1: refused before inflating.
        RawZipEntry atLimit = RawZipEntry.Deflated("at.bin", new byte[10]);

        atLimit.DataAreaBytes = Enumerable.Repeat((byte)0xFF, 10_000).ToArray();

        atLimit.UncompressedSize = 10_000 * BoundedZipPackage.MaximumCompressionRatio;

        RawZipEntry over = RawZipEntry.Deflated("over.bin", new byte[10]);

        over.DataAreaBytes = Enumerable.Repeat((byte)0xFF, 10_000).ToArray();

        over.UncompressedSize = (10_000 * BoundedZipPackage.MaximumCompressionRatio) + 1;

        BoundedZipPackage package = Open(RawZipPackage.Of(atLimit, over).ToBytes());

        Rejected(DocumentPackageRejection.NotZip, () => package.ReadEntry("at.bin", CancellationToken.None));

        Rejected(DocumentPackageRejection.ExpansionBounds, () => package.ReadEntry("over.bin", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_DeclaredSizeOverThePerEntryCap_IsExpansionBounds()
    {
        // The ratio is kept legal (200 KB of junk for 33 MiB declared is under 200x) so only the cap can refuse it.
        RawZipEntry entry = RawZipEntry.Deflated("big.bin", new byte[10]);

        entry.DataAreaBytes = new byte[200_000];

        entry.UncompressedSize = (uint)(BoundedZipPackage.MaximumEntryBytes + 1024 * 1024);

        BoundedZipPackage package = Open(RawZipPackage.Of(entry).ToBytes());

        Rejected(DocumentPackageRejection.ExpansionBounds, () => package.ReadEntry("big.bin", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_StoredEntryOverThePerEntryCap_IsExpansionBounds()
    {
        byte[] data = new byte[(int)BoundedZipPackage.MaximumEntryBytes + 1];

        BoundedZipPackage package = Open(RawZipPackage.Of(RawZipEntry.Stored("big.bin", data)).ToBytes());

        Rejected(DocumentPackageRejection.ExpansionBounds, () => package.ReadEntry("big.bin", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_EntryExactlyAtThePerEntryCap_Inflates()
    {
        BoundedZipPackage package = Open(LargePackage.Value);

        Assert.Equal((int)BoundedZipPackage.MaximumEntryBytes, package.ReadEntry("big.bin", CancellationToken.None).Length);
    }

    [Fact]
    public void ReadEntry_AggregateAcrossCalls_IsBoundedAtExactlyTheAggregateCap()
    {
        // Two 32 MiB reads land exactly on the 64 MiB cap and are allowed; the next byte requested is refused.
        BoundedZipPackage package = Open(LargePackage.Value);

        package.ReadEntry("big.bin", CancellationToken.None);

        package.ReadEntry("big.bin", CancellationToken.None);

        Rejected(DocumentPackageRejection.ExpansionBounds, () => package.ReadEntry("tiny.txt", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_AggregateCountsStoredEntriesToo()
    {
        byte[] data = new byte[22 * 1024 * 1024];

        BoundedZipPackage package = Open(RawZipPackage.Of(RawZipEntry.Stored("a.bin", data)).ToBytes());

        package.ReadEntry("a.bin", CancellationToken.None);

        package.ReadEntry("a.bin", CancellationToken.None);

        Rejected(DocumentPackageRejection.ExpansionBounds, () => package.ReadEntry("a.bin", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_UnderstatedDeclaredLength_IsNotZipAndBoundedByBytesProduced()
    {
        // Sensitivity: the "limit = declared + 1" bound in Inflate. The entry declares 1,000 bytes but its deflate
        // stream inflates to 20 MiB; the reader must stop one byte past the declared length. The honest control entry
        // declares and produces 1,000 bytes, so the understated read may cost no more than the control does.
        byte[] real = new byte[20 * 1024 * 1024];

        Array.Fill(real, (byte)'a');

        RawZipEntry understated = RawZipEntry.Deflated("understated.bin", real);

        understated.UncompressedSize = 1_000;

        byte[] honest = new byte[1_000];

        new Random(5).NextBytes(honest);

        BoundedZipPackage package = Open(RawZipPackage.Of(understated, RawZipEntry.Deflated("control.bin", honest)).ToBytes());

        // Warm up so JIT and first-use allocations are not billed to either measurement.
        Rejected(DocumentPackageRejection.NotZip, () => package.ReadEntry("understated.bin", CancellationToken.None));

        package.ReadEntry("control.bin", CancellationToken.None);

        long beforeControl = GC.GetAllocatedBytesForCurrentThread();

        package.ReadEntry("control.bin", CancellationToken.None);

        long control = GC.GetAllocatedBytesForCurrentThread() - beforeControl;

        long before = GC.GetAllocatedBytesForCurrentThread();

        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(
            () => package.ReadEntry("understated.bin", CancellationToken.None));

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(DocumentPackageRejection.NotZip, exception.Rejection);

        Assert.True(allocated < 1024 * 1024, $"An understated length allocated {allocated} bytes; it must stay bounded by the declared size.");

        Assert.True(allocated <= control + 8 * 1024, $"An understated length allocated {allocated} bytes against {control} for an honest entry of the same declared size.");
    }

    [Fact]
    public void ReadEntry_OverstatedDeclaredLength_IsNotZip()
    {
        RawZipEntry entry = RawZipEntry.Deflated("overstated.xml", "<short/>");

        entry.UncompressedSize = 5_000;

        BoundedZipPackage package = Open(RawZipPackage.Of(entry).ToBytes());

        Rejected(DocumentPackageRejection.NotZip, () => package.ReadEntry("overstated.xml", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_StreamProducingExactlyOneByteMoreThanDeclared_IsNotZip()
    {
        RawZipEntry entry = RawZipEntry.Deflated("off-by-one.xml", new byte[101]);

        entry.UncompressedSize = 100;

        BoundedZipPackage package = Open(RawZipPackage.Of(entry).ToBytes());

        Rejected(DocumentPackageRejection.NotZip, () => package.ReadEntry("off-by-one.xml", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_StreamProducingExactlyTheDeclaredLength_Succeeds()
    {
        byte[] data = new byte[BoundedZipPackageTests.PowerOfTwoBoundary];

        new Random(3).NextBytes(data);

        BoundedZipPackage package = Open(RawZipPackage.Of(RawZipEntry.Deflated("exact.bin", data)).ToBytes());

        Assert.True(data.AsSpan().SequenceEqual(package.ReadEntry("exact.bin", CancellationToken.None).Span));
    }

    [Fact]
    public void ReadEntry_CorruptDeflateData_IsNotZip()
    {
        RawZipEntry entry = RawZipEntry.Deflated("corrupt.xml", new byte[50]);

        entry.DataAreaBytes = Enumerable.Repeat((byte)0xFF, 40).ToArray();

        BoundedZipPackage package = Open(RawZipPackage.Of(entry).ToBytes());

        Rejected(DocumentPackageRejection.NotZip, () => package.ReadEntry("corrupt.xml", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_TruncatedDeflateData_IsNotZip()
    {
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 400).Select(index => $"line {index} of text\n")));

        byte[] deflated = ComprehensionPackageBuilder.Deflate(data);

        RawZipEntry entry = RawZipEntry.Deflated("cut.xml", data);

        entry.DataAreaBytes = deflated[..(deflated.Length / 2)];

        BoundedZipPackage package = Open(RawZipPackage.Of(entry).ToBytes());

        Rejected(DocumentPackageRejection.NotZip, () => package.ReadEntry("cut.xml", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_CancellationDuringInflationOfALargeLegitimateEntry_IsObserved()
    {
        BoundedZipPackage package = Open(LargePackage.Value);

        using CancellationTokenSource source = new();

        source.CancelAfter(TimeSpan.FromMilliseconds(1));

        Assert.Throws<OperationCanceledException>(() => package.ReadEntry("big.bin", source.Token));
    }

    [Fact]
    public void ReadEntry_CancelledTokenBeforeStart_ThrowsOperationCanceled()
    {
        BoundedZipPackage package = Open(Baseline().ToBytes());

        Assert.Throws<OperationCanceledException>(() => package.ReadEntry("word/document.xml", new CancellationToken(canceled: true)));
    }

    // ---------------------------------------------------------------- robustness

    [Fact]
    public void Open_RandomlyCorruptedPackages_OnlyEverSucceedOrThrowAClosedRejection()
    {
        // Seeded corruption of a valid package: whatever the bytes become, the only permitted outcomes are a package
        // or a DocumentPackageRejectedException; any other exception type would reach the host as an opaque failure.
        string[] names = ["word/document.xml", "word/styles.xml", "docs/", "plain.txt"];

        byte[] baseline = RawZipPackage.Of(
            RawZipEntry.Deflated("word/document.xml", string.Concat(Enumerable.Repeat("<w:p>some text</w:p>", 40))),
            RawZipEntry.Deflated("word/styles.xml", "<w:styles/>"),
            new RawZipEntry(Encoding.UTF8.GetBytes("docs/"), [], 0),
            RawZipEntry.Stored("plain.txt", "plain stored text")).ToBytes();

        Random random = new(20261022);

        for (int iteration = 0; iteration < FuzzIterations; iteration++)
        {
            byte[] mutated = (byte[])baseline.Clone();

            int flips = 1 + random.Next(4);

            for (int flip = 0; flip < flips; flip++)
            {
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            }

            ReadOnlyMemory<byte> input = random.Next(8) == 0 ? mutated.AsMemory(0, random.Next(mutated.Length)) : mutated;

            try
            {
                BoundedZipPackage package = Open(input);

                foreach (string name in names)
                {
                    if (package.Contains(name))
                    {
                        package.ReadEntry(name, CancellationToken.None);
                    }
                }
            }
            catch (DocumentPackageRejectedException)
            {
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private const int PowerOfTwoBoundary = 65536;

    private const int FuzzIterations = 4000;

    private static BoundedZipPackage Open(ReadOnlyMemory<byte> bytes) => BoundedZipPackage.Open(bytes, CancellationToken.None);

    private static string Read(BoundedZipPackage package, string name) =>
        Encoding.UTF8.GetString(package.ReadEntry(name, CancellationToken.None).Span);

    private static RawZipPackage Baseline() => RawZipPackage.Of(
        RawZipEntry.Deflated("word/document.xml", "<w:document/>"),
        RawZipEntry.Deflated("word/styles.xml", "<w:styles/>"));

    private static RawZipPackage ManyEntries(int count)
    {
        RawZipPackage raw = new();

        for (int index = 0; index < count; index++)
        {
            raw.Entries.Add(RawZipEntry.Stored($"e{index}.txt", "x"));
        }

        return raw;
    }

    private static DocumentPackageRejectedException Rejected(DocumentPackageRejection expected, Func<object?> act)
    {
        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(act);

        Assert.Equal(expected, exception.Rejection);

        Assert.Equal(DocumentPackageRejections.Reason(expected), exception.Message);

        Assert.Null(exception.InnerException);

        return exception;
    }

    private static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        action();

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static long CentralDirectoryOffset(byte[] bytes) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 22 + 16));

    private static long CentralDirectorySize(byte[] bytes) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 22 + 12));

    private sealed class CopyingMemoryManager(byte[] bytes) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan() => bytes;

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
