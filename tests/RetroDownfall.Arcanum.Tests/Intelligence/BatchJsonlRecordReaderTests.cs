using System.Text;

using RetroDownfall.Arcanum.Api.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class BatchJsonlRecordReaderTests
{

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

                           spillPaths.Add,

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

                           spillCreated: null,

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
                           spillCreated: null,
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

                           spillCreated: null,

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

}
