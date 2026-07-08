using System.Text.Json.Serialization;

namespace RetroDownfall.TheForge.Core.Models;

/// <summary>
/// Source-generated JSON context for <see cref="ForgeSettings"/> — the shape of
/// <c>~/.config/arcanum/forge.json</c>. camelCase to match Arcanum's own wire/config convention.
/// No blanket <c>JsonStringEnumConverter</c> is registered (house rule); <see cref="ForgeSettings"/>
/// has no enum properties today.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ForgeSettings))]
public partial class ForgeSettingsJsonContext : JsonSerializerContext;
