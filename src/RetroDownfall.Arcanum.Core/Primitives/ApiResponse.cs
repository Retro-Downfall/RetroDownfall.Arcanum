namespace RetroDownfall.Arcanum.Core.Primitives;

public sealed record ApiResponse<T>(T? Data, bool IsSuccess, Error? Error, string? TraceId = null)
{

    public static ApiResponse<T> FromResult(Result<T> result, string? traceId = null) =>

        result.IsSuccess

            ? new ApiResponse<T>(result.Value, true, null, traceId)

            : new ApiResponse<T>(default, false, result.Error, traceId);

}
