using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Api.Hosting;

public sealed record RepairActivationDrainRequest(Guid ActivationId, int ProcessId, DateTimeOffset StartTime, string QualificationSha256);

public sealed record RepairActivationDrainReceipt(Guid ActivationId, int ProcessId, DateTimeOffset StartTime,
    string QualificationSha256, bool Drained);

internal sealed class RepairActivationDrain(GrimoireConnectionAdmissionGate gate) : IAsyncDisposable
{
    private readonly SemaphoreSlim _transition = new(1);

    private RepairActivationDrainRequest? _intent;

    private IGrimoireExclusiveClosedLease? _closed;

    internal async Task<Result<RepairActivationDrainReceipt>> DrainAsync(RepairActivationDrainRequest request, CancellationToken ct)
    {
        if (request.ActivationId == Guid.Empty || request.ProcessId != Environment.ProcessId
            || request.QualificationSha256 is not { Length: 64 } sha || !sha.All(char.IsAsciiHexDigit))
            return Conflict;

        using Process process = Process.GetCurrentProcess();

        if (new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero) != request.StartTime)
            return Conflict;

        await _transition.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_intent is not null)
                return _intent == request ? Receipt(request) : Conflict;

            Result<IGrimoireClosingOwner> admitted = gate.BeginOrResumeRepairActivationDrain(request.ActivationId,
                new CovenantDigest(Convert.FromHexString(sha)));

            if (admitted.IsFailure) return admitted.Error;

            await using IGrimoireClosingOwner owner = admitted.Value;

            try
            {
                Result requestDrain = await gate.DrainRequestAndWorkAsync(owner, ct).ConfigureAwait(false);

                if (requestDrain.IsFailure) return requestDrain.Error;

                var closed = await gate.CloseConnectionAdmissionAsync(owner, ct).ConfigureAwait(false);

                if (closed.IsFailure) return closed.Error;

                _closed = closed.Value;

                _intent = request;

                return Receipt(request);
            }
            finally
            {
                if (_closed is null)
                {
                    // This route never writes installation state. A failed or abandoned drain can
                    // release its own admission change without authorizing any other maintenance.
                    _ = await gate.AbortClosingAsync(owner, static _ => ValueTask.FromResult(true),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _transition.Release();
        }
    }

    private static RepairActivationDrainReceipt Receipt(RepairActivationDrainRequest request) =>
        new(request.ActivationId, request.ProcessId, request.StartTime, request.QualificationSha256, true);

    internal static Error Conflict => new(ErrorCodes.Data.Conflict, "The repair activation does not own this exact boot and drain.");

    public async ValueTask DisposeAsync()
    {
        if (_closed is { } closed) await closed.DisposeAsync().ConfigureAwait(false);

        _transition.Dispose();
    }
}

internal static class RepairActivationDrainEndpoint
{
    internal static void MapRepairActivationDrain(this RouteGroupBuilder apiGroup)
    {
        apiGroup.MapPost("/server/repair-drain", async (RepairActivationDrainRequest request, RepairActivationDrain drain,
            HttpContext context, CancellationToken ct) =>
        {
            bool local = context.Connection.RemoteIpAddress is not { } address || IPAddress.IsLoopback(address);

            Result<RepairActivationDrainReceipt> result = local
                ? await drain.DrainAsync(request, ct).ConfigureAwait(false)
                : new Error("Data.RepairActivationLocalOperatorRequired", "Repair activation requires an authenticated loopback operator.");

            int status = result.IsSuccess ? StatusCodes.Status200OK
                : local ? StatusCodes.Status409Conflict : StatusCodes.Status403Forbidden;

            return Results.Json(ApiResponse<RepairActivationDrainReceipt>.FromResult(result, context.TraceIdentifier),
                ArcanumJsonContext.Default.ApiResponseRepairActivationDrainReceipt, statusCode: status);
        })
        .WithName("DrainServerForRepairActivation")
        .WithMetadata(GrimoireAdmissionExemptRouteMetadata.Instance);
    }
}
