using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.DataLifecycle;

/// <summary>
/// Process-authoritative retention settings snapshot. Updates are serialized with persistence so
/// API reads and retention plans observe a successful change immediately even when the underlying
/// configuration provider does not reload files.
/// </summary>
public interface IDataRetentionPolicyStore
{

    RetentionSettings Current { get; }

    Task<Result<RetentionSettings>> UpdateRuleAsync(
        RetentionRuleUpdateRequest request,
        CancellationToken cancellationToken = default);

}
