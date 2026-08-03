using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.Guardrails;

/// <summary>
/// <c>GET /api/guardrails/audit</c> — read-only query surface over the persisted guardrails audit
/// log (§8.x). Returns an empty list (not an error) when
/// <c>Arcanum:Security:Guardrails:AuditLog:Enabled</c> is <see langword="false"/>, matching every
/// other disabled-feature convention in Arcanum.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: thin query-parameter parsing over IGuardrailAuditLogger.QueryAsync; logic covered by GuardrailAuditLoggerTests.
internal static class GuardrailsAuditEndpoints
{

    private const int DefaultPageSize = 100;

    private const int MaxPageSize = 1_000;

    internal static void MapGuardrailsAuditEndpoints(this RouteGroupBuilder apiGroup)
    {

        apiGroup.MapGet("/guardrails/audit", HandleGetGuardrailsAuditAsync).WithName("GetGuardrailsAudit");

    }

    private static async Task<IResult> HandleGetGuardrailsAuditAsync(
        string? from,
        string? to,
        string? stage,
        string? violationType,
        string? sessionId,
        int? limit,
        string? cursor,
        IGuardrailAuditLogger auditLogger,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {

        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        DateTimeOffset? parsedFrom = null;

        if (!string.IsNullOrWhiteSpace(from))
        {

            if (!DateTimeOffset.TryParse(from, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
            {

                return ValidationError(traceId, $"'from' is not a valid date/time: '{from}'.");

            }

            parsedFrom = parsed;

        }

        DateTimeOffset? parsedTo = null;

        if (!string.IsNullOrWhiteSpace(to))
        {

            if (!DateTimeOffset.TryParse(to, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
            {

                return ValidationError(traceId, $"'to' is not a valid date/time: '{to}'.");

            }

            parsedTo = parsed;

        }

        if (parsedFrom.HasValue && parsedTo.HasValue && parsedFrom.Value > parsedTo.Value)
        {

            return ValidationError(traceId, "'from' must not be after 'to'.");

        }

        int effectiveLimit = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        Result<AuditQueryPage<GuardrailAuditRecord>> page = await auditLogger
            .QueryPageAsync(
                parsedFrom,
                parsedTo,
                stage,
                violationType,
                sessionId,
                effectiveLimit,
                cursor,
                cancellationToken)
            .ConfigureAwait(false);

        if (page.IsFailure)
        {

            return ValidationError(traceId, page.Error.Message);

        }

        if (page.Value.NextCursor is not null)
        {

            httpContext.Response.Headers[ArcanumApiHeaders.AuditNextCursor] = page.Value.NextCursor;

        }

        IReadOnlyList<GuardrailAuditRecord> records = page.Value.Records;

        Result<GuardrailAuditRecord[]> result = Result<GuardrailAuditRecord[]>.Success([.. records]);

        return Results.Ok(ApiResponse<GuardrailAuditRecord[]>.FromResult(result, traceId));

    }

    private static IResult ValidationError(string traceId, string message)
    {

        Result<GuardrailAuditRecord[]> invalid = Result<GuardrailAuditRecord[]>.Failure(
            new Error(ErrorCodes.Validation.InvalidQuery, message));

        return Results.Json(
            ApiResponse<GuardrailAuditRecord[]>.FromResult(invalid, traceId),
            ArcanumJsonContext.Default.ApiResponseGuardrailAuditRecordArray,
            statusCode: StatusCodes.Status400BadRequest);

    }

}
