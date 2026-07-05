namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Raw wire shape of a Chronicle SSE <c>data:</c> frame — deserialized via a source-generated
/// <see cref="RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext"/> type (AOT/trim-safe)
/// rather than an ad-hoc <see cref="System.Text.Json.JsonDocument"/> walk. All fields are optional:
/// different Chronicle event kinds populate different subsets of these (a status update vs. a tool
/// result vs. an error, etc.) — see <c>ArcanumApiClient.TryParseChronicleFrame</c> (Cli).
/// </summary>
public sealed record ChronicleFrameWire(
    string? Type,
    string? Timestamp,
    string? Message,
    string? Description,
    string? Result,
    string? Error,
    string? Summary);
