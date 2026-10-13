using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(LongRestSweepCheckpoint))]
internal sealed partial class LongRestSweepCheckpointJsonContext : JsonSerializerContext;

internal static class LongRestSweepCheckpointCodec
{
    // One bounded UTF-8 cursor can expand to six JSON bytes per input byte; all remaining
    // fields are closed enums, GUIDs, two 32-byte digests and fixed-width numeric coordinates.
    internal const int MaxCheckpointBytes = 6 * LongRestPolicy.MaxIdentityBytes + 2048;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static Result<byte[]> Encode(LongRestSweepCheckpoint checkpoint)
    {
        try
        {
            if (!Valid(checkpoint.OperationId, checkpoint)) return Invalid();

            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, LongRestSweepCheckpointJsonContext.Default.LongRestSweepCheckpoint);

            return bytes.Length <= MaxCheckpointBytes ? bytes : Invalid();
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            return Invalid();
        }
    }

    internal static Result<LongRestSweepCheckpoint> Decode(Guid operationId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length is 0 or > MaxCheckpointBytes) return Invalid();

        try
        {
            string json = StrictUtf8.GetString(payload);

            LongRestSweepCheckpoint? checkpoint = JsonSerializer.Deserialize(json, LongRestSweepCheckpointJsonContext.Default.LongRestSweepCheckpoint);

            return checkpoint is not null && Valid(operationId, checkpoint) ? checkpoint : Invalid();
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException)
        {
            return Invalid();
        }
    }

    // Retired is historical metadata, never a resumable checkpoint phase.
    private static bool Valid(Guid operationId, LongRestSweepCheckpoint checkpoint) =>
        operationId != Guid.Empty && checkpoint.Version == 1 && checkpoint.OperationId == operationId
        && LongRestSweepScope.Create(new(checkpoint.Scope, checkpoint.CampaignId), checkpoint.ProtectedPartitionDigest).IsSuccess
        && checkpoint.Revision >= 0 && checkpoint.CommittedWorkDigest is not { IsValid: false }
        && (checkpoint.CommittedItemCursor is null || !string.IsNullOrWhiteSpace(checkpoint.CommittedItemCursor)
            && StrictUtf8.GetByteCount(checkpoint.CommittedItemCursor) <= LongRestPolicy.MaxIdentityBytes)
        && (checkpoint.GenerationId is null
            ? checkpoint.Phase is null && checkpoint.Revision == 0 && checkpoint.CommittedItemCursor is null && checkpoint.CommittedWorkDigest is null
            : checkpoint.GenerationId != Guid.Empty && checkpoint.Phase is >= LongRestSweepPhase.Capture and <= LongRestSweepPhase.Deferred);

    private static Error Invalid() => new("long_rest.checkpoint_invalid", "The Long Rest checkpoint cannot be resumed.");
}
