using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>Exact, content-free Saga identity admitted by the successful provider branch.</summary>
public sealed record SagaUseCoordinate(string MemoryId, string ClaimId, string VersionId, int HashFormat, CovenantDigest ContentDigest);
