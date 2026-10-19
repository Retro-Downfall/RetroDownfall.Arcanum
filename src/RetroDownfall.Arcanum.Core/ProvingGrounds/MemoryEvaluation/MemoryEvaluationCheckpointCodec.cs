using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

/// <summary>Strict metadata-only checkpoint grammar. Ledger version zero has no serialized payload.</summary>
public static class MemoryEvaluationCheckpointCodec
{
    private static readonly GrimoireJsonContext Context = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = MemoryEvaluationLimits.SchemaDepth
    });

    public static byte[] Encode(MemoryEvaluationCheckpoint checkpoint)
    {
        if (!Valid(checkpoint)) throw new InvalidDataException("Invalid memory evaluation checkpoint.");

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, Context.MemoryEvaluationCheckpoint);

        if (bytes.Length > MemoryEvaluationLimits.EvidenceBytes) throw new InvalidDataException("Oversized memory evaluation checkpoint.");

        return bytes;
    }

    public static MemoryEvaluationCheckpoint? Decode(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > MemoryEvaluationLimits.EvidenceBytes) return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MemoryEvaluationLimits.SchemaDepth });
            if (!UniqueMembers(document.RootElement)) return null;

            MemoryEvaluationCheckpoint? checkpoint = JsonSerializer.Deserialize(bytes, Context.MemoryEvaluationCheckpoint);

            return Valid(checkpoint) ? checkpoint : null;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { return null; }
    }

    private static bool UniqueMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);

            foreach (JsonProperty property in element.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueMembers(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                if (!UniqueMembers(item)) return false;
        }

        return true;
    }

    public static bool Valid(MemoryEvaluationCheckpoint? checkpoint)
    {
        if (checkpoint is null || checkpoint.EvaluationId == Guid.Empty || checkpoint.OperationId == Guid.Empty
            || !checkpoint.RequestDigest.IsValid || !checkpoint.PlanDigest.IsValid || !Enum.IsDefined(checkpoint.Phase)
            || checkpoint.PairCount is < MemoryEvaluationLimits.MinPairs or > MemoryEvaluationLimits.MaxPairs
            || checkpoint.PhysicalSends < 0 || checkpoint.PhysicalSends > checkpoint.PairCount * 2
            || checkpoint.InputTokens < 0 || checkpoint.OutputTokens < 0 || checkpoint.Cost < 0 || checkpoint.ElapsedMilliseconds < 0
            || checkpoint.Aggregate is not { } aggregate || !aggregate.ResponseEvidenceDigest.IsValid
            || !Enum.IsDefined(aggregate.FailureReason)) return false;

        int pairs = checkpoint.PairCount;

        if (new[] { aggregate.CompletedPairs, aggregate.ValidPairs, aggregate.InvalidPairs, aggregate.ImprovedPairs, aggregate.RegressedPairs, aggregate.EquivalentPairs, aggregate.MixedPairs }.Any(count => count < 0 || count > pairs)
            || (long)aggregate.ValidPairs + aggregate.InvalidPairs != aggregate.CompletedPairs
            || (long)aggregate.ImprovedPairs + aggregate.RegressedPairs + aggregate.EquivalentPairs + aggregate.MixedPairs != aggregate.ValidPairs
            || aggregate.CompletedPairs * 2 > checkpoint.PhysicalSends || aggregate.Checks.Length > MemoryEvaluationLimits.MaxChecks
            || aggregate.ValidPairs > 0 && aggregate.Checks.IsEmpty) return false;

        for (int index = 0; index < aggregate.Checks.Length; index++)
        {
            MemoryEvaluationCheckAggregate check = aggregate.Checks[index];

            if (check is null || check.CheckIndex != index || !Enum.IsDefined(check.Kind) || !check.CheckPolicyDigest.IsValid
                || check.FailFail < 0 || check.FailPass < 0 || check.PassFail < 0 || check.PassPass < 0
                || (long)check.FailFail + check.FailPass + check.PassFail + check.PassPass != aggregate.ValidPairs
                || check.FailPass > aggregate.ImprovedPairs + aggregate.MixedPairs || check.PassFail > aggregate.RegressedPairs + aggregate.MixedPairs) return false;
        }

        return aggregate.Checks.Sum(check => check.FailPass) >= aggregate.ImprovedPairs + aggregate.MixedPairs
            && aggregate.Checks.Sum(check => check.PassFail) >= aggregate.RegressedPairs + aggregate.MixedPairs;
    }
}
