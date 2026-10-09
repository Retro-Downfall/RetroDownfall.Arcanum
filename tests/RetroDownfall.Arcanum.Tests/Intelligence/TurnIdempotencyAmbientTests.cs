using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class TurnIdempotencyAmbientTests
{
    [Fact]
    public void Accepted_opaque_HTTP_key_keeps_one_turn_identity_when_ownership_is_published()
    {
        string keyHash = new('a', 64);

        string fingerprint = new('b', 64);

        using CancellationTokenSource ownership = new();

        try
        {
            TurnIdempotencyAmbient.PublishIdentity(keyHash, fingerprint);

            TurnIdempotencyRequestIdentity? first = TurnIdempotencyAmbient.RequestIdentity;

            Assert.NotNull(first);

            Assert.NotEqual(Guid.Empty, first.ClientTurnId);

            TurnIdempotencyAmbient.Publish(ownership.Token);

            Assert.Equal(first, TurnIdempotencyAmbient.RequestIdentity);

            Assert.Equal(ownership.Token, TurnIdempotencyAmbient.OwnershipLostToken);

            TurnIdempotencyAmbient.Clear();

            TurnIdempotencyAmbient.PublishIdentity(keyHash, fingerprint);

            Assert.Equal(first, TurnIdempotencyAmbient.RequestIdentity);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }

        Assert.Null(TurnIdempotencyAmbient.RequestIdentity);
    }

    [Fact]
    public void Changed_accepted_body_changes_request_evidence_without_changing_the_client_turn_identity()
    {
        try
        {
            TurnIdempotencyAmbient.PublishIdentity(new string('a', 64), new string('b', 64));

            TurnIdempotencyRequestIdentity first = TurnIdempotencyAmbient.RequestIdentity!;

            Assert.NotNull(first);

            TurnIdempotencyAmbient.PublishIdentity(new string('a', 64), new string('c', 64));

            TurnIdempotencyRequestIdentity second = TurnIdempotencyAmbient.RequestIdentity!;

            Assert.NotNull(second);

            Assert.Equal(first.ClientTurnId, second.ClientTurnId);

            Assert.NotEqual(first.AcceptedBodyDigest, second.AcceptedBodyDigest);
        }
        finally
        {
            TurnIdempotencyAmbient.Clear();
        }
    }

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
