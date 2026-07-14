using System.Text.Json.Serialization;

namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Source-generated JSON context for <see cref="TheForgeSettings"/> and nested dock-layout DTOs —
/// the shape of <c>~/.config/arcanum/forge.json</c>. camelCase to match Arcanum's own wire/config
/// convention. No blanket <c>JsonStringEnumConverter</c> is registered (house rule).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TheForgeSettings))]
[JsonSerializable(typeof(TheForgeDockLayoutDto))]
[JsonSerializable(typeof(TheForgeDockToolLayoutDto))]
[JsonSerializable(typeof(IReadOnlyList<TheForgeDockToolLayoutDto>))]
public partial class TheForgeSettingsJsonContext : JsonSerializerContext;
