using System.Text;

using RetroDownfall.Arcanum.Core.Chronosync;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Infrastructure.Workspace;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class SystemPromptBuilder
{
    private const string BasePersona = "You are an autonomous developer assistant running as a local background daemon. You have access to local system tools and must use them when necessary to fulfill the operator's request.";

    internal static string Build(
        PingRequest request,
        string? codexContent,
        ParsedSpell? activeSpell = null,
        List<AttachedFileDto>? attachedFiles = null,
        string? campaignSummary = null)
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

        AppendChronosyncTemporalDelta(sb, request);

        if (!string.IsNullOrWhiteSpace(codexContent))
        {
            sb.AppendLine();

            sb.AppendLine("### Master Codex (CODEX.md)");

            sb.AppendLine();

            sb.Append(codexContent);
        }

        if (!string.IsNullOrWhiteSpace(campaignSummary))
        {
            sb.AppendLine();

            sb.AppendLine("### Campaign Summary (compressed context)");

            sb.AppendLine();

            sb.AppendLine(
                "The following is a summary of earlier conversation history that has been compressed to fit within the context window. Treat it as reliable prior context:");

            sb.AppendLine();

            sb.Append(campaignSummary.Trim());
        }

        if (activeSpell is not null)
        {
            sb.AppendLine();

            sb.AppendLine($"### Active Operational Spell ({activeSpell.Name})");

            sb.AppendLine();

            sb.Append(activeSpell.FullContent);

            if (activeSpell.AvailableScripts.Count > 0)
            {
                sb.AppendLine();

                sb.AppendLine();

                sb.AppendLine("### Available Spell Scripts");

                sb.AppendLine();

                foreach (string scriptName in activeSpell.AvailableScripts)
                {
                    sb.Append("- ");

                    sb.AppendLine(scriptName);
                }

                sb.AppendLine();

                sb.AppendLine(
                    "You may run these scripts only via the run_spell_script tool: pass script_name (file name only) and optional arguments.");
            }
        }

        if (attachedFiles is { Count: > 0 })
        {
            sb.AppendLine();

            sb.AppendLine("### Attached Files for this Turn");

            sb.AppendLine();

            foreach (AttachedFileDto attachedFile in attachedFiles)
            {
                sb.Append("#### ");

                sb.AppendLine(attachedFile.RelativePath);

                sb.AppendLine();

                sb.AppendLine("```");

                sb.Append(attachedFile.Content);

                sb.AppendLine();

                sb.AppendLine("```");

                sb.AppendLine();
            }
        }

        if (request.CliTerminalFormatting)
        {
            sb.AppendLine();

            sb.AppendLine("### Output Formatting Directive");

            sb.AppendLine();

            sb.Append("Output Formatting Directive: You are communicating via a raw CLI terminal. You must format your responses for readability in this environment. You are strictly permitted to use ONLY the following Markdown elements: Headings, Bold text, Italic text, and Code Blocks. Strictly avoid tables, blockquotes, inline HTML, or complex nested lists.");
        }

        return sb.ToString();
    }

    private static void AppendChronosyncTemporalDelta(StringBuilder sb, PingRequest request)
    {
        ChronosyncReport? delta = request.ChronosyncDelta;

        if (delta is null || delta.PreviousSnapshotTime is null)
        {
            return;
        }

        if (delta.NewThreads.Length == 0 && delta.MissingThreads.Length == 0 && !delta.DomainChanged)
        {
            return;
        }

        sb.AppendLine();

        sb.AppendLine("### Chronosync Report (Temporal Delta)");

        sb.AppendLine();

        if (delta.DomainChanged)
        {
            if (delta.PreviousDomain is { } prevDomain && request.ContextSnapshot is { } snap)
            {
                sb.Append("The workspace domain has shifted from ");

                sb.Append(prevDomain.ToString());

                sb.Append(" to ");

                sb.Append(snap.Domain.ToString());

                sb.AppendLine(".");
            }
            else
            {
                sb.AppendLine("The workspace domain classification has changed since your last session.");
            }

            sb.AppendLine();
        }

        if (delta.NewThreads.Length > 0)
        {
            sb.AppendLine("New threads (added since last sync):");

            sb.AppendLine();

            foreach (string thread in delta.NewThreads)
            {
                if (string.IsNullOrWhiteSpace(thread))
                {
                    continue;
                }

                sb.Append("- ");

                sb.AppendLine(thread);
            }

            sb.AppendLine();
        }

        if (delta.MissingThreads.Length > 0)
        {
            sb.AppendLine("Missing threads (removed since last sync):");

            sb.AppendLine();

            foreach (string thread in delta.MissingThreads)
            {
                if (string.IsNullOrWhiteSpace(thread))
                {
                    continue;
                }

                sb.Append("- ");

                sb.AppendLine(thread);
            }

            sb.AppendLine();
        }
    }
}
