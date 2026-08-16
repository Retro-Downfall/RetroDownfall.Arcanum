using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// The one content digest every derived artifact's label binds to.
/// </summary>
/// <remarks>
/// A label without a content digest could authorize any later value of the same artifact identity,
/// which is precisely how a stale label would come to describe bytes that are not there. Binding the
/// exact bytes means a replacement has to bring its own label or fail.
///
/// <para>Domain-separated and deliberately outside <see cref="CovenantPolicyV1Manifest"/>: this
/// digest names ordinary derived content, not a rendered or published Covenant artifact, and pinning
/// it into the frozen v1 policy set would make every future derived sink a policy change.</para>
/// </remarks>
public static class DerivedArtifactContentDigest
{

    private static readonly byte[] DomainLabel =
        Encoding.UTF8.GetBytes("Arcanum.DerivedArtifact.Content.v1\0");

    /// <summary>Digests one derived artifact's text. Null and empty are distinct inputs.</summary>
    public static CovenantDigest ForText(string? content) =>
        ForBytes(content is null ? default : Encoding.UTF8.GetBytes(content), content is not null);

    /// <summary>Digests one derived artifact's exact bytes.</summary>
    public static CovenantDigest ForBytes(ReadOnlySpan<byte> content) => ForBytes(content, present: true);

    private static CovenantDigest ForBytes(ReadOnlySpan<byte> content, bool present)
    {

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(DomainLabel);

        // The presence byte is what keeps a null artifact from digesting identically to an empty
        // one. "The summary was cleared" and "the summary is the empty string" are different facts,
        // and a label that could not tell them apart would verify against the wrong one.
        hash.AppendData([present ? (byte)1 : (byte)0]);

        hash.AppendData(content);

        return new CovenantDigest(hash.GetHashAndReset());

    }

}
