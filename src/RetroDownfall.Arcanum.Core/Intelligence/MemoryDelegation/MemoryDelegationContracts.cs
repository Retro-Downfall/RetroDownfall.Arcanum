using System.Collections.Immutable;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<MemoryDelegationPurpose>))]
public enum MemoryDelegationPurpose
{
    DelegatedTask = 1
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MemoryDelegationGrant(MemoryEvaluationScope Scope, MemoryEvaluationMemory Memory, MemoryDelegationPurpose Purpose = MemoryDelegationPurpose.DelegatedTask)
{
    private readonly MemoryEvaluationMemory _memory = Copy(Memory);

    public MemoryEvaluationMemory Memory
    {
        get => _memory;

        init => _memory = Copy(value);
    }

    private static MemoryEvaluationMemory Copy(MemoryEvaluationMemory memory) => memory is null ? null! : new(
        memory.Covenant.IsDefault ? default : memory.Covenant.ToArray().ToImmutableArray(),
        memory.Saga.IsDefault ? default : memory.Saga.ToArray().ToImmutableArray(),
        memory.Lexicon.IsDefault ? default : memory.Lexicon.ToArray().ToImmutableArray());
}
