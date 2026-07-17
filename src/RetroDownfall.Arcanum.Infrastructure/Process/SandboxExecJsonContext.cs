using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SandboxExecHelperPayload))]
internal partial class SandboxExecJsonContext : JsonSerializerContext;
