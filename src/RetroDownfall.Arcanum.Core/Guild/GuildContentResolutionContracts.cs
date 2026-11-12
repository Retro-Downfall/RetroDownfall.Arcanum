using System.Collections.Immutable;

namespace RetroDownfall.Arcanum.Core.Guild;

/// <summary>A read-only lookup of currently visible content identities by canonical UUID prefix.</summary>
public sealed record GuildResolveContentRequest(string Prefix);

/// <summary>Zero, one or two metadata references prove absence, uniqueness or ambiguity; no body is read.</summary>
public sealed record GuildContentResolutionDto(ImmutableArray<GuildDataRef> Candidates);
