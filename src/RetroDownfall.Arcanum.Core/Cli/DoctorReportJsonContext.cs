using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Cli;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DoctorReport))]
[JsonSerializable(typeof(DoctorCheck))]
public sealed partial class DoctorReportJsonContext : JsonSerializerContext;
