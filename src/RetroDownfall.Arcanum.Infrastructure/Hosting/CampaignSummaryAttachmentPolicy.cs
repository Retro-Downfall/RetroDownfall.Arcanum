using System.Globalization;
using System.Text;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

internal static class CampaignSummaryAttachmentPolicy
{

    public static string BuildConsultedReferences(
        IReadOnlyList<AttachmentMemoryProvenance> consultations)
    {

        if (consultations.Count == 0)
        {

            return string.Empty;

        }

        StringBuilder builder = new();

        builder.AppendLine("## Consulted Attachments (metadata only)");

        builder.AppendLine();

        builder.AppendLine(
            "Use these references to preserve decisions and source identity. Do not reconstruct or archive attachment content.");

        foreach (AttachmentMemoryProvenance source in consultations
            .DistinctBy(
                static item => (item.AttachmentId, item.Version)))
        {

            builder.Append("- logical-key=");

            builder.Append(Harden(source.LogicalKey));

            builder.Append(" version=");

            builder.Append(source.Version.ToString(CultureInfo.InvariantCulture));

            builder.Append(" attachment-id=");

            builder.Append(source.AttachmentId.ToString());

            builder.Append(" source-type=");

            builder.AppendLine(Harden(source.SourceType));

        }

        return builder.ToString().TrimEnd();

    }

    private static string Harden(string value) =>
        value.Replace('\r', '_').Replace('\n', '_').Trim();

}
