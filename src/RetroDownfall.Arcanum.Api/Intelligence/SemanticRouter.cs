using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class SemanticRouter
{

    internal static async Task<SpellMetadata?> DetermineActiveSpellAsync(
        IChatClient client,
        string userPrompt,
        IReadOnlyList<SpellMetadata> availableSpells,
        TimeSpan preflightTimeout,
        int maxOutputTokens,
        float temperature,
        CancellationToken cancellationToken,
        ILogger? logger = null,
        IReadOnlyList<SpellMetadata>? candidates = null)
    {
        if (availableSpells.Count == 0)
        {
            return null;
        }

        // RAG Phase 5 — when a caller (SemanticSpellRouter, in hybrid mode) supplies a pre-filtered
        // candidate list, only those spells are offered to the LLM (reduced context), and the
        // response is resolved against that same offered set below — never the full availableSpells
        // list. A hallucinated or otherwise out-of-set spell name (one the model saw in its own
        // training data or general knowledge, not from this prompt's offered list) must not resolve
        // to a real spell just because it happens to exist somewhere in the broader catalog; doing
        // so would silently defeat the whole point of the top-K filter. A null candidates list (the
        // default, used by every existing caller) means "unchanged behavior": the full catalog is
        // both offered and resolved against.
        IReadOnlyList<SpellMetadata> offeredSpells = candidates ?? availableSpells;

        if (offeredSpells.Count == 0)
        {
            return null;
        }

        string safeUser = userPrompt.Replace('\'', '`');

        var toolsList = new StringBuilder(128);

        for (int i = 0; i < offeredSpells.Count; i++)
        {
            if (i > 0)
            {
                _ = toolsList.Append("; ");
            }

            SpellMetadata s = offeredSpells[i];

            _ = toolsList.Append(s.Name).Append(": ").Append(s.Description);
        }

        string classificationPrompt = string.Format(
            CultureInfo.InvariantCulture,
            "You are an intent router. Match the user's request to the correct tool. Available tools: [{0}]. User request: '{1}'. "
            + "You must respond with a single valid JSON object containing exactly one key: spellName. If a spell matches the user's intent, the value must be the exact name of the spell. If no spell matches, the value must be NONE. Do not wrap the JSON in markdown code fences. Do not include any other text.",
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
            ResponseFormat = ChatResponseFormat.Json,
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
            logger?.LogInformation(
                "SemanticRouter preflight timed out after {TimeoutSeconds:F0}s; continuing with no active spell.",
                preflightTimeout.TotalSeconds);

            return null;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "SemanticRouter preflight failed ({ExceptionType}); continuing with no active spell.",
                ex.GetType().Name);

            return null;
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            return null;
        }

        string trimmed = response.Text.Trim();

        string cleaned = StripMarkdownFences(trimmed);

        SemanticSpellResponse? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize(cleaned, ArcanumJsonContext.Default.SemanticSpellResponse);
        }
        catch (JsonException)
        {
            string logSnippet = trimmed.Length > 200 ? trimmed[..200] : trimmed;

            logger?.LogWarning("SemanticRouter failed to parse JSON response: {ResponseText}", logSnippet);

            return null;
        }

        if (parsed is null)
        {
            return null;
        }

        string spellName = parsed.SpellName.Trim();

        if (string.IsNullOrEmpty(spellName))
        {
            return null;
        }

        if (spellName.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (SpellMetadata spell in offeredSpells)
        {
            if (spell.Name.Equals(spellName, StringComparison.OrdinalIgnoreCase))
            {
                return spell;
            }
        }

        return null;
    }

    private static string StripMarkdownFences(string trimmed)
    {
        if (trimmed.Length < 3 || !trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        ReadOnlySpan<char> afterOpen = trimmed.AsSpan(3).TrimStart();

        if (afterOpen.StartsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            afterOpen = afterOpen[4..].TrimStart();
        }

        ReadOnlySpan<char> content = afterOpen;

        int close = content.LastIndexOf("```".AsSpan(), StringComparison.Ordinal);

        if (close >= 0)
        {
            content = content[..close].TrimEnd();
        }

        return content.ToString();
    }
}
