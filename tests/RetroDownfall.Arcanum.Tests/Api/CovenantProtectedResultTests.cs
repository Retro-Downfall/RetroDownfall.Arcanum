using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Issue #88 — the lease-bound results every protected Covenant response is written through.
/// </summary>
/// <remarks>
/// The defect these prevent is the window between "the store answered" and "the bytes reached the
/// client". A reader that released its lease when the handler returned would still be serializing
/// protected content while a reset reported the data gone, and the operator would be told erasure was
/// complete by the same process that was writing the erased content down a socket. The lease is held
/// through serialization and revalidated immediately before the first byte, so a destructive
/// operation that lands mid-response is a typed refusal rather than a leak.
/// </remarks>
public sealed class CovenantProtectedResultTests
{

    private static readonly CovenantStatusDto Payload = new(
        Enabled: true,
        Available: true,
        Census: CovenantCensusReadState.Read,
        Counts: [],
        GlobalConfirmedRenderedBytes: 0,
        MaxCampaignConfirmedRenderedBytes: 0,
        MaxCampaignProposedRenderedBytes: 0,
        RenderedByteCeilingPerSection: CovenantLimits.MaxGlobalConfirmedRenderedBytes,
        Search: new CovenantSearchHealthDto(
            CovenantSearchHealthState.Healthy,
            CovenantSearchExecutionMode.Fts,
            CovenantSearchRebuildGuidance.None),
        Retention: "never",
        DegradationCode: null);

    [Fact]
    public async Task A_successful_protected_response_is_no_store_and_never_conditionally_cacheable()
    {

        DefaultHttpContext context = NewContext();

        FakeLease lease = new();

        await new CovenantProtectedJsonResult<CovenantStatusDto>(
                lease,
                Result<CovenantStatusDto>.Success(Payload),
                ArcanumJsonContext.Default.ApiResponseCovenantStatusDto)
            .ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        // Issue #89 widened this from a bare no-store. The two extra headers are for the
        // HTTP/1.0-era intermediaries that ignore Cache-Control entirely, and "private" survives the
        // caches that treat an unqualified no-store as advisory.
        Assert.Equal("no-store, private", context.Response.Headers.CacheControl.ToString());

        Assert.Equal("no-cache", context.Response.Headers.Pragma.ToString());

        Assert.Equal("0", context.Response.Headers.Expires.ToString());

        Assert.True(string.IsNullOrEmpty(context.Response.Headers.ETag.ToString()));

        Assert.True(string.IsNullOrEmpty(context.Response.Headers.LastModified.ToString()));

        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);

