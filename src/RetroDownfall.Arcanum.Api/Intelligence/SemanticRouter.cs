using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Result of a SemanticRouter preflight: the resolved spell (null when NONE / unmatched / no LLM
/// call) plus the entity strings the router extracted from the user prompt. Entities survive a
/// <c>NONE</c> spell so Lexicon retrieval can still run when the router fired but matched no spell.
/// </summary>
internal sealed record SemanticSpellRoutingResult(
    SpellMetadata? Spell,
    IReadOnlyList<string> Entities,
    UsageDetails? Usage = null);

internal static class SemanticRouter
{

    internal static async Task<SemanticSpellRoutingResult?> DetermineActiveSpellAsync(
        IChatClient client,
        string userPrompt,
        IReadOnlyList<SpellMetadata> availableSpells,
        TimeSpan preflightTimeout,
        int maxOutputTokens,
        float temperature,
        CancellationToken cancellationToken,
        ILogger? logger = null,
        IReadOnlyList<SpellMetadata>? candidates = null,
        IModelCallExecutor? modelCallExecutor = null)
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
            + "You must respond with a single valid JSON object containing exactly two keys: spellName and entities. "
            + "If a spell matches the user's intent, spellName must be the exact name of the spell; if no spell matches, spellName must be NONE. "
            + "entities must be a JSON array of short subject/noun strings (people, projects, APIs, or other named things) mentioned in the user request; use an empty array when none are present. "
            + "Do not wrap the JSON in markdown code fences. Do not include any other text.",
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
            IModelCallExecutor executor = modelCallExecutor ?? new ModelCallExecutor();

            // Auxiliary preflight — dedicated budget so routing does not consume the interactive
            // turn's model-call ceiling.
            TurnBudget auxBudget = new(maxModelCalls: 1);

            var callResult = await executor
                .ExecuteBufferedAsync(
                    client,
                    routerMessages,
                    routerOptions,
                    auxBudget,
                    ModelCallPurpose.SpellRouting,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            if (callResult.IsFailure)
            {
                logger?.LogWarning(
                    "SemanticRouter preflight failed ({ErrorCode}); continuing with no active spell.",
                    callResult.Error.Code);

                return null;
            }

            response = callResult.Value.Response;
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

        IReadOnlyList<string> entities = NormalizeEntities(parsed.Entities);

        string spellName = parsed.SpellName.Trim();

        if (string.IsNullOrEmpty(spellName) || spellName.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            return new SemanticSpellRoutingResult(null, entities, response.Usage);
        }

        foreach (SpellMetadata spell in offeredSpells)
        {
            if (spell.Name.Equals(spellName, StringComparison.OrdinalIgnoreCase))
            {
                return new SemanticSpellRoutingResult(spell, entities, response.Usage);
            }
        }

        // Unmatched spell name: no spell, but extracted entities still survive for Lexicon retrieval.
        return new SemanticSpellRoutingResult(null, entities, response.Usage);
    }

    internal static IReadOnlyList<string> NormalizeEntities(string[]? entities)
    {
        if (entities is null || entities.Length == 0)
        {
            return [];
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        List<string> result = new(LexiconLimits.MaxExtractedEntities);

        foreach (string entity in entities)
        {
            if (result.Count >= LexiconLimits.MaxExtractedEntities)
            {
                break;
            }

            string trimmed = entity?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Length > LexiconLimits.MaxNameLength)
            {
                trimmed = trimmed[..LexiconLimits.MaxNameLength];
            }

            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    internal static string StripMarkdownFences(string trimmed)
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
