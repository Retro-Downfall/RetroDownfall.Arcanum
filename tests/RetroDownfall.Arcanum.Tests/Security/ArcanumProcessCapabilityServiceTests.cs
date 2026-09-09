using System.Security.Cryptography;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class ArcanumProcessCapabilityServiceTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    [Fact]
    public void Issued_capability_has_one_canonical_fixed_size_shape_and_validates()
    {
        FakeTimeProvider time = new();
        byte[] signingKey = RandomNumberGenerator.GetBytes(32);

        using ArcanumProcessCapabilityService service = new(
            time,
            signingKey,
            Lifetime);

        CryptographicOperations.ZeroMemory(signingKey);
        byte[] capability = service.Issue();

        try
        {
            string encoded = ArcanumPresenceProofProtocol.Encode(capability);

            Assert.Equal(108, encoded.Length);
            Assert.True(service.IsValidEncoded(encoded));
            Assert.True(
                ArcanumPresenceProofProtocol.TryReadCapabilityValidityWindow(
                    capability,
                    out DateTimeOffset issuedAt,
                    out DateTimeOffset expiresAt));

            Assert.Equal(
                time.GetUtcNow().ToUnixTimeSeconds(),
                issuedAt.ToUnixTimeSeconds());

            Assert.Equal(Lifetime, expiresAt - issuedAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(capability);
        }
    }

    [Fact]
    public void Validity_window_reader_rejects_noncanonical_token_shapes()
    {
        FakeTimeProvider time = new();

        using ArcanumProcessCapabilityService service = new(
            time,
            RandomNumberGenerator.GetBytes(32),
            Lifetime);

        byte[] capability = service.Issue();

        try
        {
            capability[0] ^= 0x01;

            Assert.False(
                ArcanumPresenceProofProtocol.TryReadCapabilityValidityWindow(
                    capability,
                    out _,
                    out _));

            Assert.False(
                ArcanumPresenceProofProtocol.TryReadCapabilityValidityWindow(
                    capability.AsSpan(1),
                    out _,
                    out _));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(capability);
        }
    }

    [Fact]
    public void Tampered_expired_or_noncanonical_capability_is_rejected()
    {
        FakeTimeProvider time = new();

        using ArcanumProcessCapabilityService service = new(
            time,
            RandomNumberGenerator.GetBytes(32),
            Lifetime);

        byte[] capability = service.Issue();

        try
        {
            capability[20] ^= 0x80;
            Assert.False(service.IsValidEncoded(
                ArcanumPresenceProofProtocol.Encode(capability)));

            capability[20] ^= 0x80;
            string encoded = ArcanumPresenceProofProtocol.Encode(capability);
            Assert.False(service.IsValidEncoded($" {encoded[1..]}"));

            time.Advance(Lifetime);
            Assert.False(service.IsValidEncoded(encoded));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(capability);
        }
    }

    [Fact]
    public void Server_restart_invalidates_every_capability_from_the_previous_process()
    {
        FakeTimeProvider time = new();

        using ArcanumProcessCapabilityService first = new(
            time,
            RandomNumberGenerator.GetBytes(32),
            Lifetime);

        using ArcanumProcessCapabilityService restarted = new(
            time,
            RandomNumberGenerator.GetBytes(32),
            Lifetime);

        byte[] capability = first.Issue();

        try
        {
            string encoded = ArcanumPresenceProofProtocol.Encode(capability);

            Assert.True(first.IsValidEncoded(encoded));
            Assert.False(restarted.IsValidEncoded(encoded));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(capability);
        }
    }

    [Fact]
    public void Forward_wall_clock_jump_cannot_prematurely_expire_a_capability()
    {
        FakeTimeProvider time = new();

        using ArcanumProcessCapabilityService service = new(
            time,
            RandomNumberGenerator.GetBytes(32),
            Lifetime);

        byte[] capability = service.Issue();

        try
        {
            string encoded = ArcanumPresenceProofProtocol.Encode(capability);

            time.SetUtcNow(time.GetUtcNow() + TimeSpan.FromDays(30));

            Assert.True(service.IsValidEncoded(encoded));

            time.Advance(Lifetime);

            Assert.False(service.IsValidEncoded(encoded));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(capability);
        }
    }

    [Fact]
    public void Backward_wall_clock_jump_cannot_extend_a_capability_beyond_its_elapsed_lifetime()
    {
        FakeTimeProvider time = new();

        using ArcanumProcessCapabilityService service = new(
            time,
            RandomNumberGenerator.GetBytes(32),
            Lifetime);

        byte[] capability = service.Issue();

        try
        {
            string encoded = ArcanumPresenceProofProtocol.Encode(capability);
            DateTimeOffset issuedAt = time.GetUtcNow();

            time.Advance(TimeSpan.FromMinutes(2));
            time.SetUtcNow(issuedAt + TimeSpan.FromMinutes(1));
            time.Advance(TimeSpan.FromMinutes(3));

            Assert.False(service.IsValidEncoded(encoded));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(capability);
        }
    }
}
