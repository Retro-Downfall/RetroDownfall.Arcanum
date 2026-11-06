using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildQuestCheckpointPayload(Guid QuestId, int Version, string Phase);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuildQuestCheckpointPayload))]
internal sealed partial class GuildQuestCheckpointJsonContext : JsonSerializerContext;

/// <summary>Content-free recovery locator. Domain tables retain all execution evidence.</summary>
internal static class GuildQuestCheckpoint
{
    internal const int Version = 1;

    internal static byte[] Encode(Guid questId, string phase = "admitted")
    {
        if (questId == Guid.Empty || !IsPhase(phase))
            throw new ArgumentException("A Quest checkpoint requires an identity and a known recovery phase.");

        return JsonSerializer.SerializeToUtf8Bytes(new GuildQuestCheckpointPayload(questId, Version, phase),
            GuildQuestCheckpointJsonContext.Default.GuildQuestCheckpointPayload);
    }

    internal static Result<GuildQuestCheckpointPayload> Decode(int checkpointVersion, byte[]? payload)
    {
        if (checkpointVersion != Version)
            return new Error(LongRunningOperationErrorCodes.UnsupportedCheckpointVersion, "The Quest checkpoint version is unsupported.");

        // A locator has three small scalar fields. This bounds parsing allocation, never a work lifetime.
        if (payload is null || payload.Length is 0 or > 512)
            return Corrupt();

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Corrupt();

            HashSet<string> names = new(StringComparer.Ordinal);

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name is not ("questId" or "version" or "phase") || !names.Add(property.Name))
                    return Corrupt();
            }

            if (names.Count != 3)
                return Corrupt();

            GuildQuestCheckpointPayload? checkpoint = JsonSerializer.Deserialize(payload,
                GuildQuestCheckpointJsonContext.Default.GuildQuestCheckpointPayload);

            return checkpoint is not null && checkpoint.QuestId != Guid.Empty && checkpoint.Version == Version && IsPhase(checkpoint.Phase)
                ? checkpoint : Corrupt();
        }
        catch (JsonException)
        {
            return Corrupt();
        }
    }

    private static bool IsPhase(string phase) => phase is "admitted" or "execution" or "delivery" or "awaitingInput" or "cancellation" or "reconciliation";

    private static Error Corrupt() => new(LongRunningOperationErrorCodes.CorruptCheckpoint, "The Quest checkpoint is not a valid content-free locator.");
}
