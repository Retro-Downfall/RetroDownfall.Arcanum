using System.Text.Json;
using System.Text.Json.Serialization;

using A2A;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>Which side of the Conclave door a Sending frame describes.</summary>
public enum A2ASendingDirection
{

    /// <summary>This instance dispatched work to a remote agent (<c>dispatch_sending</c>).</summary>
    Outbound = 0,

    /// <summary>A remote agent dispatched work to this instance (the A2A server).</summary>
    Inbound = 1,

}

/// <summary>
/// What a Sending cost on the far side. A2A has no standard usage field, so a peer either reports
/// figures Arcanum recognises or it does not — and "did not report" is <em>not</em> zero.
/// </summary>
/// <param name="IsKnown">
/// <c>true</c> only when the peer actually reported usage. Never inferred, never defaulted.
/// </param>
/// <param name="TotalTokens">Peer-reported total tokens, or <c>null</c> when unknown.</param>
/// <param name="CostUsd">Peer-reported cost in USD, or <c>null</c> when unknown.</param>
/// <remarks>
/// Recording an unreported remote Sending as zero would quietly understate what a delegated operation
/// cost and make root/child/external accounting look complete when it is not (issue #60). The unknown
/// case is therefore a first-class value that every surface renders as "unknown", not as "0".
/// </remarks>
public sealed record A2ARemoteCost(bool IsKnown, long? TotalTokens, decimal? CostUsd)
{

    /// <summary>The peer reported nothing Arcanum could read.</summary>
    public static readonly A2ARemoteCost Unknown = new(false, null, null);

    /// <summary>
    /// A reported outcome. Returns <see cref="Unknown"/> when neither figure is present, so a peer that
    /// supplies an empty usage object is still recorded as unknown rather than as a free Sending.
    /// </summary>
    public static A2ARemoteCost Known(long? totalTokens, decimal? costUsd) =>
        totalTokens is null && costUsd is null
            ? Unknown
            : new A2ARemoteCost(true, totalTokens, costUsd);

    /// <summary>Operator-facing one-liner used by the CLI, Chronicle frames, and logs.</summary>
    public string Describe()
    {

        if (!IsKnown)
        {

            return "unknown (the remote agent reported no usage)";

        }

        string tokens = TotalTokens is { } t ? $"{t} tokens" : "tokens unknown";

        string cost = CostUsd is { } c ? $"${c:0.######} USD" : "cost unknown";

        return $"{tokens}, {cost}";

    }

}

/// <summary>
/// A single observable transition of an in-flight Sending, published as a <c>sendingProgress</c>
/// Chronicle frame.
/// </summary>
/// <param name="RemoteState">The remote A2A task state, in its wire spelling (<c>working</c>, …).</param>
/// <remarks>
/// Deliberately narrow: peer identity, direction, remote task state, and a timestamp. The peer's own
/// status <em>text</em> is not carried — it is remote-authored prose that can echo the delegated prompt
/// back, and a Chronicle frame is an operator surface, not a data channel (issue #61). A change in that
/// text still triggers a frame; only the text itself stays out.
/// </remarks>
public sealed record A2ASendingProgress(
    string AgentUrl,
    string? TaskId,
    string RemoteState,
    A2ASendingDirection Direction,
    DateTimeOffset Timestamp);

/// <summary>
/// Reads and writes Arcanum's A2A task-metadata usage block.
/// </summary>
/// <remarks>
/// <para>
/// A2A 1.0 has no usage field, so this is an additive, namespaced metadata key: peers that do not know
/// it are unaffected, and Arcanum-to-Arcanum Sendings get real cost attribution instead of a permanent
/// "unknown". Common unnamespaced spellings are also accepted on read so a peer that already publishes
/// a <c>usage</c> block is understood without asking it to change.
/// </para>
/// <para>
/// Every value here is remote-controlled on the read path, so parsing never throws and any malformed,
/// negative, or non-numeric shape degrades to <see cref="A2ARemoteCost.Unknown"/>.
/// </para>
/// </remarks>
public static class A2ASendingUsageMetadata
{

