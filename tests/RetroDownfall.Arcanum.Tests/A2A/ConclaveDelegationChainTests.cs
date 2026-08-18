using System.Text.Json;

using A2A;

using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>
/// Loop prevention for A2A delegation. Each hop appends its own opaque node id to a chain carried in
/// A2A message metadata; a node that sees itself already in the chain refuses the work.
/// </summary>
/// <remarks>
/// This is deliberately cycle detection rather than a fixed depth ceiling: issue #55 forbids arbitrary
/// hop counts, and a chain that never revisits a node is legitimate work no matter how long it is.
/// </remarks>
public sealed class ConclaveDelegationChainTests
{

    [Fact]
    public void NodeId_IsStableWithinTheProcess()
    {

        Assert.Equal(ConclaveDelegationChain.NodeId, ConclaveDelegationChain.NodeId);

        Assert.False(string.IsNullOrWhiteSpace(ConclaveDelegationChain.NodeId));

    }

    [Fact]
    public void Extend_AppendsThisNodeToAnEmptyChain()
    {

        string[] extended = ConclaveDelegationChain.Extend([]);

        Assert.Equal([ConclaveDelegationChain.NodeId], extended);

    }

    [Fact]
    public void Extend_PreservesUpstreamHopsInOrder()
    {

        string[] extended = ConclaveDelegationChain.Extend(["node-a", "node-b"]);

        Assert.Equal(["node-a", "node-b", ConclaveDelegationChain.NodeId], extended);

    }

    [Fact]
    public void Extend_DoesNotDuplicateThisNodeWhenItIsAlreadyLast()
    {

        string[] once = ConclaveDelegationChain.Extend([ConclaveDelegationChain.NodeId]);

        Assert.Equal([ConclaveDelegationChain.NodeId], once);

    }

    [Fact]
    public void ContainsSelf_IsTrueOnlyWhenThisNodeAlreadyHandledTheChain()
    {

        Assert.False(ConclaveDelegationChain.ContainsSelf([]));

        Assert.False(ConclaveDelegationChain.ContainsSelf(["node-a", "node-b"]));

        Assert.True(ConclaveDelegationChain.ContainsSelf(["node-a", ConclaveDelegationChain.NodeId, "node-b"]));

    }

    [Fact]
    public void WriteAndRead_RoundTripThroughA2AMessageMetadata()
    {

        Message message = new()
        {
            Role = Role.User,
            MessageId = "m1",
            Parts = [Part.FromText("goal")],
        };

        ConclaveDelegationChain.Write(message, ["node-a", "node-b"]);

        Assert.NotNull(message.Metadata);

        Assert.True(message.Metadata!.ContainsKey(ConclaveDelegationChain.MetadataKey));

        Assert.Equal(["node-a", "node-b"], ConclaveDelegationChain.Read(message.Metadata));

    }

    [Fact]
    public void Read_TreatsAbsentMetadataAsAnEmptyChain()
    {

        Assert.Empty(ConclaveDelegationChain.Read(null));

        Assert.Empty(ConclaveDelegationChain.Read(new Dictionary<string, JsonElement>()));

    }

    [Theory]
    // A remote controls this value entirely, so every malformed shape must degrade to "no chain known"
    // rather than throwing inside an inbound request handler.
    [InlineData("\"not-an-array\"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    public void Read_IgnoresHostileOrMalformedMetadata(string rawJson)
    {

        Dictionary<string, JsonElement> metadata = new()
        {
            [ConclaveDelegationChain.MetadataKey] = JsonDocument.Parse(rawJson).RootElement.Clone(),
        };

        Assert.Empty(ConclaveDelegationChain.Read(metadata));

    }

    [Fact]
    public void Chain_SurvivesTheApprenticeCheckpointRoundTrip()
    {

        // DESIGN §5.4.4 lists the chain as durable on Apprentices.CheckpointData. That claim only holds if
        // the source-generated checkpoint contract actually carries it — no schema change, no new column.
        ApprenticeCheckpoint checkpoint = new()
        {
            CurrentStep = 0,
            Timestamp = DateTimeOffset.UnixEpoch,
            ParentApprenticeId = Guid.Empty,
            DelegationChain = ["node-a", ConclaveDelegationChain.NodeId],
        };

        string json = ApprenticeRepository.SerializeCheckpoint(checkpoint);

        ApprenticeCheckpoint? restored = ApprenticeRepository.DeserializeCheckpoint(json);

        Assert.NotNull(restored);

        Assert.Equal(["node-a", ConclaveDelegationChain.NodeId], restored.DelegationChain);

    }

    [Fact]
    public void Read_DropsBlankEntriesAndBoundsTheChainItReadsBack()
    {

        Dictionary<string, JsonElement> metadata = new()
        {
            [ConclaveDelegationChain.MetadataKey] =
                JsonDocument.Parse("""["node-a", "", "   ", "node-b"]""").RootElement.Clone(),
        };

        Assert.Equal(["node-a", "node-b"], ConclaveDelegationChain.Read(metadata));

    }

    [Fact]
    public void Read_DoesNotMaterializeMoreHopsThanTheParseCeilingAllows()
    {

        // A hop is one opaque node id, and the chain is persisted onto the Apprentice checkpoint and
        // re-emitted onto every onward Sending. Under the 10 MiB inbound body ceiling a peer can supply
        // hundreds of thousands of them, so what the parse refuses is the materialization, not the
        // delegation depth: no legitimate chain — one real Arcanum instance per hop — comes near this.
        string[] flood = [.. Enumerable.Range(0, (ConclaveDelegationChain.MaxParsedHops * 2) + 7).Select(static index => $"node-{index}")];

        Assert.Equal(
            ConclaveDelegationChain.MaxParsedHops,
            ConclaveDelegationChain.Read(MetadataFor(flood)).Length);

    }

    [Fact]
    public void Read_SkipsAnEntryNoNodeIdCouldBe()
    {

        string oversized = new('a', ConclaveDelegationChain.MaxHopLength + 1);

        Assert.Equal(
            ["node-a", "node-b"],
            ConclaveDelegationChain.Read(MetadataFor(["node-a", oversized, "node-b"])));

    }

    [Fact]
    public void Read_StillProvesTheLoopWhenThisNodeSitsBeyondTheParseCeiling()
    {

        // Truncation must never turn a refusal into an accepted cycle: the whole point of the chain is
        // that a Sending which has already passed through this instance is refused, and a hostile peer
        // that pads the chain past the ceiling would otherwise buy itself exactly that.
        string[] padded =
        [
            .. Enumerable.Range(0, ConclaveDelegationChain.MaxParsedHops + 50).Select(static index => $"node-{index}"),
            ConclaveDelegationChain.NodeId,
        ];

        string[] read = ConclaveDelegationChain.Read(MetadataFor(padded));

        Assert.True(read.Length <= ConclaveDelegationChain.MaxParsedHops + 1);

        Assert.True(ConclaveDelegationChain.ContainsSelf(read));

    }

    private static Dictionary<string, JsonElement> MetadataFor(string[] chain)
    {

        Message message = new()
        {
            Role = Role.User,
            MessageId = "m1",
            Parts = [Part.FromText("goal")],
        };

        ConclaveDelegationChain.Write(message, chain);

        return new Dictionary<string, JsonElement>(message.Metadata!);

    }

}
