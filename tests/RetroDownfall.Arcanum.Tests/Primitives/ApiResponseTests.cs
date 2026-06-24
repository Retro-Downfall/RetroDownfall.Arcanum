using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Primitives;

public sealed class ApiResponseTests
{

    [Fact]
    public void FromResult_Success_ProducesDataEnvelope()
    {

        Result<string> result = Result<string>.Success("payload");

        ApiResponse<string> envelope = ApiResponse<string>.FromResult(result, traceId: "trace-1");

        Assert.True(envelope.IsSuccess);

        Assert.Equal("payload", envelope.Data);

        Assert.Null(envelope.Error);

        Assert.Equal("trace-1", envelope.TraceId);

    }

    [Fact]
    public void FromResult_Failure_ProducesErrorEnvelope()
    {

        Error error = new("Test.Failed", "Request failed.");

        Result<string> result = Result<string>.Failure(error);

        ApiResponse<string> envelope = ApiResponse<string>.FromResult(result, traceId: "trace-2");

        Assert.False(envelope.IsSuccess);

        Assert.Null(envelope.Data);

        Assert.NotNull(envelope.Error);

        Assert.Equal(error, envelope.Error.Value);

        Assert.Equal("trace-2", envelope.TraceId);

    }

    [Fact]
    public void FromResult_WithoutTraceId_LeavesTraceIdNull()
    {

        Result<int> result = Result<int>.Success(7);

        ApiResponse<int> envelope = ApiResponse<int>.FromResult(result);

        Assert.True(envelope.IsSuccess);

        Assert.Equal(7, envelope.Data);

        Assert.Null(envelope.TraceId);

    }

}
