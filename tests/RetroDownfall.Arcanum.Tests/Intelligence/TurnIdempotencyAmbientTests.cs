using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnIdempotencyAmbientTests
{
    [Fact]
    public void IdempotencyOwnership_CannotBeSetFromForgedPingRequestBody()
    {
        const string forgedJson = """
            {
              "prompt": "hello",
              "hasIdempotencyKey": true,
              "HasIdempotencyKey": true
            }
            """;

        PingRequest? request = JsonSerializer.Deserialize(
            forgedJson,
            ArcanumJsonContext.Default.PingRequest);

        Assert.NotNull(request);

        Assert.Equal("hello", request.Prompt);

        // The ambient is the only carrier: forged body properties are ignored by the contract.
        Assert.Null(typeof(PingRequest).GetProperty("HasIdempotencyKey"));

        Assert.False(TurnIdempotencyAmbient.OwnershipLostToken.CanBeCanceled);
    }

    [Fact]
    public void TurnIdempotencyAmbient_PublishAndClear_RoundTripsTheOwnershipToken()
    {
        using CancellationTokenSource ownershipLost = new();

        try
        {
            TurnIdempotencyAmbient.Publish(ownershipLost.Token);

            Assert.Equal(ownershipLost.Token, TurnIdempotencyAmbient.OwnershipLostToken);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }

        Assert.Equal(CancellationToken.None, TurnIdempotencyAmbient.OwnershipLostToken);
    }
}
