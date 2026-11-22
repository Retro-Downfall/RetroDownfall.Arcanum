using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Saved provider observation facts bound to their authenticated historical source.</summary>
internal sealed class GuildSpendObservation
{
    private readonly GuildSpendSourceCredential.VerifiedSource _source;

    internal GuildSpendObservation(GuildSpendSourceCredential.VerifiedSource source, GuildSpendAmountsRequest amounts, string evidenceDigest)
    {
        _source = source;

        Amounts = amounts;

        EvidenceDigest = evidenceDigest;
    }

    internal GuildSpendAmountsRequest Amounts { get; }

    internal string EvidenceDigest { get; }

    internal void WriteSourceIdentity(BinaryWriter writer) => _source.WriteIdentity(writer);

    internal bool IsFrom(GuildSpendSourceCredential.VerifiedSource source)
    {
        static byte[] Identity(GuildSpendSourceCredential.VerifiedSource candidate)
        {
            using MemoryStream bytes = new();

            using (BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true)) candidate.WriteIdentity(writer);

            return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
        }

        return CryptographicOperations.FixedTimeEquals(Identity(_source), Identity(source));
    }
}
