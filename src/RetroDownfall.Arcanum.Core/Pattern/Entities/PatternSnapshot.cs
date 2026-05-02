namespace RetroDownfall.Arcanum.Core.Pattern.Entities;

/// <summary>

/// Situational snapshot: inferred domain plus a bounded table of contents (signature lines).

/// </summary>

public sealed record PatternSnapshot(DomainType Domain, string RootPath, string[] Threads);
