using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Cli;

// House rule: no blanket JsonStringEnumConverter here — each doctor enum carries its own
// [JsonConverter] so the production ArcanumJsonContext and this test-side context agree.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DoctorReport))]
[JsonSerializable(typeof(DoctorCheck))]
[JsonSerializable(typeof(DoctorRemedy))]
[JsonSerializable(typeof(DoctorRepairResult))]
[JsonSerializable(typeof(DoctorRepairStep))]
[JsonSerializable(typeof(DoctorCatalog))]
[JsonSerializable(typeof(DoctorCatalogCheck))]
[JsonSerializable(typeof(DoctorCatalogRepair))]
public sealed partial class DoctorReportJsonContext : JsonSerializerContext;