        Assert.Contains("\"enabled\":true", ReadBody(context), StringComparison.Ordinal);

    }

    [Fact]
    public async Task The_lease_is_revalidated_before_the_first_byte_and_released_exactly_once()
    {

        DefaultHttpContext context = NewContext();

        FakeLease lease = new() { Context = context };

        await new CovenantProtectedJsonResult<CovenantStatusDto>(
                lease,
                Result<CovenantStatusDto>.Success(Payload),
                ArcanumJsonContext.Default.ApiResponseCovenantStatusDto)
            .ExecuteAsync(context);

        Assert.Equal(1, lease.Revalidations);

        Assert.Equal(1, lease.Disposals);

        Assert.Equal(0, lease.BytesWrittenBeforeRevalidation);

        Assert.True(context.Response.Body.Length > 0);

    }

    [Fact]
    public async Task A_lease_that_lost_its_generation_writes_the_refusal_and_never_the_payload()
    {

        DefaultHttpContext context = NewContext();

        FakeLease lease = new()
        {
            Revalidation = Result.Failure(
                new Error(ErrorCodes.Covenant.Unavailable, "The Covenant dataset was replaced.")),
        };

        await new CovenantProtectedJsonResult<CovenantStatusDto>(
                lease,
                Result<CovenantStatusDto>.Success(Payload),
                ArcanumJsonContext.Default.ApiResponseCovenantStatusDto)
            .ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);

        string body = ReadBody(context);

        Assert.DoesNotContain("\"enabled\"", body, StringComparison.Ordinal);

        Assert.Contains(ErrorCodes.Covenant.Unavailable, body, StringComparison.Ordinal);

        // Issue #89 widened this from a bare no-store. The two extra headers are for the
        // HTTP/1.0-era intermediaries that ignore Cache-Control entirely, and "private" survives the
        // caches that treat an unqualified no-store as advisory.
        Assert.Equal("no-store, private", context.Response.Headers.CacheControl.ToString());

        Assert.Equal("no-cache", context.Response.Headers.Pragma.ToString());

        Assert.Equal("0", context.Response.Headers.Expires.ToString());

        Assert.Equal(1, lease.Disposals);

    }

    [Fact]
    public async Task A_failed_payload_maps_its_own_status_and_still_releases_the_lease()
    {

        DefaultHttpContext context = NewContext();

        FakeLease lease = new();

        await new CovenantProtectedJsonResult<CovenantStatusDto>(
                lease,
                Result<CovenantStatusDto>.Failure(
                    new Error(ErrorCodes.Covenant.StaleCursor, "This page no longer exists.")),
                ArcanumJsonContext.Default.ApiResponseCovenantStatusDto)
            .ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);

        Assert.Equal(1, lease.Disposals);

    }

    [Fact]
    public async Task Executing_twice_is_refused_rather_than_writing_a_second_body_under_a_released_lease()
    {

        DefaultHttpContext context = NewContext();

        FakeLease lease = new();

        CovenantProtectedJsonResult<CovenantStatusDto> result = new(
            lease,
            Result<CovenantStatusDto>.Success(Payload),
            ArcanumJsonContext.Default.ApiResponseCovenantStatusDto);

        await result.ExecuteAsync(context);

        await Assert.ThrowsAsync<InvalidOperationException>(() => result.ExecuteAsync(NewContext()));

        Assert.Equal(1, lease.Disposals);

    }

    [Fact]
    public async Task A_protected_stream_revalidates_before_the_first_byte_and_disposes_both_lease_and_stream()
    {

        DefaultHttpContext context = NewContext();

        FakeLease lease = new();

        TrackingStream source = new("protected bytes"u8.ToArray());

        await new CovenantProtectedStreamResult(lease, source, "application/octet-stream").ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        // Issue #89 widened this from a bare no-store. The two extra headers are for the
        // HTTP/1.0-era intermediaries that ignore Cache-Control entirely, and "private" survives the
        // caches that treat an unqualified no-store as advisory.
        Assert.Equal("no-store, private", context.Response.Headers.CacheControl.ToString());

        Assert.Equal("no-cache", context.Response.Headers.Pragma.ToString());

        Assert.Equal("0", context.Response.Headers.Expires.ToString());

        Assert.Equal("protected bytes", ReadBody(context));

        Assert.Equal(1, lease.Revalidations);

        Assert.Equal(1, lease.Disposals);

        Assert.True(source.Disposed);

    }

    [Fact]
    public async Task A_protected_stream_whose_lease_moved_on_writes_no_content_at_all()
    {

        DefaultHttpContext context = NewContext();

        FakeLease lease = new()
        {
            Revalidation = Result.Failure(
                new Error(ErrorCodes.Covenant.ErasureIncomplete, "Local erasure has not completed.")),
        };

        TrackingStream source = new("protected bytes"u8.ToArray());

        await new CovenantProtectedStreamResult(lease, source, "application/octet-stream").ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);

        Assert.DoesNotContain("protected", ReadBody(context), StringComparison.Ordinal);

        Assert.True(source.Disposed);

        Assert.Equal(1, lease.Disposals);

    }

    private static DefaultHttpContext NewContext()
    {

        DefaultHttpContext context = new();

        context.Response.Body = new MemoryStream();

        return context;

    }

    private static string ReadBody(HttpContext context)
    {

        MemoryStream body = (MemoryStream)context.Response.Body;

        return Encoding.UTF8.GetString(body.ToArray());

    }

    /// <summary>
    /// A lease that records what happened to it, including how many response bytes existed at the
    /// moment it was asked to revalidate. Zero is the only acceptable answer.
    /// </summary>
    private sealed class FakeLease : ICovenantOperationLease
    {

        private readonly CancellationTokenSource _revocation = new();

        public Result Revalidation { get; init; } = Result.Success();

        public int Revalidations { get; private set; }

        public int Disposals { get; private set; }

        public long BytesWrittenBeforeRevalidation { get; private set; } = -1;

        public HttpContext? Context { get; set; }

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.NewGuid(),
            RuntimeAuthorityGeneration: 1,
            CovenantLeaseKind.InstallationRead,
            CovenantLeaseCoverage.Installation,
            Scope: null,
            DatasetGeneration: Guid.NewGuid(),
            CapabilityGeneration: 1,
            AuthorityEpoch: 1,
            CanonicalSequence: 0,
            CampaignAvailabilityGeneration: null,
            CampaignPathRevision: null,
            AcceleratorEpoch: null,
            AppliedCampaignDeletionSequence: null,
            RecoveryOwner: null,
            CleanupOnlyHistoricalCampaign: false);

        public CancellationToken Revocation => _revocation.Token;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {

            Revalidations++;

            BytesWrittenBeforeRevalidation = Context?.Response.Body.Length ?? 0;

            return ValueTask.FromResult(Revalidation);

        }

        public ValueTask DisposeAsync()
        {

            Disposals++;

            _revocation.Dispose();

            return ValueTask.CompletedTask;

        }

    }

    private sealed class TrackingStream(byte[] content) : MemoryStream(content)
    {

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {

            Disposed = true;

            base.Dispose(disposing);

        }

    }

}
