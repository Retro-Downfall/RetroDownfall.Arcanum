using System.Security.Cryptography;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Lexicon;

namespace RetroDownfall.Arcanum.Core.Memory;

/// <summary>
/// A private, disposable copy of the installation's erasure key, and the only way the digest grammar
/// is used with it.
/// </summary>
/// <remarks>
/// <para>The key bytes are copied in, so the caller's buffer can be cleared at once, and zeroed on
/// <see cref="Dispose"/>. After disposal no digest can be computed, but <see cref="KeyId"/> stays
/// readable: it names the key without revealing it, and a caller that captured the key's identity
/// before releasing the material may still need to compare it.</para>
///
/// <para><see cref="FromBytes"/> is public because Core internals are not visible to Infrastructure,
/// where the keyring that reads secure storage lives.</para>
/// </remarks>
public sealed class MemoryErasureKey : IDisposable
{
    private readonly byte[] _key;

    private readonly byte[] _keyId;

    private bool _disposed;

    private MemoryErasureKey(byte[] key)
    {
        _key = key;

        _keyId = MemoryErasureDigestGrammar.KeyId(key);
    }

    /// <summary>The first sixteen bytes of the key's labelled HMAC. Readable after disposal.</summary>
    public ReadOnlySpan<byte> KeyId => _keyId;

    /// <summary>Copies exactly <see cref="MemoryErasureDigestGrammar.KeyBytes"/> key bytes into a new key.</summary>
    public static MemoryErasureKey FromBytes(ReadOnlySpan<byte> key)
    {
        if (key.Length != MemoryErasureDigestGrammar.KeyBytes)
        {
            throw new ArgumentException(
                $"An erasure key is exactly {MemoryErasureDigestGrammar.KeyBytes} bytes.",
                nameof(key));
        }

        return new MemoryErasureKey(key.ToArray());
    }

    /// <summary>Whether evidence recorded under <paramref name="keyId"/> was recorded under this key.</summary>
    public bool HasKeyId(ReadOnlySpan<byte> keyId) =>
        CryptographicOperations.FixedTimeEquals(_keyId, keyId);

    public byte[] Fingerprint(MemoryErasureIdentity identity) =>
        MemoryErasureDigestGrammar.Fingerprint(Material, identity);

    public byte[] SagaRequest(Guid mutationId, string memoryId, Guid? expectedClaimVersionId) =>
        MemoryErasureDigestGrammar.SagaRequest(Material, mutationId, memoryId, expectedClaimVersionId);

    public byte[] LexiconRequest(Guid mutationId, LexiconCurationTarget target) =>
        MemoryErasureDigestGrammar.LexiconRequest(Material, mutationId, target);

    public byte[] CovenantRequest(Guid mutationId, CovenantErasePrepareRequest request) =>
        MemoryErasureDigestGrammar.CovenantRequest(Material, mutationId, request);

    public byte[] Subject(MemoryReviewStore store, string rowId) =>
        MemoryErasureDigestGrammar.Subject(Material, store, rowId);

    public byte[] Effect(MemoryErasureEffectFacts facts) =>
        MemoryErasureDigestGrammar.Effect(Material, facts);

    public byte[] SagaContentBinding(string memoryId, string content) =>
        MemoryErasureDigestGrammar.SagaContentBinding(Material, memoryId, content);

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);

        _disposed = true;
    }

    private ReadOnlySpan<byte> Material
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return _key;
        }
    }
}
