using System.Globalization;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// Writes the shared external-retention disclosure to diagnostics before a destructive confirmation.
/// </summary>
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

        foreach (CovenantRetentionHelpTarget target in
                 CovenantExternalRetentionDisclosure.ResolveHelpTargets(settings.Value.Providers ?? []))
        {

            dispatcher.WriteDiagnostic(
                target.Provider.Length == 0
                    ? $"  Retention guidance: {target.Uri}"
                    : $"  Retention guidance ({target.Provider}): {target.Uri}");

        }

    }

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
