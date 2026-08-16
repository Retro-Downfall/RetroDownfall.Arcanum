using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnIdempotencyAmbientTests
{

    [Fact]
    public void HasIdempotencyKey_CannotBeSetFromForgedPingRequestBody()
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

        // Ambient is the source of truth — forged body properties are ignored by the contract.
        Assert.False(TurnIdempotencyAmbient.Current);

        TurnExecutionRequest turnRequest = new(
            request,
            InvocationContexts.AttendedSession(),
            TurnResponseMode.Buffered,
            TurnPurpose.Interactive,
            HumanInteractionAvailable: false,
            HasIdempotencyKey: TurnIdempotencyAmbient.Current,
            AccountingHandle: null);

        Assert.False(turnRequest.HasIdempotencyKey);
    }

    [Fact]
    public void TurnIdempotencyAmbient_PublishAndClear_RoundTrips()
    {
        try
        {
            TurnIdempotencyAmbient.Publish(true);

            Assert.True(TurnIdempotencyAmbient.Current);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }

        Assert.False(TurnIdempotencyAmbient.Current);
    }

}
