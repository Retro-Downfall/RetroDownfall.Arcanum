using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// An <see cref="IMemoryScopeResolver"/> with no database behind it, for suites that compose the MCP
/// tool server directly.
/// </summary>
/// <remarks>
/// Defaults to <see cref="MemoryScope.Installation"/>, which is the scope a disabled gate produces and
/// therefore the one that leaves every existing assertion describing the behaviour it always described.
/// Set <see cref="Scope"/> to make a suite ask a scoped question.
/// </remarks>
internal sealed class FakeMemoryScopeResolver(MemoryScope? scope = null) : IMemoryScopeResolver
{

    internal MemoryScope Scope { get; set; } = scope ?? MemoryScope.Installation;

    /// <summary>The Session id the tool call actually resolved through, for wiring assertions.</summary>
    internal Guid? LastSessionId { get; private set; }

    public bool IsCampaignScopingEnabled => Scope.IsEnforced;

    public MemoryScope ForResolvedCampaign(Guid? campaignId) =>
        MemoryScope.Resolve(IsCampaignScopingEnabled, campaignId);

    public ValueTask<MemoryScope> ResolveForSessionAsync(Guid? sessionId, CancellationToken cancellationToken)
    {

        LastSessionId = sessionId;

        return ValueTask.FromResult(Scope);

    }

}
