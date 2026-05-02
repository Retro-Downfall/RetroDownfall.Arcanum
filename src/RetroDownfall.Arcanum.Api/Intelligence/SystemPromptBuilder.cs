using System.Text;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Workspace;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class SystemPromptBuilder
{
    private const string BasePersona = "You are an autonomous developer assistant running as a local background daemon. You have access to local system tools and must use them when necessary to fulfill the operator's request.";

    internal static string Build(PingRequest request, string? codexContent, ParsedSpell? activeSpell = null)
    {
        var sb = new StringBuilder(512);

        sb.AppendLine(BasePersona);

        if (request.ContextSnapshot is { } snapshot)
        {
            sb.AppendLine();

            sb.AppendLine("### Workspace Context");

            sb.AppendLine();

            sb.Append("Domain: ");

            sb.AppendLine(snapshot.Domain.ToString());

            sb.Append("RootPath: ");

            sb.AppendLine(snapshot.RootPath);

            sb.AppendLine();

            sb.AppendLine("### Table of Contents");

            sb.AppendLine();

            foreach (string thread in snapshot.Threads)
            {
                if (string.IsNullOrWhiteSpace(thread))
                {
                    continue;
                }

                sb.Append("- ");

                sb.AppendLine(thread);
            }
        }

        if (!string.IsNullOrWhiteSpace(codexContent))
        {
            sb.AppendLine();

            sb.AppendLine("### Master Codex (CODEX.md)");

            sb.AppendLine();

            sb.Append(codexContent);
        }

        if (activeSpell is not null)
        {
            sb.AppendLine();

            sb.AppendLine($"### Active Operational Spell ({activeSpell.Name})");

            sb.AppendLine();

            sb.Append(activeSpell.FullContent);
        }

        return sb.ToString();
    }
}
