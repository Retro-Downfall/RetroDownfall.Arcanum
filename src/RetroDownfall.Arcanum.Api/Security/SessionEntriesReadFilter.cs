using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Api.Security;

/// <summary>Session transcript reads retain conditional protected authority through complete serialization.</summary>
internal sealed class SessionEntriesReadFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;

        ICovenantExportPolicy? policy = http.RequestServices.GetService<ICovenantExportPolicy>();

        if (policy is null)
        {
            return await next(context).ConfigureAwait(false);
        }

        if (!Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out Guid sessionId))
        {
            return CovenantAuthorityRefusal.Forbidden(http);
        }

        Result<CovenantExportAdmission> acquired = await policy.AcquireConditionalReadAsync(null, http.RequestAborted).ConfigureAwait(false);

        if (acquired.IsFailure)
        {
            return CovenantAuthorityRefusal.InvalidContextPolicy(http, acquired.Error);
        }

        ICovenantSnapshotReadLease? owned = acquired.Value.ReadLease;

        try
        {
            if (owned is null)
            {
                Result<CovenantSessionExportSensitivity> sensitivity = await policy
                    .InspectSessionWithoutLeaseAsync(sessionId, http.RequestAborted).ConfigureAwait(false);

                if (sensitivity.IsFailure)
                {
                    return CovenantAuthorityRefusal.InvalidContextPolicy(http, sensitivity.Error);
                }

                return sensitivity.Value.IsRefused
                    ? CovenantAuthorityRefusal.Forbidden(http)
                    : await next(context).ConfigureAwait(false);
            }

            Result<CovenantReadAuthorityEpoch>? epoch = http.RequestServices.GetService<IOperatorAuthorityContextIssuer>()?.IssueReadEpoch();

            if (epoch is not { IsSuccess: true } issued || !issued.Value.Matches(owned.Snapshot))
            {
                return CovenantAuthorityRefusal.Forbidden(http);
            }

            object? result = await next(context).ConfigureAwait(false);

            IResult response = result switch
            {
                IValueHttpResult { Value: ApiResponse<EntryDto[]> entries } => new CovenantProtectedJsonResult<EntryDto[]>(
                    owned, entries.IsSuccess ? Result<EntryDto[]>.Success(entries.Data!) : Result<EntryDto[]>.Failure(entries.Error!.Value),
                    ArcanumJsonContext.Default.ApiResponseEntryDtoArray),
                IValueHttpResult { Value: ApiResponse<SessionEntryCountDto> count } => new CovenantProtectedJsonResult<SessionEntryCountDto>(
                    owned, count.IsSuccess ? Result<SessionEntryCountDto>.Success(count.Data!) : Result<SessionEntryCountDto>.Failure(count.Error!.Value),
                    ArcanumJsonContext.Default.ApiResponseSessionEntryCountDto),
                _ => CovenantAuthorityRefusal.Forbidden(http)
            };

            if (response is CovenantProtectedJsonResult<EntryDto[]> or CovenantProtectedJsonResult<SessionEntryCountDto>)
            {
                owned = null;
            }

            return response;
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
