using System.Text.Json.Serialization;

namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Source-generated JSON context for <see cref="ForgeSettings"/> and nested dock-layout DTOs —
/// the shape of <c>~/.config/arcanum/forge.json</c>. camelCase to match Arcanum's own wire/config
/// convention. No blanket <c>JsonStringEnumConverter</c> is registered (house rule).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ForgeSettings))]
[JsonSerializable(typeof(ForgeDockLayoutDto))]
[JsonSerializable(typeof(ForgeDockToolLayoutDto))]
[JsonSerializable(typeof(IReadOnlyList<ForgeDockToolLayoutDto>))]
public partial class ForgeSettingsJsonContext : JsonSerializerContext;
