using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Core.Serialization;

namespace RetroDownfall.TheForge.Ux.Services.Services;

/// <summary>
/// Wraps the read-only audit query surfaces: <c>GET /api/audit</c> (inference turns) and
/// <c>GET /api/guardrails/audit</c> (guardrail violations). Both return empty arrays — not errors —
/// when their respective audit logs are disabled server-side.
/// </summary>
public sealed class AuditService
{

    private readonly ArcanumApiClient _apiClient;

    public AuditService(ArcanumApiClient apiClient)
    {

        _apiClient = apiClient;

    }

    public Task<ApiResponse<InferenceAuditRecord[]>?> QueryInferenceAsync(
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

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseInferenceAuditRecordArray, cancellationToken);

    }

    /// <summary>Backward-compatible alias for <see cref="QueryInferenceAsync"/>.</summary>
    public Task<ApiResponse<InferenceAuditRecord[]>?> QueryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? model,
        string? sessionId,
        int? limit,
        CancellationToken cancellationToken) =>
        QueryInferenceAsync(from, to, model, sessionId, limit, cancellationToken);

    public Task<ApiResponse<GuardrailAuditRecord[]>?> QueryGuardrailsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? stage,
        string? violationType,
        string? sessionId,
        int? limit,
        CancellationToken cancellationToken)
    {

        string path = QueryStringBuilder.Build(
            "/api/guardrails/audit",
            ("from", from?.ToString("O")),
            ("to", to?.ToString("O")),
            ("stage", stage),
            ("violationType", violationType),
            ("sessionId", sessionId),
            ("limit", limit?.ToString()));

        return _apiClient.GetAsync(path, TheForgeJsonContext.Default.ApiResponseGuardrailAuditRecordArray, cancellationToken);

    }

}
