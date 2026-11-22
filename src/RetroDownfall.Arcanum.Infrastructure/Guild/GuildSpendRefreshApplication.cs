using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildSpendRefreshCheckpoint(int Version, Guid RefreshId, Guid QuestId, Guid RootQuestId,
    Guid EffectId, Guid SourceId, Guid OperationId, Guid InstallationGeneration, Guid PrincipalId,
    Guid SignetEpoch, Guid SpendEpoch, Guid SpendKeyId, string Phase);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuildSpendRefreshCheckpoint))]
internal sealed partial class GuildSpendRefreshCheckpointJsonContext : JsonSerializerContext;

internal sealed partial class GuildQuestStore
{
    internal static byte[] RefreshRequestDigest(Guid installation, Guid principal, Guid quest, Guid effect, Guid refresh) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", "Arcanum.Guild.RefreshRequest.v1", Id(installation), Id(principal),
            Id(quest), Id(effect), Id(refresh))));
}