    /// <summary>Namespaced A2A task-metadata key carrying Arcanum's usage block.</summary>
    public const string MetadataKey = "arcanum.conclave.usage";

    /// <summary>Unnamespaced key some peers already use for the same idea.</summary>
    private const string FallbackMetadataKey = "usage";

    private static readonly string[] TotalTokenNames = ["totalTokens", "total_tokens", "tokens"];

    private static readonly string[] CostNames = ["costUsd", "cost_usd", "cost"];

    /// <summary>Stamps a usage block onto a task's metadata dictionary.</summary>
    public static void Write(IDictionary<string, JsonElement> metadata, long? totalTokens, decimal? costUsd)
    {

        ArgumentNullException.ThrowIfNull(metadata);

        metadata[MetadataKey] = JsonSerializer.SerializeToElement(
            new A2AUsageBlock { TotalTokens = totalTokens, CostUsd = costUsd },
            A2ASendingUsageJsonContext.Default.A2AUsageBlock);

    }

    /// <summary>
    /// Reads the cost a peer reported for <paramref name="task"/>, from the task's own metadata or from
    /// its final status message. Returns <see cref="A2ARemoteCost.Unknown"/> for an absent, empty, or
    /// malformed block.
    /// </summary>
    /// <remarks>
    /// Both locations are checked because the A2A SDK's <c>TaskUpdater</c> lets an agent attach metadata
    /// to the terminal status message but not to the task record itself, so that is where Arcanum's own
    /// server publishes its usage.
    /// </remarks>
    public static A2ARemoteCost Read(AgentTask? task)
    {

        A2ARemoteCost fromTask = Read(task?.Metadata);

        return fromTask.IsKnown ? fromTask : Read(task?.Status.Message?.Metadata);

    }

    /// <summary>Reads the cost a peer reported in an A2A metadata dictionary.</summary>
    public static A2ARemoteCost Read(IDictionary<string, JsonElement>? metadata)
    {

        if (metadata is null)
        {

            return A2ARemoteCost.Unknown;

        }

        if (metadata.TryGetValue(MetadataKey, out JsonElement block) && TryReadBlock(block, out A2ARemoteCost namespaced))
        {

            return namespaced;

        }

        return metadata.TryGetValue(FallbackMetadataKey, out JsonElement fallback)
            && TryReadBlock(fallback, out A2ARemoteCost generic)
                ? generic
                : A2ARemoteCost.Unknown;

    }

    private static bool TryReadBlock(JsonElement block, out A2ARemoteCost cost)
    {

        cost = A2ARemoteCost.Unknown;

        if (block.ValueKind != JsonValueKind.Object)
        {

            return false;

        }

        long? totalTokens = null;

        foreach (string name in TotalTokenNames)
        {

            if (block.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out long parsed)
                && parsed >= 0)
            {

                totalTokens = parsed;

                break;

            }

        }

        decimal? costUsd = null;

        foreach (string name in CostNames)
        {

            if (block.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetDecimal(out decimal parsed)
                && parsed >= 0)
            {

                costUsd = parsed;

                break;

            }

        }

        cost = A2ARemoteCost.Known(totalTokens, costUsd);

        return cost.IsKnown;

    }

}

/// <summary>Wire shape of the <c>arcanum.conclave.usage</c> A2A task-metadata block.</summary>
public sealed record A2AUsageBlock
{

    [JsonPropertyName("totalTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalTokens { get; init; }

    [JsonPropertyName("costUsd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CostUsd { get; init; }

}

/// <summary>Source-generated contract so the usage metadata block stays Native AOT-safe.</summary>
[JsonSerializable(typeof(A2AUsageBlock))]
internal sealed partial class A2ASendingUsageJsonContext : JsonSerializerContext;
