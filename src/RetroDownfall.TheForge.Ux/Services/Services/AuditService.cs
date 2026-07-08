using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>Wraps <c>GET /api/audit</c> — the persisted inference audit log query surface (The Scrying Pool).</summary>
public sealed class AuditService
{

    private readonly ArcanumApiClient _apiClient;

    public AuditService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<InferenceAuditRecord[]>?> QueryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? model,
        string? sessionId,
        int? limit,
        CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            "/api/audit",
            ("from", from?.ToString("O")),
            ("to", to?.ToString("O")),
            ("model", model),
            ("sessionId", sessionId),
            ("limit", limit?.ToString()));

        return _apiClient.GetAsync(path, ForgeJsonContext.Default.ApiResponseInferenceAuditRecordArray, cancellationToken);

    }

}
