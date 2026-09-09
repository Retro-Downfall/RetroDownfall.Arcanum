using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Api.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class ArcanumPresenceProofProtocolTests
{
    private const string Authority = "http://localhost:5001";

    [Fact]
    public void Matching_key_nonce_and_authority_verify()
    {
        byte[] keyDigest = SHA256.HashData("server-key"u8);

        byte[] nonce = RandomNumberGenerator.GetBytes(
            ArcanumPresenceProofProtocol.NonceBytes);

        byte[] processCapability = RandomNumberGenerator.GetBytes(
            ArcanumProcessCapabilityService.TokenBytes);

        byte[] envelope = ArcanumPresenceProofProtocol.CreateCapabilityEnvelope(
            keyDigest,
            nonce,
            Authority,
            processCapability);

        byte[] proof = ArcanumPresenceProofProtocol.ComputeProof(
            keyDigest,
            nonce,
            Authority,
            envelope);

        Assert.True(
            ArcanumPresenceProofProtocol.VerifyProof(
                keyDigest,
                nonce,
                Authority,
                envelope,
                proof));

        Assert.True(
            ArcanumPresenceProofProtocol.TryDecryptCapabilityEnvelope(
                keyDigest,
                nonce,
                Authority,
                envelope,
                out byte[]? decrypted));

        Assert.Equal(processCapability, decrypted);
    }

    [Fact]
    public void Replayed_proof_does_not_verify_for_a_new_nonce()
    {
        byte[] keyDigest = SHA256.HashData("server-key"u8);

        byte[] firstNonce = RandomNumberGenerator.GetBytes(
            ArcanumPresenceProofProtocol.NonceBytes);

        byte[] secondNonce = RandomNumberGenerator.GetBytes(
            ArcanumPresenceProofProtocol.NonceBytes);

        byte[] envelope = ArcanumPresenceProofProtocol.CreateCapabilityEnvelope(
            keyDigest,
            firstNonce,
            Authority,
            RandomNumberGenerator.GetBytes(ArcanumProcessCapabilityService.TokenBytes));

        byte[] proof = ArcanumPresenceProofProtocol.ComputeProof(
            keyDigest,
            firstNonce,
            Authority,
            envelope);

        Assert.False(
            ArcanumPresenceProofProtocol.VerifyProof(
                keyDigest,
                secondNonce,
                Authority,
                envelope,
                proof));
    }

    [Fact]
    public void Proof_does_not_verify_for_a_different_key_or_authority()
    {
        byte[] expectedDigest = SHA256.HashData("expected-key"u8);

        byte[] wrongDigest = SHA256.HashData("wrong-key"u8);

        byte[] nonce = RandomNumberGenerator.GetBytes(
            ArcanumPresenceProofProtocol.NonceBytes);

        byte[] envelope = ArcanumPresenceProofProtocol.CreateCapabilityEnvelope(
            expectedDigest,
            nonce,
            Authority,
            RandomNumberGenerator.GetBytes(ArcanumProcessCapabilityService.TokenBytes));

        byte[] proof = ArcanumPresenceProofProtocol.ComputeProof(
            expectedDigest,
            nonce,
            Authority,
            envelope);

        Assert.False(
            ArcanumPresenceProofProtocol.VerifyProof(
                wrongDigest,
                nonce,
                Authority,
                envelope,
                proof));

        Assert.False(
            ArcanumPresenceProofProtocol.VerifyProof(
                expectedDigest,
                nonce,
                "https://localhost:5001",
                envelope,
                proof));
    }

    [Fact]
    public void Capability_ciphertext_is_bound_to_the_key_nonce_and_authority()
    {
        byte[] keyDigest = SHA256.HashData("server-key"u8);
        byte[] nonce = RandomNumberGenerator.GetBytes(
            ArcanumPresenceProofProtocol.NonceBytes);
        byte[] capability = RandomNumberGenerator.GetBytes(
            ArcanumProcessCapabilityService.TokenBytes);

        byte[] envelope = ArcanumPresenceProofProtocol.CreateCapabilityEnvelope(
            keyDigest,
            nonce,
            Authority,
            capability);

        envelope[20] ^= 0x01;

        Assert.False(
            ArcanumPresenceProofProtocol.TryDecryptCapabilityEnvelope(
                keyDigest,
                nonce,
                Authority,
                envelope,
                out _));

        Assert.False(
            ArcanumPresenceProofProtocol.TryDecryptCapabilityEnvelope(
                keyDigest,
                nonce,
                "https://localhost:5001",
                envelope,
                out _));
    }

    [Theory]
    [InlineData("http://localhost:5001/api/health", "http://localhost:5001")]
    [InlineData("HTTP://LOCALHOST:5001/api/health", "http://localhost:5001")]
    [InlineData("https://localhost/api/health", "https://localhost:443")]
    [InlineData("http://[::1]:5001/api/health", "http://[::1]:5001")]
    public void Authority_is_canonical_and_contains_no_path(
        string url,
        string expected)
    {
        bool success = ArcanumPresenceProofProtocol.TryCanonicalAuthority(
            new Uri(url),
            out string? authority);

        Assert.True(success);
        Assert.Equal(expected, authority);
    }

    [Theory]
    [InlineData("ftp://localhost:5001/api/health")]
    [InlineData("http://example.com:5001/api/health")]
    [InlineData("http://user:password@localhost:5001/api/health")]
    public void Non_loopback_or_credential_bearing_authority_is_rejected(string url)
    {
        Assert.False(
            ArcanumPresenceProofProtocol.TryCanonicalAuthority(
                new Uri(url),
                out _));
    }

    [Fact]
    public void Encoded_nonce_and_proof_have_exact_bounded_shapes()
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(
            ArcanumPresenceProofProtocol.NonceBytes);

        string encodedNonce = ArcanumPresenceProofProtocol.Encode(nonce);

        Assert.Equal(44, encodedNonce.Length);

        Assert.True(
            ArcanumPresenceProofProtocol.TryDecode(
                encodedNonce,
                ArcanumPresenceProofProtocol.NonceBytes,
                out byte[]? decoded));

        Assert.Equal(nonce, decoded);

        Assert.False(
            ArcanumPresenceProofProtocol.TryDecode(
                Convert.ToBase64String(Encoding.UTF8.GetBytes("short")),
                ArcanumPresenceProofProtocol.NonceBytes,
                out _));

        string canonicalProof = Convert.ToBase64String(new byte[32]);
        string noncanonicalProof = $"{canonicalProof[..^2]}B=";

        Assert.False(
            ArcanumPresenceProofProtocol.TryDecode(
                noncanonicalProof,
                ArcanumPresenceProofProtocol.ProofBytes,
                out _));
    }
}
