using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Infrastructure.Workspace;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class SemanticRouter
{

    internal static async Task<ParsedSpell?> DetermineActiveSpellAsync(
        IChatClient client,
        string userPrompt,
        IReadOnlyList<ParsedSpell> availableSpells,
        TimeSpan preflightTimeout,
        int maxOutputTokens,
        float temperature,
        CancellationToken cancellationToken)
    {
        if (availableSpells.Count == 0)
        {
            return null;
        }

        string safeUser = userPrompt.Replace('\'', '`');

        var toolsList = new StringBuilder(128);

        for (int i = 0; i < availableSpells.Count; i++)
        {
            if (i > 0)
            {
                _ = toolsList.Append("; ");
            }

            ParsedSpell s = availableSpells[i];

            _ = toolsList.Append(s.Name).Append(": ").Append(s.Description);
        }

        string classificationPrompt = string.Format(
            CultureInfo.InvariantCulture,
            "You are an intent router. Match the user's request to the correct tool. Available tools: [{0}]. User request: '{1}'. Return ONLY the exact Name of the matching tool. If none match, return 'NONE'.",
            toolsList.ToString(),
            safeUser);

        var routerMessages = new List<MeAiChatMessage>
        {
            new MeAiChatMessage(ChatRole.User, classificationPrompt),
        };

        var routerOptions = new ChatOptions
        {
            MaxOutputTokens = maxOutputTokens,
            Temperature = temperature,
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeoutCts.CancelAfter(preflightTimeout);

        ChatResponse response;

        try
        {
            response = await client
                .GetResponseAsync(routerMessages, routerOptions, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }

        string normalized = NormalizeRouterReply(response.Text);

        if (normalized.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (ParsedSpell spell in availableSpells)
        {
            if (spell.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return spell;
            }
        }

        return null;
    }

    private static string NormalizeRouterReply(string? raw)
    {
        string trimmed = (raw ?? string.Empty).Trim();

        int lineBreak = trimmed.IndexOfAny(['\r', '\n']);

        if (lineBreak >= 0)
        {
            trimmed = trimmed[..lineBreak].Trim();
        }

        trimmed = trimmed.Trim().Trim('"', '\'');

        int space = trimmed.IndexOf(' ');

        if (space > 0)
        {
            trimmed = trimmed[..space];
        }

        return trimmed;
    }
}
