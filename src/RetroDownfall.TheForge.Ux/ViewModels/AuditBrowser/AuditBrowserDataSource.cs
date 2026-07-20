using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.TheForge.Ux.Services.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.AuditBrowser;

/// <summary>
/// Data-source seam for the Audit Browser (Phase 8). Wraps <see cref="AuditService"/> for
/// inference and guardrails audit queries. Tests fake this interface. Empty arrays from the server
/// mean logging is disabled <em>or</em> no records match — never an error.
/// </summary>
public interface IAuditBrowserDataSource
{

    Task<DataSourceResult<InferenceAuditRecord[]>> QueryInferenceAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? model,
        string? sessionId,
        int? limit,
        CancellationToken cancellationToken);

    Task<DataSourceResult<GuardrailAuditRecord[]>> QueryGuardrailsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? stage,
        string? violationType,
        string? sessionId,
        int? limit,
        CancellationToken cancellationToken);

}

/// <summary>API-backed <see cref="IAuditBrowserDataSource"/>.</summary>
public sealed class AuditBrowserDataSource : IAuditBrowserDataSource
{

    private readonly AuditService _service;

    public AuditBrowserDataSource(AuditService service)
    {

        _service = service;

    }

    public async Task<DataSourceResult<InferenceAuditRecord[]>> QueryInferenceAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? model,
        string? sessionId,
        int? limit,
        CancellationToken cancellationToken)
    {

        ApiResponse<InferenceAuditRecord[]>? response = await _service
            .QueryInferenceAsync(from, to, model, sessionId, limit, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<InferenceAuditRecord[]>.FromResponse(response);

    }

    public async Task<DataSourceResult<GuardrailAuditRecord[]>> QueryGuardrailsAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? stage,
        string? violationType,
        string? sessionId,
        int? limit,
        CancellationToken cancellationToken)
    {

        ApiResponse<GuardrailAuditRecord[]>? response = await _service
            .QueryGuardrailsAsync(from, to, stage, violationType, sessionId, limit, cancellationToken)
            .ConfigureAwait(false);

        return DataSourceResult<GuardrailAuditRecord[]>.FromResponse(response);

    }

}
