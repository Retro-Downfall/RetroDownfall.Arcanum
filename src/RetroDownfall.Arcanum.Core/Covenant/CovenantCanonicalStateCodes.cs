using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// Where the FTS5 inspection index stands relative to canonical truth.
/// </summary>
/// <remarks>
/// The Covenant design names <c>FullRebuildRequired</c> and <c>Rebuilding</c> without assigning
/// numbers and without naming the settled state. These codes are that assignment, pinned here rather
/// than left to each caller so the persisted <c>covenant_state</c> discriminant has exactly one
/// owner. As with every other Covenant code table, no member uses zero: a zero would be
/// indistinguishable from an unset integer column.
///
/// <list type="bullet">
/// <item><see cref="Idle"/> — no rebuild is owed. The accelerator is either synchronized or behind
/// by deltas the outbox still carries, which ordinary synchronization drains.</item>
/// <item><see cref="FullRebuildRequired"/> — the outbox hit its cap or the dataset generation
/// changed, so the surviving deltas cannot reconstruct the projection and only a full rebuild
/// can.</item>
/// <item><see cref="Rebuilding"/> — a rebuild captured its target tuple and is in progress. Fresh
/// mutations resume bounded outbox writes; if those deltas reach the cap again, the state returns to
/// <see cref="FullRebuildRequired"/> and the current rebuild becomes stale.</item>
/// </list>
/// </remarks>
[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<CovenantFtsRebuildState>))]
public enum CovenantFtsRebuildState : byte
{

    Idle = 1,

    FullRebuildRequired = 2,

    Rebuilding = 3,

}
