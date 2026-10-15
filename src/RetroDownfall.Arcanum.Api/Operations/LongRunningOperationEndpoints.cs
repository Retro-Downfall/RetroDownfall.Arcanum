using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Operations;

namespace RetroDownfall.Arcanum.Api.Operations;

internal static class LongRunningOperationEndpoints
{
    public static RouteGroupBuilder MapLongRunningOperationEndpoints(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapGet("/operations", async (
            string? kind,
            string? state,
            int? limit,
            int? offset,
            ILongRunningOperationStore store,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            if (!TryParseState(state, out LongRunningOperationState? parsedState))
            {
                return Error<LongRunningOperationDto[]>(
                    httpContext,
                    StatusCodes.Status400BadRequest,
                    ErrorCodes.Operation.InvalidState,
                    "State must be Pending, Running, Waiting, Cancelling, Completed, Failed, Abandoned, or ReconciliationRequired.",
                    ArcanumJsonContext.Default.ApiResponseLongRunningOperationDtoArray);
            }

            IReadOnlyList<LongRunningOperation> operations = await store.ListAsync(
                new LongRunningOperationQuery(kind, parsedState, limit ?? 100, offset ?? 0),
                cancellationToken).ConfigureAwait(false);
            LongRunningOperationDto[] data = operations
                .Select(LongRunningOperationDto.FromOperation)
                .ToArray();
            return Success(
                httpContext,
                data,
                ArcanumJsonContext.Default.ApiResponseLongRunningOperationDtoArray);
        })
        .WithName("ListLongRunningOperations");

        apiGroup.MapGet("/operations/{id:guid}", async (
            Guid id,
            ILongRunningOperationStore store,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            LongRunningOperation? operation = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            return operation is null
                ? Error<LongRunningOperationDto>(
                    httpContext,
                    StatusCodes.Status404NotFound,
                    ErrorCodes.Operation.NotFound,
                    "The durable operation was not found.",
                    ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto)
                : Success(
                    httpContext,
                    LongRunningOperationDto.FromOperation(operation),
                    ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);
        })
        .WithName("GetLongRunningOperation");

        apiGroup.MapPost("/operations/{id:guid}/cancel", async (
            Guid id,
            ILongRunningOperationStore store,
            TimeProvider timeProvider,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            LongRunningOperation? operation = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (operation is null)
            {
                return Error<LongRunningOperationDto>(
                    httpContext,
                    StatusCodes.Status404NotFound,
                    ErrorCodes.Operation.NotFound,
                    "The durable operation was not found.",
                    ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);
            }

            bool changed = await store.RequestCancellationAsync(
                id,
                operation.Revision,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            if (!changed)
            {
                return Error<LongRunningOperationDto>(
                    httpContext,
                    StatusCodes.Status409Conflict,
                    ErrorCodes.Operation.StateConflict,
                    "The operation cannot be cancelled in its current state.",
                    ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);
            }

            LongRunningOperation updated = (await store.GetAsync(id, cancellationToken).ConfigureAwait(false))!;
            return Success(
                httpContext,
                LongRunningOperationDto.FromOperation(updated),
                ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);
        })
        .WithName("CancelLongRunningOperation");

        apiGroup.MapPost("/operations/{id:guid}/retry", async (
            Guid id,
            ILongRunningOperationStore store,
            LongRunningOperationReconciler reconciler,
            TimeProvider timeProvider,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            LongRunningOperation? operation = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (operation is null)
            {
                return Error<LongRunningOperationDto>(
                    httpContext,
                    StatusCodes.Status404NotFound,
                    ErrorCodes.Operation.NotFound,
                    "The durable operation was not found.",
                    ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);
            }

            LongRunningRecoveryAdmissionDecision admission =
                LongRunningOperationRecoveryAdmission.Classify(operation, ownerEvidence: null);
            if (admission.Kind is LongRunningRecoveryAdmissionKind.OwnerBoundAwaitingExactOwner)
            {
                return Error<LongRunningOperationDto>(
                    httpContext,
                    StatusCodes.Status409Conflict,
                    ErrorCodes.Operation.StateConflict,
                    "Only Failed, Abandoned, or ReconciliationRequired operations can be retried.",
                    ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);
            }

            bool changed = await store.ResetForRetryAsync(
                id,
                operation.Revision,
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            if (!changed)
            {
                return Error<LongRunningOperationDto>(
                    httpContext,
                    StatusCodes.Status409Conflict,
                    ErrorCodes.Operation.StateConflict,
                    "Only Failed, Abandoned, or ReconciliationRequired operations can be retried.",
                    ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);
            }

            await WakeAfterRetryAsync(
                operation,
                reconciler,
                httpContext.RequestServices.GetService<ILongRestSweepWake>(),
                cancellationToken).ConfigureAwait(false);

            LongRunningOperation updated = (await store.GetAsync(id, cancellationToken).ConfigureAwait(false))!;
            return Success(
                httpContext,
                LongRunningOperationDto.FromOperation(updated),
                ArcanumJsonContext.Default.ApiResponseLongRunningOperationDto);
        })
        .WithName("RetryLongRunningOperation");

        apiGroup.MapPost("/operations/reconcile", async (
            LongRunningOperationReconciler reconciler,
            LongRunningOperationReconciliationStatus status,
            TimeProvider timeProvider,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileAsync(
                now,
                $"manual-{Environment.ProcessId}-{Guid.NewGuid():N}",
                maxOperations: 500,
                maxConcurrency: 4,
                cancellationToken).ConfigureAwait(false);
            status.Record(now, summary);
            return Success(
                httpContext,
                summary,
                ArcanumJsonContext.Default.ApiResponseLongRunningOperationReconciliationSummary);
        })
        .WithName("ReconcileLongRunningOperations");

        return apiGroup;
    }

    /// <summary>
    /// An operator retry returns a Long Rest sweep to <c>Pending</c>, and two timers would otherwise stand between that and the sweep running: the
    /// reconciler parks a retried row as <c>long_rest.readmit_requested</c> on its minute, and the Long Rest worker scans for parked sweeps on its own.
    /// The retry is an explicit decision, so neither is waited out: the reconciler settles the row now, and the worker is woken to readmit it. The
    /// worker proves everything again from the durable rows; the wake is only a hint. Any other kind is left to the reconciler's own cadence.
    /// </summary>
    internal static async Task WakeAfterRetryAsync(
        LongRunningOperation retried,
        LongRunningOperationReconciler reconciler,
        ILongRestSweepWake? wake,
        CancellationToken cancellationToken)
    {
        if (retried.Kind != LongRunningOperationKinds.LongRestSweep)
        {
            return;
        }

        _ = await reconciler
            .ReconcileNowAsync($"operator-retry-{Environment.ProcessId}-{retried.Id:N}", maxOperations: 100, maxConcurrency: 1, cancellationToken)
            .ConfigureAwait(false);

        wake?.Wake(retried.Id);
    }

    private static bool TryParseState(string? value, out LongRunningOperationState? state)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            state = null;
            return true;
        }

        if (Enum.TryParse(value, ignoreCase: true, out LongRunningOperationState parsed)
            && Enum.IsDefined(parsed))
        {
            state = parsed;
            return true;
        }

        state = null;
        return false;
    }

    private static IResult Success<T>(
        HttpContext httpContext,
        T data,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo)
    {
        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        ApiResponse<T> response = ApiResponse<T>.FromResult(Result<T>.Success(data), traceId);
        return Results.Json(response, typeInfo);
    }

    private static IResult Error<T>(
        HttpContext httpContext,
        int statusCode,
        string code,
        string message,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<ApiResponse<T>> typeInfo)
    {
        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        ApiResponse<T> response = ApiResponse<T>.FromResult(
            Result<T>.Failure(new Error(code, message)),
            traceId);
        return Results.Json(response, typeInfo, statusCode: statusCode);
    }
}
