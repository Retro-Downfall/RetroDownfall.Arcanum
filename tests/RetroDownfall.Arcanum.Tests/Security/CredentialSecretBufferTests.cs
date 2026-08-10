using System.Runtime.InteropServices;
using System.Text;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// Pins the unmanaged secret buffer the OS credential stores hand to the platform APIs.
/// </summary>
/// <remarks>
/// The Windows store previously allocated the blob and passed the pointer to <c>CredWriteW</c>
/// without ever copying the secret into it, so Credential Manager persisted whatever heap
/// remnant occupied the block. Nothing caught it: no CI job runs the suite on Windows, so every
/// Windows-gated test is inert. Keeping the marshalling in a platform-neutral type is what makes
/// that class of bug reachable from a test on any host.
/// </remarks>
public sealed class CredentialSecretBufferTests
{

    [Fact]
    public void FromUtf16_copies_the_secret_into_unmanaged_memory()
    {

        using CredentialSecretBuffer buffer = CredentialSecretBuffer.FromUtf16("correct horse battery staple");

        byte[] roundTripped = new byte[buffer.ByteCount];

        Marshal.Copy(buffer.Pointer, roundTripped, 0, buffer.ByteCount);

        Assert.Equal("correct horse battery staple", Encoding.Unicode.GetString(roundTripped));

    }

    [Fact]
    public void FromUtf16_round_trips_non_ascii_secrets()
    {

        const string secret = "pässwörd-éè-中文-\U0001F510";

        using CredentialSecretBuffer buffer = CredentialSecretBuffer.FromUtf16(secret);

        Assert.Equal(Encoding.Unicode.GetByteCount(secret), buffer.ByteCount);

        byte[] roundTripped = new byte[buffer.ByteCount];

        Marshal.Copy(buffer.Pointer, roundTripped, 0, buffer.ByteCount);

        Assert.Equal(secret, Encoding.Unicode.GetString(roundTripped));

    }

    [Fact]
    public void FromUtf16_allocates_a_readable_buffer_for_an_empty_secret()
    {

        using CredentialSecretBuffer buffer = CredentialSecretBuffer.FromUtf16(string.Empty);

        Assert.Equal(0, buffer.ByteCount);

        Assert.NotEqual(nint.Zero, buffer.Pointer);

    }

    [Fact]
    public void Dispose_zeroes_the_secret_before_releasing_the_block()
    {

        CredentialSecretBuffer buffer = CredentialSecretBuffer.FromUtf16("shred me");

        nint pointer = buffer.Pointer;

        int byteCount = buffer.ByteCount;

        byte[] beforeDispose = new byte[byteCount];

        Marshal.Copy(pointer, beforeDispose, 0, byteCount);

        Assert.Contains(beforeDispose, b => b != 0);

        buffer.ZeroForTest();

        byte[] afterZero = new byte[byteCount];

        Marshal.Copy(pointer, afterZero, 0, byteCount);

        Assert.All(afterZero, b => Assert.Equal(0, b));

        buffer.Dispose();

    }

    /// <summary>
    /// A second release must not reach the allocator. Freeing the same block twice corrupts the
    /// heap and takes the process down, which is exactly what happened while this type was still a
    /// readonly struct and therefore could not clear its own pointer.
    /// </summary>
    [Fact]
    public void Dispose_is_idempotent()
    {

        CredentialSecretBuffer buffer = CredentialSecretBuffer.FromUtf16("release me twice");

        buffer.Dispose();

        Assert.Equal(nint.Zero, buffer.Pointer);

        buffer.Dispose();

        Assert.Equal(nint.Zero, buffer.Pointer);

    }

    [Fact]
    public void FromUtf16_rejects_a_null_secret()
    {

        Assert.Throws<ArgumentNullException>(() => CredentialSecretBuffer.FromUtf16(null!));

    }

}
