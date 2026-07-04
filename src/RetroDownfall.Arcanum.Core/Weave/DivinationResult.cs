namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// A single Divination (semantic search) hit against The Weave. Phase 1: returned from
/// <see cref="IDivinationService.SearchAsync"/> and consumed internally (e.g. by tests) only — no
/// Phase 1 endpoint serializes this to the wire, so it is intentionally not registered on
/// <c>ArcanumJsonContext</c> yet. Future phases that expose Divination over HTTP (for example the
/// Phase 2 session search endpoint) register their own wire DTOs shaped for that endpoint rather than
/// serializing this type directly.
/// </summary>
public sealed record DivinationResult(
    string Id,
    float Similarity,
    IReadOnlyDictionary<string, string> Metadata);
