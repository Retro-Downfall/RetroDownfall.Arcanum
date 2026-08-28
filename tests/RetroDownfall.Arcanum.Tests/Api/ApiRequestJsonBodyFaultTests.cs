using System.Net;

using System.Text;

using System.Text.Json;

using Microsoft.AspNetCore.Hosting;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// A request body the server cannot finish reading answers the client, not the operator's error log.
/// </summary>
/// <remarks>
/// Kestrel raises <see cref="BadHttpRequestException"/> for a body that ends early and for one past the
/// size ceiling, carrying the status it chose — 400 and 413. <c>ApiRequestJson.ReadAsync</c> caught only
/// <c>JsonException</c> and <c>InvalidOperationException</c>, so that exception escaped to
/// <c>ArcanumExceptionHandler</c>, which special-cases only <c>JsonException</c>, and a client that
/// dropped mid-upload was told the server broke: 500 <c>Hub.Unhandled</c> with an Error-level log.
///
/// <para>The routes are driven through the real application because the helper is shared by every route
/// that reads a JSON body, so this held for all of them rather than for one family. <c>/api/lore</c> is
/// here deliberately: its use of the helper long predates the Saga curation routes, which is what makes
/// this a repair of the helper rather than of one caller.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class ApiRequestJsonBodyFaultTests
{

    [SkippableTheory]
    [InlineData("/api/lore")]
    [InlineData("/api/memory/saga/m-1/retire")]
    public async Task A_body_that_ends_early_is_answered_with_the_envelope_and_not_a_server_error(string route)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpResponseMessage response = await SendFaultingBodyAsync(
            route,
            new BadHttpRequestException("Unexpected end of request content", StatusCodes.Status400BadRequest));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await AssertEnvelopeAsync(response, ErrorCodes.Validation.InvalidBody, ApiRequestJson.IncompleteBodyMessage);

    }

    [SkippableTheory]
    [InlineData("/api/lore")]
    [InlineData("/api/memory/saga/m-1/retire")]
    public async Task A_body_past_the_size_ceiling_keeps_the_status_kestrel_chose(string route)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpResponseMessage response = await SendFaultingBodyAsync(
            route,
            new BadHttpRequestException("Request body too large", StatusCodes.Status413PayloadTooLarge));

        // 413 rather than a flat 400: the two faults are different to a client, and Kestrel has already
        // told them apart. Collapsing them would tell a client to retry a body that can never fit.
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        // A distinct code, not the invalid-body one. Every other code on this installation's 413 is
        // distinct from its family's invalid-request code, and Validation.InvalidBody is pinned to 400
        // by the mapper and its tests — so reusing it here would have put one code on two statuses.
        await AssertEnvelopeAsync(response, ErrorCodes.Validation.BodyTooLarge, ApiRequestJson.BodyTooLargeMessage);

    }

    /// <summary>
    /// A body arriving under Kestrel's minimum data rate is a 408, and says so.
    /// </summary>
    /// <remarks>
    /// Reachable in production on a slow or stalled upload, and it was the third status the helper's
    /// new catch could receive. Nothing is wrong with the body, so a 400 would be actively misleading:
    /// it is worth resending unchanged on a better connection.
    /// </remarks>
    [SkippableTheory]
    [InlineData("/api/lore")]
    [InlineData("/api/memory/saga/m-1/retire")]
    public async Task A_body_arriving_too_slowly_is_a_timeout_rather_than_a_bad_request(string route)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpResponseMessage response = await SendFaultingBodyAsync(
            route,
            new BadHttpRequestException(
                "Reading the request body timed out due to data arriving too slowly",
                StatusCodes.Status408RequestTimeout));

        Assert.Equal(HttpStatusCode.RequestTimeout, response.StatusCode);

        await AssertEnvelopeAsync(
            response,
            ErrorCodes.Validation.BodyReadTimeout,
            ApiRequestJson.BodyReadTimeoutMessage);

    }

    /// <summary>
    /// Each code resolves through the mapper to the very status the helper set, so the two authorities
    /// cannot drift apart.
    /// </summary>
    [Theory]
    [InlineData(ErrorCodes.Validation.InvalidBody, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Validation.BodyTooLarge, StatusCodes.Status413PayloadTooLarge)]
    [InlineData(ErrorCodes.Validation.BodyReadTimeout, StatusCodes.Status408RequestTimeout)]
    public void Every_body_fault_code_maps_to_the_status_the_helper_sends_it_with(string code, int expected) =>
        Assert.Equal(expected, RetroDownfall.Arcanum.Api.Primitives.ArcanumErrorMapper.ResolveStatusCode(code));

    private static async Task AssertEnvelopeAsync(
        HttpResponseMessage response,
        string expectedCode,
        string expectedMessage)
    {

        ApiResponse<bool>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.False(body!.IsSuccess);

        Assert.Equal(expectedCode, body.Error?.Code);

        Assert.Equal(expectedMessage, body.Error?.Message);

        Assert.False(string.IsNullOrEmpty(body.TraceId));

    }

    private static async Task<HttpResponseMessage> SendFaultingBodyAsync(string route, Exception failure)
    {

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
                services.AddSingleton<IStartupFilter>(new BodyFaultFilter(route, failure)),
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        return await client.PostAsync(
            route,
            new StringContent("{\"key\":\"k\"}", Encoding.UTF8, "application/json"));

    }

    /// <summary>Replaces the request body with one that fails the way Kestrel's does.</summary>
    /// <remarks>
    /// A startup filter rather than a test-only endpoint, so the fault is injected into the real
    /// pipeline ahead of the real route and every layer between them runs as it does in production.
    /// </remarks>
    private sealed class BodyFaultFilter(string route, Exception failure) : IStartupFilter
    {

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {

                app.Use(async (context, proceed) =>
                {

                    if (context.Request.Path.StartsWithSegments(route, StringComparison.Ordinal))
                    {

                        context.Request.Body = new FaultingBody(
                            Encoding.UTF8.GetBytes("{\"key\":\"k"),
                            failure);

                    }

                    await proceed(context);

                });

                next(app);

            };

    }

    /// <summary>Yields a valid JSON prefix, then fails.</summary>
    private sealed class FaultingBody(byte[] prefix, Exception failure) : Stream
    {

        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _offset; set => throw new NotSupportedException(); }

        public override void Flush()
        {

        }

        public override int Read(byte[] buffer, int offset, int count)
        {

            if (_offset >= prefix.Length)
            {

                throw failure;

            }

            int take = Math.Min(count, prefix.Length - _offset);

            Array.Copy(prefix, _offset, buffer, offset, take);

            _offset += take;

            return take;

        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {

            if (_offset >= prefix.Length)
            {

                throw failure;

            }

            int take = Math.Min(buffer.Length, prefix.Length - _offset);

            prefix.AsSpan(_offset, take).CopyTo(buffer.Span);

            _offset += take;

            return ValueTask.FromResult(take);

        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    }

}
