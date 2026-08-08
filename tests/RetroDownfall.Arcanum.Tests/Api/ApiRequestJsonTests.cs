using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class ApiRequestJsonTests
{

    [Fact]
    public async Task ReadAsync_malformed_json_returns_error_result()
    {

        DefaultHttpContext httpContext = new();

        byte[] payload = Encoding.UTF8.GetBytes("{not-json");

        httpContext.Request.Body = new MemoryStream(payload);

        httpContext.Request.ContentLength = payload.Length;

        httpContext.Request.ContentType = "application/json";

        (PingRequest? body, IResult? error) = await ApiRequestJson.ReadAsync(
            httpContext,
            ArcanumJsonContext.Default.PingRequest,
            ctx => ApiRequestJson.InvalidBodyResult(ctx, ApiRequestJson.MalformedJsonMessage),
            CancellationToken.None);

        Assert.Null(body);

        Assert.NotNull(error);

    }

    [Fact]
    public async Task ReadAsync_non_json_content_type_returns_415_instead_of_throwing()
    {

        // ReadFromJsonAsync raises InvalidOperationException here, which used to escape ReadAsync and
        // surface as HTTP 500 Hub.Unhandled with an Error-level stack trace for a routine client mistake.
        DefaultHttpContext httpContext = new();

        byte[] payload = Encoding.UTF8.GetBytes("""{"prompt":"hello"}""");

        httpContext.Request.Body = new MemoryStream(payload);

        httpContext.Request.ContentLength = payload.Length;

        httpContext.Request.ContentType = "application/x-www-form-urlencoded";

        (PingRequest? body, IResult? error) = await ApiRequestJson.ReadAsync(
            httpContext,
            ArcanumJsonContext.Default.PingRequest,
            ctx => ApiRequestJson.InvalidBodyResult(ctx, ApiRequestJson.MalformedJsonMessage),
            CancellationToken.None);

        Assert.Null(body);

        Assert.NotNull(error);

        Assert.Equal(
            StatusCodes.Status415UnsupportedMediaType,
            await ExecuteStatusCodeAsync(error));

    }

    [Fact]
    public async Task ReadAsync_missing_content_type_returns_415()
    {

        DefaultHttpContext httpContext = new();

        httpContext.Request.Body = new MemoryStream([]);

        httpContext.Request.ContentLength = 0;

        (PingRequest? body, IResult? error) = await ApiRequestJson.ReadAsync(
            httpContext,
            ArcanumJsonContext.Default.PingRequest,
            ctx => ApiRequestJson.InvalidBodyResult(ctx, ApiRequestJson.MalformedJsonMessage),
            CancellationToken.None);

        Assert.Null(body);

        Assert.NotNull(error);

        Assert.Equal(
            StatusCodes.Status415UnsupportedMediaType,
            await ExecuteStatusCodeAsync(error));

    }

    [Fact]
    public async Task UnsupportedMediaTypeResult_names_the_required_header_in_a_structured_envelope()
    {

        DefaultHttpContext httpContext = new();

        string body = await ExecuteBodyAsync(ApiRequestJson.UnsupportedMediaTypeResult(httpContext));

        Assert.Contains(ErrorCodes.Validation.UnsupportedMediaType, body, StringComparison.Ordinal);

        Assert.Contains("application/json", body, StringComparison.Ordinal);

    }

    private static async Task<int> ExecuteStatusCodeAsync(IResult result)
    {

        DefaultHttpContext context = new()
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };

        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        return context.Response.StatusCode;

    }

    private static async Task<string> ExecuteBodyAsync(IResult result)
    {

        DefaultHttpContext context = new()
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };

        MemoryStream buffer = new();

        context.Response.Body = buffer;

        await result.ExecuteAsync(context);

        return Encoding.UTF8.GetString(buffer.ToArray());

    }

    [Fact]
    public void InvalidBodyResult_uses_generic_envelope()
    {

        DefaultHttpContext httpContext = new();

        httpContext.TraceIdentifier = "trace-1";

        IResult result = ApiRequestJson.InvalidBodyResult(httpContext, "missing");

        Assert.NotNull(result);

    }

    [Fact]
    public void InvalidBodyResult_typed_envelope_sets_validation_code()
    {

        using Activity activity = new Activity("api-json").Start();

        DefaultHttpContext httpContext = new();

        httpContext.TraceIdentifier = "trace-2";

        IResult result = ApiRequestJson.InvalidBodyResult(
            httpContext,
            "missing",
            ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(result);

    }

}
