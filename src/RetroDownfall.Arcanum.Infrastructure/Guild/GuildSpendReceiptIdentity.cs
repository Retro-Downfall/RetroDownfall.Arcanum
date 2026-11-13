using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Domain-separated canonical request identity; the full digest is retained with every receipt.</summary>
internal static class GuildSpendReceiptIdentity
{
    internal static byte[] OperatorDigest(GuildCallerContext caller, GuildBudgetEffectRequest binding, GuildSpendCorrectionRequest request)
    {
        return OperatorDigest(caller.InstallationGeneration, caller.PrincipalId!.Value, binding, request);
    }

    internal static byte[] OperatorDigest(Guid generation, Guid principal, GuildBudgetEffectRequest binding, GuildSpendCorrectionRequest request)
    {
        GuildSpendAmountsRequest amounts = request.Amounts!;

        string canonical = string.Join("|", "guild-spend-operator-v1", Id(generation), Id(principal),
            Id(binding.QuestId), Id(binding.EffectId), Id(binding.DispatchId), Id(binding.AccountingAssociationId), Id(binding.SourceId),
            Id(request.IdempotencyKey), request.ExpectedRevision.ToString(CultureInfo.InvariantCulture), ((int)request.Origin!.Value).ToString(CultureInfo.InvariantCulture),
            amounts.InputTokens!.Value.ToString(CultureInfo.InvariantCulture), amounts.OutputTokens!.Value.ToString(CultureInfo.InvariantCulture),
            amounts.CachedTokens!.Value.ToString(CultureInfo.InvariantCulture), amounts.ReasoningTokens!.Value.ToString(CultureInfo.InvariantCulture),
            ExactUsdText.Format(amounts.Usd!.Value), request.EvidenceDigest!.ToUpperInvariant());

        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    internal static byte[] ProviderDigest(GuildSpendSourceCredential.VerifiedSource source, GuildSpendObservation observation)
    {
        GuildSpendAmountsRequest amounts = observation.Amounts;

        using MemoryStream bytes = new();

        using (BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("Arcanum.Guild.ProviderObservation.v1");

            source.WriteIdentity(writer);

            writer.Write(amounts.InputTokens!.Value);

            writer.Write(amounts.OutputTokens!.Value);

            writer.Write(amounts.CachedTokens!.Value);

            writer.Write(amounts.ReasoningTokens!.Value);

            writer.Write(amounts.Usd!.Value.ToString("G29", CultureInfo.InvariantCulture));

            writer.Write(observation.EvidenceDigest);
        }

        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
    }

    private static string Id(Guid value) => value.ToString("D").ToUpperInvariant();
}
