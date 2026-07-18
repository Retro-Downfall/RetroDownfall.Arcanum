using System.Text.Json.Serialization;
using RetroDownfall.TheForge.Core.Models.DiagnosticMcp;

namespace RetroDownfall.TheForge.Core.Serialization;

/// <summary>Source-generated JSON for The Forge-local Diagnostic MCP Invocation fixtures.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiagnosticMcpFixtureStoreDocument))]
[JsonSerializable(typeof(DiagnosticMcpFixtureRecord))]
[JsonSerializable(typeof(IReadOnlyList<DiagnosticMcpFixtureRecord>))]
public partial class TheForgeDiagnosticMcpFixturesJsonContext : JsonSerializerContext;
