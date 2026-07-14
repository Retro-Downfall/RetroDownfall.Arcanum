using RetroDownfall.Arcanum.Core.CommLink;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps <c>POST /api/commlink/send</c> for the Comm Link Alert Dashboard.</summary>
public sealed class CommLinkService
{

    private readonly ArcanumApiClient _apiClient;

    public CommLinkService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<bool>?> SendAsync(string title, string body, CommLinkSeverity severity, string source, CancellationToken cancellationToken) =>
        _apiClient.PostAsync(
            "/api/commlink/send",
            new CommLinkMessageRequestDto(title, body, severity, source),
            TheForgeJsonContext.Default.CommLinkMessageRequestDto,
            TheForgeJsonContext.Default.ApiResponseBoolean,
            cancellationToken);

}
