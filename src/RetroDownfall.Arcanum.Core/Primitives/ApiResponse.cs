using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// API envelope. <see cref="Error" /> is guaranteed to be non-null when <see cref="IsSuccess" /> is false,
/// and null when <see cref="IsSuccess" /> is true. The nullable annotation is required because JSON serialization
/// must omit the error on success.
/// </summary>
/// <remarks>
/// <para>W3.6: <see cref="Data" /> is omitted from the wire when it equals the default for <typeparamref name="T" />
/// (<see cref="JsonIgnoreCondition.WhenWritingDefault" />). On failure (<see cref="IsSuccess" /> false) the data is
/// always the default, so failure envelopes carry no <c>data</c> field for both reference- and value-type payloads —
/// removing the ambiguous defaulted <c>data: false</c>/<c>data: null</c> that a client could misread without checking
/// <see cref="IsSuccess" />. (This is valid for value types, unlike <see cref="JsonIgnoreCondition.WhenWritingNull" />,
/// which throws at source-generation configuration time for non-nullable <typeparamref name="T" /> such as <c>bool</c>.)
/// On success a default-valued payload (e.g. <c>false</c>/<c>0</c>/<c>null</c>) is likewise omitted and read back as the
/// default, which is lossless.</para>
/// </remarks>
public sealed record ApiResponse<T>(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] T? Data,
    bool IsSuccess,
    Error? Error,
    string? TraceId = null)
{
    public static ApiResponse<T> FromResult(Result<T> result, string? traceId = null) =>

        result.IsSuccess
            ? new ApiResponse<T>(result.Value, true, null, traceId)
            : new ApiResponse<T>(default, false, result.Error, traceId);
}
