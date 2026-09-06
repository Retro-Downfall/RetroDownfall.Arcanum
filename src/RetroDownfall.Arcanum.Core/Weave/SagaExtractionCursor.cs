namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// The exact Grimoire entry through which one Session's Saga extraction has committed.
/// </summary>
/// <remarks>
/// <see cref="EntrySequence"/> is the authoritative resume position. The timestamp travels with it
/// so diagnostics and the legacy watermark API can retain their existing meaning without using a
/// timestamp as an ordering cursor.
/// </remarks>
public sealed record SagaExtractionCursor(
    long EntrySequence,
    DateTimeOffset EntryCreatedAt);
