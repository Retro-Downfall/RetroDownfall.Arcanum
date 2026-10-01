using System.Globalization;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Memory;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Writes the shared external-retention disclosure to diagnostics before a destructive confirmation.
/// </summary>
/// <remarks>
/// Every line goes to the diagnostic stream in every mode. It is disclosure an operator reads before
/// answering, which the output contract classes with the question itself, and under <c>--json</c> a
/// payload write would land ahead of the one document a script parses.
///
/// <para>Help targets are resolved here, on the client, from the operator's own configured providers.
/// No provider name crosses the wire, so the only names this writer can print are ones the operator
/// typed into <c>arcanum.json</c>.</para>
/// </remarks>
internal sealed class CovenantExternalRetentionDisclosureWriter(
    IConsoleDispatcher dispatcher,
    IOptions<ArcanumSettings> settings)
{

    public void Write(DataRetentionCovenantInventory? covenant)
    {

        if (covenant is not { })
        {

            return;

        }

        dispatcher.WriteDiagnostic(CovenantExternalRetentionDisclosure.DestructiveOperationText);

        dispatcher.WriteDiagnostic(DescribeExposure(covenant));

        WriteHelpTargets();

    }

    /// <summary>
    /// Writes what one erase cannot revoke: the shared sentence, one line per external channel, then
    /// where to go and delete.
    /// </summary>
    /// <remarks>
    /// The channel and evidence phrases are a closed map, so a value the map does not know fails the
    /// command before the question is put rather than printing a guess. Two words are kept out of it on
    /// purpose. No phrase says "not disclosed": the evidence model has no such value, and absence of a
    /// record is not evidence of absence. And no phrase prints a count, because an erase binds and
    /// returns none.
    ///
    /// <para>The writer is told the evidence, not the store, so each phrase has to be true for every
    /// store that can report it. A receipt window is described without any bound on when the item was
    /// created: the Lexicon backup channel reports one whenever any backup was ever recorded, because an
    /// entry keeps no creation time, and "while this item existed" would claim a bound that store never
    /// measured. "Not recorded" likewise says no record shows a send, not that no records are kept: on
    /// the backup channel Arcanum keeps receipts, and found none that could have carried this.</para>
    /// </remarks>
    public void WriteErasure(MemoryErasureExternalExposureDto external)
    {

        ArgumentNullException.ThrowIfNull(external);

        dispatcher.WriteDiagnostic(CovenantExternalRetentionDisclosure.DestructiveOperationText);

        dispatcher.WriteDiagnostic(Revocation(external.Revocation));

        foreach (MemoryExternalExposureChannelDto channel in external.Channels)
        {

            dispatcher.WriteDiagnostic($"  {ChannelLabel(channel.Channel)}: {EvidencePhrase(channel.Evidence)}");

        }

        WriteHelpTargets();

    }

    /// <summary>
    /// Writes one help action per configured provider, then the operator guide.
    /// </summary>
    /// <remarks>
    /// The one place these lines are spelled. Data reset, factory reset, protected restore and the
    /// erase verbs all point an operator at the same documents, and a second copy of the loop is how
    /// one of them would come to label a target differently.
    /// </remarks>
    public void WriteHelpTargets()
    {

        foreach (CovenantRetentionHelpTarget target in
                 CovenantExternalRetentionDisclosure.ResolveHelpTargets(settings.Value.Providers ?? []))
        {

            dispatcher.WriteDiagnostic(
                target.Provider.Length == 0
                    ? $"  Retention guidance: {target.Uri}"
                    : $"  Retention guidance ({target.Provider}): {target.Uri}");

        }

    }

    private static string Revocation(MemoryExternalRevocation revocation) =>
        revocation switch
        {
            MemoryExternalRevocation.NotPerformed => "Arcanum does not revoke copies that already left this machine:",
            _ => throw new ArgumentOutOfRangeException(nameof(revocation), revocation, "No revocation statement exists for this value."),
        };

    private static string ChannelLabel(MemoryExternalChannel channel) =>
        channel switch
        {
            MemoryExternalChannel.InferenceProviderAuthorship => "Inference provider that wrote it",
            MemoryExternalChannel.InferenceProviderContext => "Inference providers that read it in a turn",
            MemoryExternalChannel.EmbeddingProvider => "Embedding provider",
            MemoryExternalChannel.EncryptedBackup => "Encrypted backups",
            MemoryExternalChannel.OtherExternal => "Other external copies",
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "No label exists for this external channel."),
        };

    private static string EvidencePhrase(MemoryExternalEvidence evidence) =>
        evidence switch
        {
            MemoryExternalEvidence.Known =>
                "known — this content was sent there at least once.",
            MemoryExternalEvidence.ReceiptWindow =>
                "possible — Arcanum recorded a send on this channel and cannot rule out that it carried this content.",
            MemoryExternalEvidence.NotRecorded =>
                "not recorded — Arcanum holds no record that this content was sent there, so a send cannot be ruled out.",
            MemoryExternalEvidence.NotApplicable =>
                "not applicable — this store never sends content on this channel.",
            _ => throw new ArgumentOutOfRangeException(nameof(evidence), evidence, "No phrase exists for this evidence value."),
        };

    private static string DescribeExposure(DataRetentionCovenantInventory covenant) =>
        covenant.PossibleDisclosures > 0
            ? "This installation's own receipts record "
                + (covenant.DisclosureCountKind is CovenantDisclosureCountKind.LowerBound
                    ? "at least "
                    : "exactly ")
                + covenant.PossibleDisclosures.ToString(CultureInfo.InvariantCulture)
                + (covenant.PossibleDisclosures == 1 ? " physical attempt" : " physical attempts")
                + " that could have carried protected content out of it. Nothing this reset does can "
                + "revoke any of them."
            : "This installation's own receipts record no nonrevocable disclosure leaving it.";

}
