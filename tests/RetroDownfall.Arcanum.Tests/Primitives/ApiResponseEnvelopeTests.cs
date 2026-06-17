using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;

namespace RetroDownfall.Arcanum.Tests.Primitives;

public sealed class ApiResponseEnvelopeTests
{

    [Fact]
    public void FromResult_Failure_ProducesEnvelopeFields()
    {

        ApiResponse<SanctumConfig> envelope = ApiResponse<SanctumConfig>.FromResult(
            Result<SanctumConfig>.Failure(new Error("Campaign.NotFound", "Campaign was not found.")),
            traceId: "trace-404");

        Assert.False(envelope.IsSuccess);

        Assert.Null(envelope.Data);

        Assert.NotNull(envelope.Error);

        Assert.Equal("Campaign.NotFound", envelope.Error.Value.Code);

        Assert.Equal("trace-404", envelope.TraceId);

    }

    [Fact]
    public void Serialize_NotFoundCampaign_UsesSourceGeneratedContext()
    {

        ApiResponse<SanctumConfig> envelope = ApiResponse<SanctumConfig>.FromResult(
            Result<SanctumConfig>.Failure(new Error("Campaign.NotFound", "Campaign was not found.")),
            traceId: "trace-404");

        string json = JsonSerializer.Serialize(
            envelope,
            ArcanumJsonContext.Default.ApiResponseSanctumConfig);

        using JsonDocument doc = JsonDocument.Parse(json);

        JsonElement root = doc.RootElement;

        Assert.False(root.GetProperty("isSuccess").GetBoolean());

        Assert.Equal("Campaign.NotFound", root.GetProperty("error").GetProperty("code").GetString());

        Assert.Equal("trace-404", root.GetProperty("traceId").GetString());

    }

}
