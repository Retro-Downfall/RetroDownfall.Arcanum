namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// API envelope. <see cref="Error" /> is guaranteed to be non-null when <see cref="IsSuccess" /> is false,
/// and null when <see cref="IsSuccess" /> is true. The nullable annotation is required because JSON serialization
/// must omit the error on success.
/// </summary>
public sealed record ApiResponse<T>(T? Data, bool IsSuccess, Error? Error, string? TraceId = null)
{
    public static ApiResponse<T> FromResult(Result<T> result, string? traceId = null) =>

        result.IsSuccess
            ? new ApiResponse<T>(result.Value, true, null, traceId)
            : new ApiResponse<T>(default, false, result.Error, traceId);
}
