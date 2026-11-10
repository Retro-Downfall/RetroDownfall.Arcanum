using System.Text.Json;

using A2A;

using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Tests.A2A;

public sealed class A2ASendingGuildUsageTests
{
    [Fact]
    public void Guild_usage_read_does_not_invoke_a_stored_dictionary_comparer()
    {
        CountingComparer comparer = new();

        Dictionary<string, JsonElement> metadata = new(comparer)
        {
            [A2ASendingUsageMetadata.MetadataKey] = Block(42),
        };

        comparer.Calls = 0;

        AgentTask task = new() { Metadata = metadata };

        Assert.Equal(42, A2ASendingUsageMetadata.ReadGuild(task).TotalTokens);

        Assert.Equal(0, comparer.Calls);

        Assert.Equal(42, A2ASendingUsageMetadata.Read(task).TotalTokens);

        Assert.True(comparer.Calls > 0);
    }

    [Fact]
    public void Guild_usage_read_does_not_invoke_a_derived_dictionary_interface_slot()
    {
        ReimplementedDictionary metadata = new()
        {
            [A2ASendingUsageMetadata.MetadataKey] = Block(43),
        };

        Assert.Equal(43, A2ASendingUsageMetadata.ReadGuild(new AgentTask { Metadata = metadata }).TotalTokens);

        Assert.Equal(0, metadata.InterfaceCalls);

        Assert.False(A2ASendingUsageMetadata.Read(new AgentTask { Metadata = metadata }).IsKnown);

        Assert.Equal(2, metadata.InterfaceCalls);
    }

    [Fact]
    public void Guild_usage_read_keeps_namespaced_precedence_and_status_fallback()
    {
        AgentTask task = new()
        {
            Metadata = new() { ["usage"] = Block(11), [A2ASendingUsageMetadata.MetadataKey] = Block(12) },
            Status = new() { Message = new() { Metadata = new() { ["usage"] = Block(13) } } },
        };

        Assert.Equal(12, A2ASendingUsageMetadata.ReadGuild(task).TotalTokens);

        task.Metadata = new() { ["usage"] = JsonDocument.Parse("{}").RootElement.Clone() };

        Assert.Equal(13, A2ASendingUsageMetadata.ReadGuild(task).TotalTokens);
    }

    [Fact]
    public void Guild_usage_read_uses_ordinal_wire_keys_and_keeps_unknown_cost_truthful()
    {
        AgentTask task = new() { Metadata = new(StringComparer.OrdinalIgnoreCase) { ["USAGE"] = Block(8) } };

        Assert.False(A2ASendingUsageMetadata.ReadGuild(task).IsKnown);

        Assert.Equal(8, A2ASendingUsageMetadata.Read(task).TotalTokens);

        Assert.False(A2ASendingUsageMetadata.ReadGuild(null).IsKnown);
    }

    private static JsonElement Block(long tokens) => JsonDocument.Parse("{\"totalTokens\":" + tokens + "}").RootElement.Clone();

    private sealed class CountingComparer : IEqualityComparer<string>
    {
        internal int Calls;

        public bool Equals(string? left, string? right) { Calls++; return StringComparer.Ordinal.Equals(left, right); }

        public int GetHashCode(string value) { Calls++; return StringComparer.Ordinal.GetHashCode(value); }
    }

    private sealed class ReimplementedDictionary : Dictionary<string, JsonElement>, IDictionary<string, JsonElement>
    {
        internal int InterfaceCalls;

        public new Dictionary<string, JsonElement>.Enumerator GetEnumerator()
        {
            InterfaceCalls++;

            return base.GetEnumerator();
        }

        bool IDictionary<string, JsonElement>.TryGetValue(string key, out JsonElement value)
        {
            InterfaceCalls++;

            value = default;

            return false;
        }
    }
}
