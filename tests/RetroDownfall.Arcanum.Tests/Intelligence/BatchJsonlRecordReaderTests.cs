using System.Text;

using RetroDownfall.Arcanum.Api.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class BatchJsonlRecordReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cursor_LargeWhitespace_UsesBoundedRawReadAhead(bool hasRecord)
    {
        string content = new string(' ', BatchJsonlRecordReader.InMemoryByteLimit * 3)
            + (hasRecord ? "\n{}\n{}" : "\n\t");

        using NonSeekableSource source = new(Encoding.UTF8.GetBytes(content));

        List<string> spills = [];

        using BatchJsonlRecordReader.Cursor reader = new(
            source,
            observer: new CallbackObserver(spills.Add));

        Assert.Equal(hasRecord, await reader.HasNextRecordAsync(CancellationToken.None));

        Assert.InRange(source.ReadCalls, 1, 14);

        Assert.InRange(source.MaxRequestedBytes, 1, 64 * 1024);

        Assert.Empty(spills);

        int reads = source.ReadCalls;

        Assert.Equal(hasRecord, await reader.HasNextRecordAsync(CancellationToken.None));

        Assert.Equal(reads, source.ReadCalls);

        if (hasRecord)
        {
            Assert.Equal(2, (await reader.ReadRecordAsync(CancellationToken.None)).PhysicalLine);

            Assert.True(await reader.HasNextRecordAsync(CancellationToken.None));

            Assert.Equal(3, (await reader.ReadRecordAsync(CancellationToken.None)).PhysicalLine);

            Assert.False(await reader.HasNextRecordAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Cursor_CancellationDuringSpill_CleansBeforeReturningToPageOwner()
    {
        using CancellationTokenSource stopping = new();

        using NonSeekableSource source = new(Encoding.UTF8.GetBytes(new string('x', BatchJsonlRecordReader.InMemoryByteLimit + 1)));

        string? spill = null;

        using BatchJsonlRecordReader.Cursor reader = new(
            source,
            observer: new CallbackObserver(path =>
            {
                spill = path;

                stopping.Cancel();
            }));

        Assert.True(await reader.HasNextRecordAsync(stopping.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.ReadRecordAsync(stopping.Token));

        Assert.NotNull(spill);

        Assert.False(File.Exists(spill));
    }

    [Fact]
    public async Task Cursor_Lookahead_StagesOnlyFirstByteWithoutParsingOrSpilling()
    {
        using NonSeekableSource source = new(Encoding.UTF8.GetBytes(
            " \t\r\n  " + new string('x', BatchJsonlRecordReader.InMemoryByteLimit + 1)));

        List<string> spills = [];

        using BatchJsonlRecordReader.Cursor reader = new(
            source,
            observer: new CallbackObserver(spills.Add));

        Assert.True(await reader.HasNextRecordAsync(CancellationToken.None));

        Assert.InRange(source.BytesRead, 7, 64 * 1024);

        int prefetchedBytes = source.BytesRead;

        Assert.True(await reader.HasNextRecordAsync(CancellationToken.None));

        Assert.Equal(prefetchedBytes, source.BytesRead);

        Assert.Empty(spills);

        BatchJsonlRecordReadResult record = await reader.ReadRecordAsync(CancellationToken.None);

        Assert.Equal(2, record.PhysicalLine);

        Assert.Contains("protocol", record.Error, StringComparison.OrdinalIgnoreCase);

        Assert.Single(spills);

        Assert.False(File.Exists(spills[0]));

        Assert.False(await reader.HasNextRecordAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n\n")]
    [InlineData("large")]
    public async Task Cursor_WhitespaceOnly_DoesNotSpillOrInventRecord(string input)
    {
        string content = input == "large"
            ? new string(' ', BatchJsonlRecordReader.InMemoryByteLimit * 3) + "\n\t"
            : input;

        using NonSeekableSource source = new(Encoding.UTF8.GetBytes(content));

        List<string> spills = [];

        using BatchJsonlRecordReader.Cursor reader = new(
            source,
            observer: new CallbackObserver(spills.Add));

        Assert.False(await reader.HasNextRecordAsync(CancellationToken.None));

        Assert.False(await reader.HasNextRecordAsync(CancellationToken.None));

        Assert.Empty(spills);
    }

    [Fact]
    public async Task Cursor_LeadingWhitespace_PreservesPhysicalLimitAndNextLineIdentity()
    {
        using NonSeekableSource source = new(Encoding.UTF8.GetBytes("\n" + new string(' ', 513) + "{}\r\n\n{}"));

        using BatchJsonlRecordReader.Cursor reader = new(source, maxRecordBytes: 512);

        Assert.True(await reader.HasNextRecordAsync(CancellationToken.None));

        BatchJsonlRecordReadResult oversized = await reader.ReadRecordAsync(CancellationToken.None);

        Assert.Equal(2, oversized.PhysicalLine);

        Assert.Contains("measured 516 UTF-8 bytes", oversized.Error, StringComparison.Ordinal);

        Assert.True(await reader.HasNextRecordAsync(CancellationToken.None));

        BatchJsonlRecordReadResult next = await reader.ReadRecordAsync(CancellationToken.None);

        Assert.Equal(4, next.PhysicalLine);

        Assert.Null(next.Error);

        Assert.False(await reader.HasNextRecordAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cursor_BomAfterLeadingWhitespace_RemainsInvalid(bool large)
    {
        byte[] input = [32, .. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(
            large ? "{\"padding\":\"" + new string('x', BatchJsonlRecordReader.InMemoryByteLimit) + "\"}" : "{}")];

        using NonSeekableSource source = new(input);

        using BatchJsonlRecordReader.Cursor reader = new(source);

        Assert.True(await reader.HasNextRecordAsync(CancellationToken.None));

        Assert.NotNull((await reader.ReadRecordAsync(CancellationToken.None)).Error);
    }

    [Fact]
    public async Task ReadAsync_LargeWhitespaceOnly_DoesNotCreateSpill()
    {
        using NonSeekableSource source = new(Encoding.UTF8.GetBytes(new string(' ', BatchJsonlRecordReader.InMemoryByteLimit * 2)));

        List<string> spills = [];

        await foreach (BatchJsonlRecordReadResult record in BatchJsonlRecordReader.ReadAsync(
                           source,
                           null,
                           new CallbackObserver(spills.Add),
                           CancellationToken.None))
        {
            Assert.Fail($"Unexpected whitespace record at {record.PhysicalLine}.");
        }

        Assert.Empty(spills);
    }

    private sealed class NonSeekableSource(byte[] input) : MemoryStream(input)
    {
        internal int BytesRead { get; private set; }

        internal int ReadCalls { get; private set; }

        internal int MaxRequestedBytes { get; private set; }

        public override bool CanSeek => false;

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;

            MaxRequestedBytes = Math.Max(MaxRequestedBytes, buffer.Length);

            int count = await base.ReadAsync(buffer, cancellationToken);

            BytesRead += count;

            return count;
        }
    }

    [Fact]

    public async Task ReadAsync_GiantMalformedRecord_SpillsWithoutMaterializingLineAndContinues()

    {
        string giantMalformedRecord = new('x', BatchJsonlRecordReader.InMemoryByteLimit + 1);

        string validRecord =

            """{"custom_id":"next","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}""";

        byte[] input = Encoding.UTF8.GetBytes(giantMalformedRecord + "\n" + validRecord + "\n");

        List<string> spillPaths = [];

        List<BatchJsonlRecordReadResult> records = [];

        await foreach (BatchJsonlRecordReadResult record in BatchJsonlRecordReader.ReadAsync(
                           new MemoryStream(input),

                           temporaryDirectory: null,

                           new CallbackObserver(spillPaths.Add),

                           CancellationToken.None))

        {
            records.Add(record);
        }

        Assert.Equal(2, records.Count);

        Assert.Equal(1, records[0].PhysicalLine);

        Assert.Null(records[0].Request);

        Assert.Contains("protocol", records[0].Error, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(2, records[1].PhysicalLine);

        Assert.Equal("next", records[1].Request!.CustomId);

        Assert.Null(records[1].Error);

        Assert.Single(spillPaths);

        Assert.False(File.Exists(spillPaths[0]));
    }

    [Fact]

    public async Task ReadAsync_SpillUnavailable_ReturnsPerRecordPhysicalErrorAndContinues()

    {
        string missingDirectory = Path.Combine(
            Path.GetTempPath(),

            $"arcanum-missing-batch-spill-{Guid.NewGuid():N}",

            "missing");

        string giantMalformedRecord = new('x', BatchJsonlRecordReader.InMemoryByteLimit + 1);

        string validRecord =

            """{"custom_id":"next","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}""";

        byte[] input = Encoding.UTF8.GetBytes(giantMalformedRecord + "\n" + validRecord + "\n");

        List<BatchJsonlRecordReadResult> records = [];

        await foreach (BatchJsonlRecordReadResult record in BatchJsonlRecordReader.ReadAsync(
                           new MemoryStream(input),

                           missingDirectory,

                           observer: null,

                           CancellationToken.None))

        {
            records.Add(record);
        }

        Assert.Equal(2, records.Count);

        Assert.Null(records[0].Request);

        Assert.Contains("physical resource protection", records[0].Error, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("physical line 1", records[0].Error, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("continue", records[0].Error, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("next", records[1].Request!.CustomId);
    }

    [Fact]

    public async Task ReadAsync_Utf8BomPrefixedFirstRecord_ParsesOnInMemoryAndSpilledPathsAlike()
    {
        string smallRecord =
            """{"custom_id":"in-memory","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}""";

        string spilledRecord =
            "{\"custom_id\":\"spilled\",\"method\":\"POST\",\"url\":\"/v1/chat/completions\",\"body\":{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\""
            + new string('y', BatchJsonlRecordReader.InMemoryByteLimit + 1)
            + "\"}]}}";

        Assert.Equal(
            "in-memory",
            (await ReadSingleAsync(Encoding.UTF8.Preamble.ToArray(), smallRecord)).Request!.CustomId);

        Assert.Equal(
            "spilled",
            (await ReadSingleAsync(Encoding.UTF8.Preamble.ToArray(), spilledRecord)).Request!.CustomId);
    }

    private static async Task<BatchJsonlRecordReadResult> ReadSingleAsync(
        byte[] preamble,
        string record)
    {
        byte[] input = [.. preamble, .. Encoding.UTF8.GetBytes(record + "\n")];

        List<BatchJsonlRecordReadResult> records = [];

        await foreach (BatchJsonlRecordReadResult read in BatchJsonlRecordReader.ReadAsync(
                           new MemoryStream(input),
                           temporaryDirectory: null,
                           observer: null,
                           CancellationToken.None))
        {
            records.Add(read);
        }

        BatchJsonlRecordReadResult single = Assert.Single(records);

        Assert.Null(single.Error);

        return single;
    }

    [Fact]

    public async Task ReadAsync_RecordAboveMaterializationBoundary_ReportsMeasurementAndContinues()

    {
        const int testRecordLimit = 512;

        string oversizedRecord = new('x', testRecordLimit + 1);

        string validRecord =

            """{"custom_id":"next","method":"POST","url":"/v1/chat/completions","body":{"model":"m","messages":[{"role":"user","content":"hi"}]}}""";

        byte[] input = Encoding.UTF8.GetBytes(oversizedRecord + "\n" + validRecord + "\n");

        List<BatchJsonlRecordReadResult> records = [];

        await foreach (BatchJsonlRecordReadResult record in BatchJsonlRecordReader.ReadAsync(
                           new MemoryStream(input),

                           temporaryDirectory: null,

                           observer: null,

                           CancellationToken.None,

                           maxRecordBytes: testRecordLimit))

        {
            records.Add(record);
        }

        Assert.Equal(2, records.Count);

        Assert.Contains("measured 513 UTF-8 bytes", records[0].Error, StringComparison.Ordinal);

        Assert.Contains("limit is 512 bytes", records[0].Error, StringComparison.Ordinal);

        Assert.Contains("not allocated or sent", records[0].Error, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("next", records[1].Request!.CustomId);
    }

    private sealed class CallbackObserver(Action<string> callback) : IBatchJsonlRecordObserver
    {
        public void SpillCreated(string path) => callback(path);
    }
}
