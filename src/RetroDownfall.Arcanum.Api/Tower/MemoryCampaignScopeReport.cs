using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Api.Tower;

/// <summary>
/// Turns a resolved <see cref="MemoryScope"/> into the block <c>memory status</c>,
/// <c>memory search</c>, and <c>memory explain</c> report.
/// </summary>
/// <remarks>
/// One projection, because the three surfaces must not describe the same scope differently. The
/// sentences say what an operator needs in order to act: whether recall is narrowed, what narrowed it,
/// and that widening it again is a configuration change rather than a data change.
/// </remarks>
internal static class MemoryCampaignScopeReport
{

    internal static MemoryCampaignScopeDto Describe(MemoryScope scope) =>
        new(
            scope.Kind.ToString(),
            scope.CampaignId,
            scope.Kind switch
            {

                MemoryScopeKind.Campaign =>
                    "Campaign-scoped memory is enabled. A turn here draws on this Campaign's memories plus "
                    + "the installation-scoped ones, and on no other Campaign's.",

                MemoryScopeKind.GlobalOnly =>
                    "Campaign-scoped memory is enabled and nothing resolved a Campaign, so a turn here draws "
                    + "on the installation-scoped memories only.",

                _ =>
                    "Campaign-scoped memory is disabled, so a turn here draws on every durable memory on the "
                    + "installation. Enabling Arcanum:Features:CampaignScopedMemory narrows that; nothing is "
                    + "deleted or re-scoped either way.",

            });

}
