namespace RetroDownfall.Arcanum.Core.Weave.Tapestry;

/// <summary>A complete eligible corpus identity and the revisions observed in the same transaction.</summary>
public sealed record TapestryBuildSnapshot(TapestryScope Scope, long CorpusRevision, long InvalidationRevision, TapestryCorpusIdentity Corpus);

/// <summary>Configuration identity, never a claim that a remote provider's weights are immutable.</summary>
public sealed record TapestryGenerationRecipe(string AlgorithmVersion, string SettingsFingerprint, string? SummaryModel, string SummaryRecipeVersion, int EmbeddingDimension, string? EmbeddingIdentity);

public sealed record TapestryGraphEdge(string ParentNodeId, string ChildNodeId, double? GlobalPosterior, double? LocalPosterior, string MembershipReason);

/// <summary>LegacySingleParent is reserved for the transitional spherical builder; RAPTOR leaves compatibility fields unset.</summary>
public sealed record TapestryGraphCheckpoint(string GenerationId, IReadOnlyList<TapestryNodeWrite> Nodes, IReadOnlyList<TapestryGraphEdge> Edges, bool LegacySingleParent = false);

public sealed record TapestryPublishRequest(string GenerationId, TapestryBuildSnapshot Snapshot, TapestryGenerationRecipe Recipe, TapestryTerminalReason TerminalReason, DateTimeOffset CompletedAt);

/// <summary>Historical settings inputs needed to prove a legacy generation's embedding identity.</summary>
public sealed record TapestryLegacySettings(int MaxTreeDepth, int TargetChildrenPerSummary, int MaxChildrenPerSummary, int MaxClustersPerLayer, int MaxSummaryTokens);

/// <summary>Provider/model identity and actual query width, captured from the generator that produced it.</summary>
public sealed record TapestryRetrievalIdentity(string EmbeddingIdentity, int EmbeddingDimension, TapestryLegacySettings? LegacySettings = null);

/// <summary>A lookup observation, revalidated transactionally before hydrating content.</summary>
public sealed record TapestryGenerationAccess(TapestryGeneration Generation, TapestryRetrievalIdentity Identity);
