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
/// <para>The routes are driven through the real application because the helper is shared by sixteen
/// call sites, so this held across all of them rather than for one family. <c>/api/lore</c> is here
/// deliberately: its use of the helper long predates the Saga curation routes, which is what makes this
/// a repair of the helper rather than of one caller. <c>/api/config/validate</c> is here for the
/// opposite reason — it reads its body by hand and cannot call the helper at all.</para>
///
/// <para>Not every route in the Api reads its body through the helper. The ones that read JSON by hand
/// and catch only <c>JsonException</c> have the same hole <c>/api/config/validate</c> had, and this
/// suite does not speak for them; it speaks for the four routes it drives and the call sites the helper
/// serves.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class ApiRequestJsonBodyFaultTests
{

    [SkippableTheory]
    [InlineData("/api/lore")]
    [InlineData("/api/memory/saga/m-1/retire")]
    // Reads its body with JsonDocument.ParseAsync because it needs the raw tree, so it cannot route
    // through the helper at all, and went on answering 500 for these faults once the helper stopped.
    // It is here because a reader that cannot call the helper is exactly where a helper-only repair
    // fails to reach -- so this suite covers one of those beside the ones that do call it. It speaks
    // for the routes it drives and for the sixteen call sites the helper serves, not for every route
    // in the Api: others read JSON by hand, and a hand reader catching only JsonException has the
    // same hole this one had.
    [InlineData("/api/config/validate")]
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
    // Reads its body with JsonDocument.ParseAsync because it needs the raw tree, so it cannot route
    // through the helper at all, and went on answering 500 for these faults once the helper stopped.
    // It is here because a reader that cannot call the helper is exactly where a helper-only repair
    // fails to reach -- so this suite covers one of those beside the ones that do call it. It speaks
    // for the routes it drives and for the sixteen call sites the helper serves, not for every route
    // in the Api: others read JSON by hand, and a hand reader catching only JsonException has the
    // same hole this one had.
    [InlineData("/api/config/validate")]
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
    // Reads its body with JsonDocument.ParseAsync because it needs the raw tree, so it cannot route
    // through the helper at all, and went on answering 500 for these faults once the helper stopped.
    // It is here because a reader that cannot call the helper is exactly where a helper-only repair
    // fails to reach -- so this suite covers one of those beside the ones that do call it. It speaks
    // for the routes it drives and for the sixteen call sites the helper serves, not for every route
    // in the Api: others read JSON by hand, and a hand reader catching only JsonException has the
    // same hole this one had.
    [InlineData("/api/config/validate")]
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
    /// Trailers over the header ceiling are a 431, not a 400 wearing a 431's status.
    /// </summary>
    /// <remarks>
    /// Reachable while reading a chunked body, because trailers arrive after it and count against the
    /// same ceiling. Before this was named, the response carried Kestrel's 431 while the code said
    /// <c>Validation.InvalidBody</c>, which the mapper resolves to 400 — the one shape the helper's
    /// own remark promised could not happen.
    /// </remarks>
    [SkippableTheory]
    [InlineData("/api/lore")]
    [InlineData("/api/memory/saga/m-1/retire")]
    [InlineData("/api/config/validate")]
    public async Task Trailers_over_the_header_ceiling_are_named_as_such(string route)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using HttpResponseMessage response = await SendFaultingBodyAsync(
            route,
            new BadHttpRequestException(
                "Request headers too long",
                StatusCodes.Status431RequestHeaderFieldsTooLarge));

        Assert.Equal(HttpStatusCode.RequestHeaderFieldsTooLarge, response.StatusCode);

        await AssertEnvelopeAsync(
            response,
            ErrorCodes.Validation.RequestHeadersTooLarge,
            ApiRequestJson.RequestHeadersTooLargeMessage);

    }

    /// <summary>
    /// Each code resolves through the mapper to the very status the helper sends it with.
    /// </summary>
    /// <remarks>
    /// This derives the mapper side only — it asserts code-to-status against literal statuses. The other
    /// direction, that the helper actually sends each code with that status, is what the wire cases
    /// above assert. Neither alone closes the loop; together they do.
    /// </remarks>
    [Theory]
    [InlineData(ErrorCodes.Validation.InvalidBody, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorCodes.Validation.BodyTooLarge, StatusCodes.Status413PayloadTooLarge)]
    [InlineData(ErrorCodes.Validation.BodyReadTimeout, StatusCodes.Status408RequestTimeout)]
    [InlineData(ErrorCodes.Validation.RequestHeadersTooLarge, StatusCodes.Status431RequestHeaderFieldsTooLarge)]
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
