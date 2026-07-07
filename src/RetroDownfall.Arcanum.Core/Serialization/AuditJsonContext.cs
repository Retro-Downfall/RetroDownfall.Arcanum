using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Core.Serialization;

/// <summary>
/// AOT-safe source-generated JSON context for the persisted inference audit log's on-disk JSONL
/// format (§8.26) — deliberately a separate context from <c>ConfigurationJsonContext</c> (config
/// file) and the Api project's <c>ArcanumJsonContext</c> (HTTP wire types), since this format is
/// neither: it is Core's own on-disk record shape, readable by both <c>InferenceAuditLogger</c>
/// (Infrastructure, writes it) and <c>GET /api/audit</c> (Api, reads it back via the shared
/// <see cref="IInferenceAuditLogger.QueryAsync"/> contract).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InferenceAuditRecord))]
[JsonSerializable(typeof(GuardrailAuditRecord))]
[JsonSerializable(typeof(List<string>))]
public sealed partial class AuditJsonContext : JsonSerializerContext;
