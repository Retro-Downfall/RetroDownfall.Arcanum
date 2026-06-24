using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Api.Streaming;

public sealed class SseConnectionResultsTests
{

    [Fact]
    public void TooManyConnections_returns_503_json_envelope()
    {

        DefaultHttpContext httpContext = new();

        httpContext.TraceIdentifier = "trace-123";

        IResult result = SseConnectionResults.TooManyConnections(httpContext);

        JsonHttpResult<ApiResponse<bool>> json = Assert.IsType<JsonHttpResult<ApiResponse<bool>>>(result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, json.StatusCode);

    }

    [Fact]
    public void TooManyConnections_uses_activity_trace_id_when_present()
    {

        using Activity activity = new Activity("sse-test").Start();

        DefaultHttpContext httpContext = new();

        httpContext.TraceIdentifier = "trace-fallback";

        IResult result = SseConnectionResults.TooManyConnections(httpContext);

        JsonHttpResult<ApiResponse<bool>> json = Assert.IsType<JsonHttpResult<ApiResponse<bool>>>(result);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, json.StatusCode);

        Assert.Equal("Api.TooManyConnections", json.Value!.Error!.Value.Code);

    }

}
