using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Middleware;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Api.Middleware;

public sealed class ArcanumExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_V1Path_WithResponseStarted_ReturnsFalse()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext(responseStarted: true);

        httpContext.Request.Path = "/v1/chat/completions";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.False(handled);
    }

    /// <summary>
    /// The <c>/v1</c> body readers answer a malformed body themselves, so a <see cref="JsonException"/> that
    /// reaches the handler there is the server's own and gets the OpenAI shape of the logged 500.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_JsonException_V1Path_IsTheOpenAiUnhandledError()
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/v1/chat/completions";

        httpContext.Request.ContentLength = 8;

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new JsonException("bad json"),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(
            ReadBody(httpContext),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Contains(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task TryHandleAsync_JsonException_ResponseStarted_ReturnsFalse()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext(responseStarted: true);

        httpContext.Request.Path = "/api/spells/execute";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new JsonException("bad json"),
            CancellationToken.None);

        Assert.False(handled);
    }

    /// <summary>
    /// A <see cref="JsonException"/> raised on a request with no body is the server's own: corrupt data it
    /// read or a payload it built, never the caller's body.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_JsonException_on_a_bodyless_GET_logs_error_and_returns_500()
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Method = HttpMethods.Get;

        httpContext.Request.Path = "/api/apprentices/00000000-0000-0000-0000-000000000000/chronicle";

        JsonException corruptRow = new("The persisted plan is not valid JSON.");

        bool handled = await handler.TryHandleAsync(httpContext, corruptRow, CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, corruptRow));

        ApiResponse<string>? body = JsonSerializer.Deserialize(
            ReadBody(httpContext),
            ArcanumJsonContext.Default.ApiResponseString);

        Assert.Equal(ErrorCodes.Hub.Unhandled, body?.Error?.Code);
    }

    [Fact]
    public async Task TryHandleAsync_JsonException_with_a_body_the_route_never_read_is_not_blamed_on_the_caller()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/spells/execute";

        httpContext.Request.ContentLength = 12;

        bool handled = await handler.TryHandleAsync(httpContext, new JsonException("server side"), CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_NonJsonException_V1Path_ReturnsOpenAiUnhandledError()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/v1/models";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.True(handled);
    }

    [Fact]
    public async Task TryHandleAsync_NonJsonException_NonV1Path_ReturnsInternalError()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/spells/execute";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(500, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_RequestAbortedOperationCanceled_ReturnsFalse()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        using CancellationTokenSource cts = new();

        cts.Cancel();

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.RequestAborted = cts.Token;

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new OperationCanceledException(),
            CancellationToken.None);

        Assert.False(handled);
    }

    /// <summary>
    /// A gate that closed under an in-flight request answers exactly as admission would have.
    /// </summary>
    /// <remarks>
    /// Admission refuses what arrives after stage one begins. A request admitted a moment earlier is
    /// drained, and while it drains it can still reach SQLite and be refused there — so without this
    /// arm the one window the refusal exists for produces a 500 instead, and the request path is
    /// written into an Error-level log on the way past.
    /// </remarks>
    [Fact]
    public async Task TryHandleAsync_MaintenanceRefusal_ApiPath_IsTheDocumentedServiceUnavailable()
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/sessions";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new GrimoireMaintenanceUnavailableException(),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);

        ApiResponse<string>? body = JsonSerializer.Deserialize(
            ReadBody(httpContext),
            ArcanumJsonContext.Default.ApiResponseString);

        Assert.NotNull(body);

        Assert.Equal(ErrorCodes.Grimoire.MaintenanceUnavailable, body.Error?.Code);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);

        Assert.DoesNotContain(
            logger.Entries,
            static entry => entry.Message.Contains("/api/sessions", StringComparison.Ordinal));

        Assert.All(logger.Entries, static entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task TryHandleAsync_MaintenanceRefusal_V1Path_IsTheOpenAiServiceUnavailable()
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/v1/chat/completions";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new GrimoireMaintenanceUnavailableException(),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(
            ReadBody(httpContext),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("service_unavailable", body.Error.Type);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task TryHandleAsync_MaintenanceRefusal_WithResponseStarted_RewritesNothing()
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext(responseStarted: true);

        httpContext.Request.Path = "/api/events/logs";

        httpContext.Response.StatusCode = StatusCodes.Status200OK;

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new GrimoireMaintenanceUnavailableException(),
            CancellationToken.None);

        // Handled, even though nothing could be written. Reporting otherwise returns the exception to
        // the framework's exception middleware, which logs it at Error with the request path.
        Assert.True(handled);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// A raw delete the labelled-artifact guard refused is answered with the guard's own error, mapped
    /// to the status its code carries, and logged as the expected refusal it is.
    /// </summary>
    [Theory]
    [InlineData(ErrorCodes.Covenant.ForbiddenAuthority, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorCodes.Covenant.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    public async Task TryHandleAsync_LabeledArtifactRefusal_AnswersTheMappedStatusWithTheGuardsError(string code, int status)
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/sessions/00000000-0000-0000-0000-000000000000/compact";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new LabeledArtifactRefusalException(new Error(code, "The guard refused the delete.")),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(status, httpContext.Response.StatusCode);

        ApiResponse<string>? body = JsonSerializer.Deserialize(
            ReadBody(httpContext),
            ArcanumJsonContext.Default.ApiResponseString);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(code, body.Error?.Code);

        Assert.Equal("The guard refused the delete.", body.Error?.Message);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);

        Assert.All(logger.Entries, static entry => Assert.Null(entry.Exception));
    }

    /// <summary>
    /// The OpenAI-compatible surface keeps its own error envelope: a refusal there is not given the
    /// native one.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_LabeledArtifactRefusal_V1Path_KeepsTheOpenAiUnhandledAnswer()
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/v1/chat/completions";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new LabeledArtifactRefusalException(new Error(ErrorCodes.Covenant.ForbiddenAuthority, "The guard refused the delete.")),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);

        Assert.Contains(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task TryHandleAsync_LabeledArtifactRefusal_WithResponseStarted_ReturnsFalse()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext(responseStarted: true);

        httpContext.Request.Path = "/api/saga/00000000-0000-0000-0000-000000000000";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new LabeledArtifactRefusalException(new Error(ErrorCodes.Covenant.Unavailable, "The guard refused the delete.")),
            CancellationToken.None);

        Assert.False(handled);
    }

    /// <summary>
    /// Every other exception keeps its Error-level line and its 500.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_UnexpectedException_StillLogsAtError()
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/spells/execute";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);

        Assert.Contains(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// A bound-body route has no pre-check of its own: the framework's reader raises
    /// <see cref="InvalidOperationException"/> for a JSON Content-Type whose charset it cannot decode, and
    /// that is the caller's mistake, answered 415.
    /// </summary>
    [Theory]
    [InlineData("/api/prompts", "windows-1252")]
    [InlineData("/api/prompts", "bogus")]
    [InlineData("/v1/files", "bogus")]
    public async Task TryHandleAsync_InvalidOperationException_for_a_charset_the_request_names_but_cannot_be_decoded_is_a_415(
        string path,
        string charset)
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = path;

        httpContext.Request.ContentType = $"application/json; charset={charset}";

        httpContext.Request.ContentLength = 2;

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException($"Unable to read the request as JSON because the request content type charset '{charset}' is not a known encoding."),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, httpContext.Response.StatusCode);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);

        Assert.DoesNotContain(ErrorCodes.Hub.Unhandled, ReadBody(httpContext), StringComparison.Ordinal);
    }

    /// <summary>
    /// The 415 is for that request shape only: an <see cref="InvalidOperationException"/> on a request whose
    /// charset decodes, or that sent no body, or that is not JSON-typed, is the server's own fault.
    /// </summary>
    [Theory]
    [InlineData("application/json; charset=utf-8", 2L)]
    [InlineData("application/json", 2L)]
    [InlineData("application/json; charset=bogus", 0L)]
    [InlineData("text/plain; charset=bogus", 2L)]
    public async Task TryHandleAsync_InvalidOperationException_on_any_other_request_is_still_the_logged_500(
        string contentType,
        long contentLength)
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/prompts";

        httpContext.Request.ContentType = contentType;

        httpContext.Request.ContentLength = contentLength;

        InvalidOperationException serverFault = new("JsonTypeInfo metadata for type 'X' was not provided.");

        bool handled = await handler.TryHandleAsync(httpContext, serverFault, CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, serverFault));
    }

    /// <summary>
    /// A request-level fault the framework detected while binding or reading a body is the client's, and is
    /// answered with the status the framework chose and the documented envelope, never a 500.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_BadHttpRequestException_413_ReturnsBodyTooLargeEnvelope()
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/prompts";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, httpContext.Response.StatusCode);

        ApiResponse<bool>? body = JsonSerializer.Deserialize(
            ReadBody(httpContext),
            ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Validation.BodyTooLarge, body.Error?.Code);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);

        Assert.All(logger.Entries, static entry => Assert.Null(entry.Exception));
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest, ErrorCodes.Validation.InvalidBody)]
    [InlineData(StatusCodes.Status408RequestTimeout, ErrorCodes.Validation.BodyReadTimeout)]
    [InlineData(StatusCodes.Status415UnsupportedMediaType, ErrorCodes.Validation.UnsupportedMediaType)]
    [InlineData(StatusCodes.Status431RequestHeaderFieldsTooLarge, ErrorCodes.Validation.RequestHeadersTooLarge)]
    public async Task TryHandleAsync_BadHttpRequestException_KeepsTheFrameworkStatusWithItsDocumentedCode(
        int status,
        string expectedCode)
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/spells/execute";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new BadHttpRequestException("framework wording is never echoed", status),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(status, httpContext.Response.StatusCode);

        ApiResponse<bool>? body = JsonSerializer.Deserialize(
            ReadBody(httpContext),
            ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.Equal(expectedCode, body.Error?.Code);

        Assert.DoesNotContain("framework wording", body.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_BadHttpRequestException_V1Path_ReturnsTheOpenAiPayloadTooLargeError()
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/v1/files";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, httpContext.Response.StatusCode);

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(
            ReadBody(httpContext),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("invalid_request_error", body.Error.Type);

        Assert.Equal("payload_too_large", body.Error.Code);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public async Task TryHandleAsync_BadHttpRequestException_ResponseStarted_ReturnsFalse()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext(responseStarted: true);

        httpContext.Request.Path = "/api/prompts";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge),
            CancellationToken.None);

        Assert.False(handled);
    }

    /// <summary>
    /// A route that read its body through <c>ApiRequestJson.ReadAsync</c> has already answered a malformed
    /// body itself, so a <see cref="JsonException"/> raised afterwards -- a corrupt row the POST loaded, a
    /// payload the server built -- is the server's own fault, logged and answered 500.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_JsonException_after_a_completed_body_read_is_the_servers_own_and_a_logged_500()
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/lore";

        httpContext.Request.ContentType = "application/json";

        byte[] payload = Encoding.UTF8.GetBytes("""{"prompt":"hello"}""");

        httpContext.Request.Body = new MemoryStream(payload);

        httpContext.Request.ContentLength = payload.Length;

        (PingRequest? request, IResult? error) = await ApiRequestJson.ReadAsync(
            httpContext,
            ArcanumJsonContext.Default.PingRequest,
            static context => ApiRequestJson.InvalidBodyResult(context, ApiRequestJson.MalformedJsonMessage),
            CancellationToken.None);

        Assert.Null(error);

        Assert.Equal("hello", request?.Prompt);

        JsonException corruptRow = new("A row the route loaded after reading its body is not valid JSON.");

        bool handled = await handler.TryHandleAsync(httpContext, corruptRow, CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, corruptRow));

        Assert.DoesNotContain(ApiRequestJson.MalformedJsonMessage, ReadBody(httpContext), StringComparison.Ordinal);
    }

    /// <summary>
    /// A request over HTTP/2 or HTTP/3 can stream its body with neither a <c>Content-Length</c> nor a
    /// <c>Transfer-Encoding</c>; the framework's own body detection is what says it carries one.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_InvalidOperationException_for_an_unreadable_charset_on_a_streamed_body_without_length_is_a_415()
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/prompts";

        httpContext.Request.Protocol = "HTTP/2";

        httpContext.Request.ContentType = "application/json; charset=bogus";

        httpContext.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature(canHaveBody: true));

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("Unable to read the request as JSON because the request content type charset 'bogus' is not a known encoding."),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, httpContext.Response.StatusCode);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// The framework's body detection saying there is no body outranks nothing else: such a request has
    /// nothing for the charset to have spoiled, so the exception is the server's own.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_InvalidOperationException_on_a_request_the_framework_says_has_no_body_is_the_logged_500()
    {
        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/prompts";

        httpContext.Request.ContentType = "application/json; charset=bogus";

        httpContext.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature(canHaveBody: false));

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);

        Assert.Contains(logger.Entries, static entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// The two refusals that derive from <see cref="InvalidOperationException"/> keep their own answers on
    /// a request that also happens to name an undecodable charset: the charset arm is for the reader's
    /// exception alone.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_MaintenanceRefusal_on_a_request_with_an_unreadable_charset_is_still_the_503()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/sessions";

        httpContext.Request.ContentType = "application/json; charset=bogus";

        httpContext.Request.ContentLength = 2;

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new GrimoireMaintenanceUnavailableException(),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);

        Assert.Contains(ErrorCodes.Grimoire.MaintenanceUnavailable, ReadBody(httpContext), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryHandleAsync_LabeledArtifactRefusal_on_a_request_with_an_unreadable_charset_is_still_the_guards_403()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/sessions/00000000-0000-0000-0000-000000000000/compact";

        httpContext.Request.ContentType = "application/json; charset=bogus";

        httpContext.Request.ContentLength = 2;

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new LabeledArtifactRefusalException(new Error(ErrorCodes.Covenant.ForbiddenAuthority, "The guard refused the delete.")),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status403Forbidden, httpContext.Response.StatusCode);

        Assert.Contains(ErrorCodes.Covenant.ForbiddenAuthority, ReadBody(httpContext), StringComparison.Ordinal);
    }

    /// <summary>
    /// A bound body that never arrived is a missing body, not a parameter the binder could not convert.
    /// </summary>
    [Theory]
    [InlineData("Required parameter \"PromptCreateRequest request\" was not provided from body.")]
    [InlineData("Implicit body inferred for parameter \"PromptCreateRequest request\" but no body was provided. Did you mean to use a Service instead?")]
    public async Task TryHandleAsync_BadHttpRequestException_for_a_missing_bound_body_says_the_body_is_required(string frameworkMessage)
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/prompts";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new BadHttpRequestException(frameworkMessage, StatusCodes.Status400BadRequest),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);

        ApiResponse<bool>? body = JsonSerializer.Deserialize(
            ReadBody(httpContext),
            ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.Equal(ErrorCodes.Validation.InvalidBody, body?.Error?.Code);

        Assert.Equal(ApiRequestJson.DefaultInvalidBodyMessage, body?.Error?.Message);
    }

    /// <summary>
    /// A query, route or header value the binder could not convert, or one that was required and absent,
    /// keeps the parameter wording.
    /// </summary>
    [Theory]
    [InlineData("Failed to bind parameter \"int limit\" from \"abc\".")]
    [InlineData("Required parameter \"string name\" was not provided from query string.")]
    [InlineData("Required parameter \"Guid id\" was not provided from route.")]
    public async Task TryHandleAsync_BadHttpRequestException_for_an_unbindable_parameter_keeps_the_parameter_wording(string frameworkMessage)
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/prompts";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new BadHttpRequestException(frameworkMessage, StatusCodes.Status400BadRequest),
            CancellationToken.None);

        Assert.True(handled);

        ApiResponse<bool>? body = JsonSerializer.Deserialize(
            ReadBody(httpContext),
            ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.Equal(ApiRequestJson.ParameterBindingFailedMessage, body?.Error?.Message);
    }

    private sealed class BodyDetectionFeature(bool canHaveBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody { get; } = canHaveBody;
    }

    private static string ReadBody(HttpContext httpContext)
    {
        MemoryStream body = (MemoryStream)httpContext.Features
            .GetRequiredFeature<IHttpResponseBodyFeature>()
            .Stream;

        return Encoding.UTF8.GetString(body.ToArray());
    }

    private sealed class RecordingLogger : ILogger<ArcanumExceptionHandler>
    {
        private readonly List<LogEntry> _entries = [];

        internal IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
            }
        }
    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private static DefaultHttpContext CreateHttpContext(bool responseStarted = false)
    {
        ServiceCollection services = new();

        services.AddRouting();

        services.AddLogging();

        ServiceProvider provider = services.BuildServiceProvider();

        MemoryStream body = new();

        TestResponseFeature responseFeature = new()
        {
            HasStarted = responseStarted,
            Body = body,
        };

        TestResponseBodyFeature bodyFeature = new(body, responseFeature);

        TestRequestFeature requestFeature = new();

        FeatureCollection features = new();

        features.Set<IHttpRequestFeature>(requestFeature);

        features.Set<IHttpResponseFeature>(responseFeature);

        features.Set<IHttpResponseBodyFeature>(bodyFeature);

        DefaultHttpContext httpContext = new(features);

        httpContext.RequestServices = provider;

        return httpContext;
    }

    private sealed class TestRequestFeature : IHttpRequestFeature
    {
        public string Protocol { get; set; } = "HTTP/1.1";

        public string Scheme { get; set; } = "http";

        public string Method { get; set; } = "POST";

        public string PathBase { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string QueryString { get; set; } = string.Empty;

        public string RawTarget { get; set; } = string.Empty;

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = new MemoryStream();
    }

    private sealed class TestResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = new MemoryStream();

        public bool HasStarted { get; set; }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }

    private sealed class TestResponseBodyFeature : IHttpResponseBodyFeature
    {
        private readonly Stream _stream;

        private readonly IHttpResponseFeature _responseFeature;

        private readonly PipeWriter _writer;

        public TestResponseBodyFeature(Stream stream, IHttpResponseFeature responseFeature)
        {
            _stream = stream;

            _responseFeature = responseFeature;

            _writer = PipeWriter.Create(stream);
        }

        public Stream Stream => _stream;

        // The same stream the feature reports, so a result that writes through BodyWriter — which
        // every Results.Json does — lands where a test that reads the body can see it.
        public PipeWriter Writer => _writer;

        public Task CompleteAsync() => Task.CompletedTask;

        public void DisableBuffering()
        {
        }

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
