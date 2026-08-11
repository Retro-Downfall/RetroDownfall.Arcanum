using System.Runtime.InteropServices;
using System.Text;

namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>
/// An unmanaged copy of a secret, sized and encoded for a platform credential API.
/// </summary>
/// <remarks>
/// The allocate-and-copy pair is one type rather than two statements at each call site because
/// separating them is exactly how a secret goes missing: an allocation whose copy never happened
/// still yields a valid pointer and a plausible length, so the platform API succeeds and persists
/// uninitialized heap. Owning both halves here also gives the secret a single place to be zeroed
/// before the block returns to the allocator.
/// <para>
/// Deliberately a class rather than a struct. A readonly struct cannot clear its own pointer, so a
/// second <see cref="Dispose"/> — from a copy, or from a <c>using</c> that runs after an explicit
/// release — would free the same block twice and corrupt the heap.
/// </para>
/// </remarks>
internal sealed class CredentialSecretBuffer : IDisposable
{

    private nint _pointer;

    private readonly int _byteCount;

    private CredentialSecretBuffer(nint pointer, int byteCount)
    {

        _pointer = pointer;

        _byteCount = byteCount;

    }

    /// <summary>The unmanaged block holding the encoded secret. <see cref="nint.Zero"/> once disposed.</summary>
    internal nint Pointer => _pointer;

    /// <summary>The number of encoded bytes, which is the length the platform API must be given.</summary>
    internal int ByteCount => _byteCount;

    /// <summary>Encodes <paramref name="secret"/> as UTF-16 and copies it into unmanaged memory.</summary>
    internal static CredentialSecretBuffer FromUtf16(string secret)
    {

        ArgumentNullException.ThrowIfNull(secret);

        byte[] encoded = Encoding.Unicode.GetBytes(secret);

        // A zero-length AllocHGlobal is legal but implementation-defined; one byte keeps the
        // pointer unambiguously readable for an empty secret without changing the reported size.
        nint pointer = Marshal.AllocHGlobal(encoded.Length == 0 ? 1 : encoded.Length);

        try
        {

            Marshal.Copy(encoded, 0, pointer, encoded.Length);

        }
        catch
        {

            Marshal.FreeHGlobal(pointer);

            throw;

        }
        finally
        {

            Array.Clear(encoded);

        }

        return new CredentialSecretBuffer(pointer, encoded.Length);

    }

    /// <summary>Overwrites the secret in place. Exposed so a test can observe the zeroing Dispose performs.</summary>
    internal void ZeroForTest() => Zero();

    public void Dispose()
    {

        nint pointer = _pointer;

        if (pointer == nint.Zero)
        {

            return;

        }

        Zero();

        _pointer = nint.Zero;

        Marshal.FreeHGlobal(pointer);

    }

    private void Zero()
    {

        if (_pointer == nint.Zero || _byteCount <= 0)
        {

            return;

        }

        unsafe
        {

            new Span<byte>((void*)_pointer, _byteCount).Clear();

        }

    }

}
