namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

/// <summary>
/// A single lore entry on the <c>/api/lore</c> surface.
/// </summary>
/// <param name="Key">Lore key.</param>
/// <param name="Value">Lore value.</param>
/// <param name="UpdatedAtUtc">
/// Last-write instant. Producers must supply a <see cref="DateTimeKind.Utc"/> value so every code
/// path emits the same wire shape — SQLite materializes stored timestamps as
/// <see cref="DateTimeKind.Unspecified"/>, which would otherwise serialize without the <c>Z</c>
/// designator on read paths while the upsert path emits one.
/// </param>
public sealed record LoreDto(string Key, string Value, DateTime UpdatedAtUtc);
