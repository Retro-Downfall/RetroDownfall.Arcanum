using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.TheForge.Ux.ViewModels;

/// <summary>
/// Forge-local result of a data-source API call. Carries the unwrapped payload plus the server
/// <see cref="Error.Code"/>/<see cref="Error.Message"/> on failure, so ViewModels can render honest
/// disabled / feature-off / not-found states without ever touching the <see cref="ApiResponse{T}"/>
/// envelope or <see cref="System.Net.Http.HttpClient"/>. Data sources map responses via
/// <see cref="FromResponse"/>; they never throw on API failures.
/// </summary>
/// <remarks>
/// <see cref="ApiResponse{T}"/> returns <see langword="null"/> from <c>ArcanumApiClient</c> only for a
/// 204 no-body success (transport faults synthesize a failure envelope, never <see langword="null"/>),
/// so a <see langword="null"/> response maps to success here. <see cref="ApiResponse{T}.Error"/> is
/// nullable on the envelope, so it is unwrapped defensively.
/// </remarks>
public sealed record DataSourceResult<T>(T? Data, bool Success, string? ErrorCode, string? ErrorMessage)
{

    public static DataSourceResult<T> FromResponse(ApiResponse<T>? response)
    {

        if (response is null)
        {

            return new(default, true, null, null);

        }

        if (response.IsSuccess)
        {

            return new(response.Data, true, null, null);

        }

        return new(
            default,
            false,
            response.Error?.Code,
            response.Error?.Message ?? "The request failed.");

    }

}
