using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using RetroDownfall.Arcanum.Api.Streaming;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

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
        Assert.False(json.Value!.IsSuccess);
        Assert.Equal("trace-123", json.Value.TraceId);
        Assert.Equal(ErrorCodes.Api.TooManyConnections, json.Value.Error!.Value.Code);
        Assert.Equal(
            "The server has reached the maximum number of concurrent SSE connections.",
            json.Value.Error.Value.Message);

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

        Assert.Equal(activity.Id, json.Value!.TraceId);
        Assert.Equal(ErrorCodes.Api.TooManyConnections, json.Value.Error!.Value.Code);

    }

    [Fact]
    public void FromDenial_Global_UsesGlobalConnectionMessage()
    {

        DefaultHttpContext httpContext = new();
        httpContext.TraceIdentifier = "trace-global";
        SseConnectionDenial denial = new(SseDenialReason.Global, "ignored", 99);

        IResult result = SseConnectionResults.FromDenial(httpContext, denial);

        JsonHttpResult<ApiResponse<bool>> json = Assert.IsType<JsonHttpResult<ApiResponse<bool>>>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, json.StatusCode);
        Assert.False(json.Value!.IsSuccess);
        Assert.Equal("trace-global", json.Value.TraceId);
        Assert.Equal(ErrorCodes.Api.TooManyConnections, json.Value.Error!.Value.Code);
        Assert.Equal(
            "The server has reached the maximum number of concurrent SSE connections.",
            json.Value.Error.Value.Message);

    }

    [Fact]
    public void FromDenial_PerType_IncludesEventTypeAndLimit()
    {

        DefaultHttpContext httpContext = new();
        httpContext.TraceIdentifier = "trace-type";
        SseConnectionDenial denial = new(SseDenialReason.PerType, "session.updated", 3);

        IResult result = SseConnectionResults.FromDenial(httpContext, denial);

        JsonHttpResult<ApiResponse<bool>> json = Assert.IsType<JsonHttpResult<ApiResponse<bool>>>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, json.StatusCode);
        Assert.False(json.Value!.IsSuccess);
        Assert.Equal("trace-type", json.Value.TraceId);
        Assert.Equal(ErrorCodes.Api.TooManyConnections, json.Value.Error!.Value.Code);
        Assert.Equal(
            "Too many connections for event type 'session.updated' (limit: 3)",
            json.Value.Error.Value.Message);

    }

    [Fact]
    public void TooManyConnections_PerType_UsesActivityTraceIdWhenPresent()
    {

        using Activity activity = new Activity("sse-type-test").Start();
        DefaultHttpContext httpContext = new();
        httpContext.TraceIdentifier = "trace-fallback";

        IResult result = SseConnectionResults.TooManyConnections(httpContext, "apprentice.progress", 1);

        JsonHttpResult<ApiResponse<bool>> json = Assert.IsType<JsonHttpResult<ApiResponse<bool>>>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, json.StatusCode);
        Assert.Equal(activity.Id, json.Value!.TraceId);
        Assert.Equal(
            "Too many connections for event type 'apprentice.progress' (limit: 1)",
            json.Value.Error!.Value.Message);

    }

}
