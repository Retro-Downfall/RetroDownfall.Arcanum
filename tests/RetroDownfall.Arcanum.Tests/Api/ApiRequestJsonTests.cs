using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;

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
