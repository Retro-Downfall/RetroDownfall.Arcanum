using System.Text.Json;
using System.Text.Json.Serialization;

using A2A;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// Loop prevention for A2A delegation, carried in A2A message metadata under
/// <see cref="MetadataKey"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each Arcanum instance appends its own opaque <see cref="NodeId"/> when it dispatches a Sending. An
/// instance that receives a Sending whose chain already contains its own node id knows the work has
/// looped back to it and refuses, breaking the cycle at the first repeat.
/// </para>
/// <para>
/// This is cycle detection, <em>not</em> a hop ceiling. Issue #55 forbids arbitrary fixed limits, and a
/// chain that never revisits a node is legitimate delegation however long it runs — only a repeat is
/// evidence of a loop. The identifier is a per-process GUID: it is opaque (it names no host, path, or
/// credential), it needs no persistence, and a cycle that matters necessarily closes within the lifetime
/// of the process that started it.
/// </para>
/// <para>
/// A remote controls the inbound value completely, so <see cref="Read"/> never throws and treats any
/// malformed shape as "no chain known" — the worst a hostile peer can do is decline the protection it
/// would itself benefit from.
/// </para>
/// </remarks>
public static class ConclaveDelegationChain
{

    /// <summary>A2A message-metadata key carrying the delegation chain (a JSON array of node ids).</summary>
    public const string MetadataKey = "arcanum.conclave.delegationChain";

    /// <summary>
    /// Opaque identifier for this Arcanum process within a delegation chain. Stable for the process
    /// lifetime and meaningless outside it.
    /// </summary>
    public static string NodeId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Returns <paramref name="chain"/> with this node appended, unless it is already the last hop.</summary>
    public static string[] Extend(IReadOnlyList<string> chain)
    {

        if (chain.Count > 0 && string.Equals(chain[^1], NodeId, StringComparison.Ordinal))
        {

            return [.. chain];

        }

        return [.. chain, NodeId];

    }

    /// <summary>True when this node already appears in <paramref name="chain"/> — the work has looped back.</summary>
    public static bool ContainsSelf(IReadOnlyList<string> chain)
    {

        for (int i = 0; i < chain.Count; i++)
        {

            if (string.Equals(chain[i], NodeId, StringComparison.Ordinal))
            {

                return true;

            }

        }

        return false;

    }

    /// <summary>Stamps <paramref name="chain"/> onto <paramref name="message"/>'s A2A metadata.</summary>
    public static void Write(Message message, IReadOnlyList<string> chain)
    {

        message.Metadata ??= [];

        message.Metadata[MetadataKey] = JsonSerializer.SerializeToElement(
            chain as string[] ?? [.. chain],
            ConclaveDelegationChainJsonContext.Default.StringArray);

    }

    /// <summary>
    /// Reads the delegation chain from inbound A2A metadata. Returns an empty chain for absent, malformed,
    /// or hostile values rather than throwing.
    /// </summary>
    public static string[] Read(IReadOnlyDictionary<string, JsonElement>? metadata)
    {

        if (metadata is null || !metadata.TryGetValue(MetadataKey, out JsonElement raw))
        {

            return [];

        }

        if (raw.ValueKind != JsonValueKind.Array)
        {

            return [];

        }

        List<string> hops = [];

        foreach (JsonElement item in raw.EnumerateArray())
        {

            if (item.ValueKind != JsonValueKind.String)
            {

                continue;

            }

            string? value = item.GetString();

            if (!string.IsNullOrWhiteSpace(value))
            {

                hops.Add(value.Trim());

            }

        }

        return [.. hops];

    }

}

/// <summary>Source-generated contract for the delegation chain so the A2A metadata stays Native AOT-safe.</summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class ConclaveDelegationChainJsonContext : JsonSerializerContext;
