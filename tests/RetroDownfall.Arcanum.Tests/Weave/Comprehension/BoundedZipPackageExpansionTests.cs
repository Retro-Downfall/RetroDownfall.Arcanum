using System.IO.Compression;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension;

/// <summary>
/// The package-level expansion bound and the single-allocation inflate: what one extraction may inflate
/// is bounded by the package it was handed, not only per entry, and a part costs one buffer of its size.
/// </summary>
public sealed class BoundedZipPackageExpansionTests
{
    private const int Mebibyte = 1024 * 1024;

    [Fact]
    public void ReadEntry_RepeatedReadsOfASubFloorEntryInATinyPackage_AreBoundedAtTheOneMebibyteFloor()
    {
        // Half a mebibyte that deflates to about half a kilobyte: no per-entry ratio applies at this size,
        // so only the package-level bound can stop the same part being inflated again and again.
        byte[] data = new byte[Mebibyte / 2];

        Array.Fill(data, (byte)'a');

        BoundedZipPackage package = Open(RawZipPackage.Of(RawZipEntry.Deflated("part.xml", data)).ToBytes());

        Assert.Equal(data.Length, package.ReadEntry("part.xml", CancellationToken.None).Length);

        Assert.Equal(data.Length, package.ReadEntry("part.xml", CancellationToken.None).Length);

        Rejected(DocumentPackageRejection.ExpansionBounds, () => package.ReadEntry("part.xml", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_AggregateAcrossReads_IsBoundedAtTwoHundredTimesThePackageInput()
    {
        byte[] data = new byte[64 * 1024];

        new Random(177).NextBytes(data);

        byte[] bytes = RawZipPackage.Of(RawZipEntry.Stored("part.bin", data)).ToBytes();

        long budget = (long)bytes.Length * BoundedZipPackage.MaximumCompressionRatio;

        int allowed = (int)(budget / data.Length);

        // The package bound, not the 64 MiB aggregate cap or the 1 MiB floor, is what decides this case.
        Assert.True(budget > BoundedZipPackage.CompressionRatioFloorBytes);

        Assert.True((long)(allowed + 1) * data.Length < BoundedZipPackage.MaximumAggregateBytes);

        BoundedZipPackage package = Open(bytes);

        for (int read = 0; read < allowed; read++)
        {
            Assert.Equal(data.Length, package.ReadEntry("part.bin", CancellationToken.None).Length);
        }

        Rejected(DocumentPackageRejection.ExpansionBounds, () => package.ReadEntry("part.bin", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_PackageBound_IsMeasuredOnTheBytesHandedInNotTheirBackingArray()
    {
        byte[] data = new byte[3 * Mebibyte / 4];

        Array.Fill(data, (byte)'a');

        byte[] package = RawZipPackage.Of(RawZipEntry.Deflated("part.xml", data)).ToBytes();

        // The package sits inside a much larger backing array; only its own length may fund inflation.
        byte[] backing = new byte[(16 * Mebibyte) + package.Length];

        package.CopyTo(backing, 16 * Mebibyte);

        BoundedZipPackage opened = Open(new ReadOnlyMemory<byte>(backing, 16 * Mebibyte, package.Length));

        Assert.Equal(data.Length, opened.ReadEntry("part.xml", CancellationToken.None).Length);

        Rejected(DocumentPackageRejection.ExpansionBounds, () => opened.ReadEntry("part.xml", CancellationToken.None));
    }

    [Fact]
    public void ReadEntry_DeflatedEntry_IsInflatedIntoOneBufferOfItsDeclaredSize()
    {
        byte[] data = new byte[8 * Mebibyte];

        new Random(22).NextBytes(data);

        RawZipEntry entry = new(ComprehensionPackageBuilder.Utf8Bytes("part.bin"), data, 8)
        {
            DataAreaBytes = ComprehensionPackageBuilder.Deflate(data, CompressionLevel.Fastest),
        };

        byte[] bytes = RawZipPackage.Of(entry).ToBytes();

        // Warm-up on a separate instance so first-use costs are not billed to the measurement.
        Assert.Equal(data.Length, Open(bytes).ReadEntry("part.bin", CancellationToken.None).Length);

        BoundedZipPackage package = Open(bytes);

        long before = GC.GetAllocatedBytesForCurrentThread();

        ReadOnlyMemory<byte> inflated = package.ReadEntry("part.bin", CancellationToken.None);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(data.AsSpan().SequenceEqual(inflated.Span));

        Assert.True(
            allocated < data.Length + (256 * 1024),
            $"Inflating {data.Length} declared bytes allocated {allocated} bytes; one buffer of the declared size is the bound.");
    }

    private static BoundedZipPackage Open(ReadOnlyMemory<byte> bytes) => BoundedZipPackage.Open(bytes, CancellationToken.None);

    private static void Rejected(DocumentPackageRejection expected, Func<object?> act)
    {
        DocumentPackageRejectedException exception = Assert.Throws<DocumentPackageRejectedException>(act);

        Assert.Equal(expected, exception.Rejection);

        Assert.Equal(DocumentPackageRejections.Reason(expected), exception.Message);
    }
}
