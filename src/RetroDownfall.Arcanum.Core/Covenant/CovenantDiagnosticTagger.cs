using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// The one production path from Covenant content identity to a loggable correlation label.
/// </summary>
/// <remarks>
/// The input is always a validated <see cref="CovenantDigest"/> and never free text, so two callers
/// tagging the same entry always frame the same 32 bytes. The output is the first 128 bits of
/// HMAC-SHA-256 under an installation-private key (§10.12).
///
/// <para>Truncation to 128 bits is deliberate and sufficient: this value correlates log lines, it does
/// not authenticate anything. Publishing the full 256 bits would invite a reader to treat it as an
/// identity worth comparing against a content hash, which is exactly the confusion the keying
/// prevents.</para>
///
/// <para>The borrowed key is cleared before this method returns on every path, including the throwing
/// ones. A key that outlived one tag in a stack frame would be a copy nobody owns.</para>
/// </remarks>
public sealed class CovenantDiagnosticTagger(ICovenantDiagnosticKeySource keys) : ICovenantDiagnosticTagger
{

    private const int KeyBytes = 32;

    private readonly ICovenantDiagnosticKeySource _keys =
        keys ?? throw new ArgumentNullException(nameof(keys));

    /// <inheritdoc/>
    public uint KeyVersion
    {

        get
        {

            Span<byte> key = stackalloc byte[KeyBytes];

            try
            {

                return _keys.TryCopyDiagnosticKey(key, out uint version)
                    ? version
                    : 0;

            }
            finally
            {

                CryptographicOperations.ZeroMemory(key);

            }

        }

    }

    /// <inheritdoc/>
    public CovenantDiagnosticTag Create(CovenantDigest contentIdentityDigest)
    {

        if (!contentIdentityDigest.IsValid)
        {
            throw new ArgumentException(
                "A diagnostic tag requires a validated content-identity digest.",
                nameof(contentIdentityDigest));
        }

        Span<byte> key = stackalloc byte[KeyBytes];

        try
        {

            if (!_keys.TryCopyDiagnosticKey(key, out uint version) || version == 0)
            {
                throw new InvalidOperationException(
                    "Covenant diagnostic key material has not been established.");
            }

            Span<byte> mac = stackalloc byte[32];

            _ = HMACSHA256.HashData(key, contentIdentityDigest.Span, mac);

            return new CovenantDiagnosticTag(version, mac[..CovenantDiagnosticTag.TagBytes]);

        }
        finally
        {

            CryptographicOperations.ZeroMemory(key);

        }

    }

}
