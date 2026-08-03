namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// One stable newest-first audit page. <see cref="NextCursor"/> is opaque, query-bound, and
/// snapshot-bound; callers must send it unchanged with the same filters to continue.
/// </summary>
public sealed record AuditQueryPage<T>(
    IReadOnlyList<T> Records,
    string? NextCursor);
