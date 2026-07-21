using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using MeAiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Fallback Lexicon entity extraction for inference turns that did not run the SemanticRouter (or
/// ran it with no entities) — notably <c>OverrideSpellName</c>, pure embedding spell routing
/// (DirectResonance), and no-spell user-facing turns. Runs a single low-token JSON preflight on the
/// supplied <see cref="IChatClient"/> (the caller resolves the fast model when configured) with
/// <c>SemanticRouterPreflightTimeoutSeconds</c> and no tools. Failures degrade to an empty entity
/// list — Lexicon retrieval is best-effort and never fails the inference turn.
/// </summary>
internal static class LexiconEntityExtractor
{

    /// <summary>Low internal token cap for the extractor preflight (kept separate from the router).</summary>
    public const int MaxOutputTokens = 128;

    public static async Task<(IReadOnlyList<string> Entities, UsageDetails? Usage)> ExtractAsync(
        IChatClient client,
        string userPrompt,
        TimeSpan preflightTimeout,
        CancellationToken cancellationToken,
        ILogger? logger = null,
        IModelCallExecutor? modelCallExecutor = null)
    {

        if (string.IsNullOrWhiteSpace(userPrompt))
        {
            return ([], null);
        }

        string safeUser = userPrompt.Replace('\'', '`');

        string extractionPrompt = string.Format(
            CultureInfo.InvariantCulture,
            "Extract the named entities (people, projects, APIs, or other named subjects) mentioned in the user request. "
            + "Respond with a single valid JSON object containing exactly one key, entities, whose value is a JSON array of short subject/noun strings. "
            + "Use an empty array when no named entities are present. Do not wrap the JSON in markdown code fences. Do not include any other text. "
            + "User request: '{0}'",
            safeUser);

        var messages = new List<MeAiChatMessage>
        {
            new MeAiChatMessage(ChatRole.User, extractionPrompt),
        };

        var options = new ChatOptions
        {
            MaxOutputTokens = MaxOutputTokens,
            Temperature = 0f,
            ResponseFormat = ChatResponseFormat.Json,
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeoutCts.CancelAfter(preflightTimeout);

        ChatResponse response;

        try
        {
            IModelCallExecutor executor = modelCallExecutor ?? new ModelCallExecutor();

            // Auxiliary preflight — dedicated budget so Lexicon extraction does not consume the
            // interactive turn's model-call ceiling.
            TurnBudget auxBudget = new(maxModelCalls: 1);

            var callResult = await executor
                .ExecuteBufferedAsync(
                    client,
                    messages,
                    options,
                    auxBudget,
                    ModelCallPurpose.LexiconExtraction,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            if (callResult.IsFailure)
            {
                logger?.LogWarning(
                    "LexiconEntityExtractor preflight failed ({ErrorCode}); continuing with no entities.",
                    callResult.Error.Code);

                return ([], null);
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
                "LexiconEntityExtractor preflight timed out after {TimeoutSeconds:F0}s; continuing with no entities.",
                preflightTimeout.TotalSeconds);

            return ([], null);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "LexiconEntityExtractor preflight failed ({ExceptionType}); continuing with no entities.",
                ex.GetType().Name);

            return ([], null);
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            return ([], response.Usage);
        }

        string cleaned = SemanticRouter.StripMarkdownFences(response.Text.Trim());

        LexiconEntityExtractionResponse? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize(cleaned, ArcanumJsonContext.Default.LexiconEntityExtractionResponse);
        }
        catch (JsonException)
        {
            logger?.LogWarning("LexiconEntityExtractor failed to parse JSON response: {ResponseText}", response.Text);

            return ([], response.Usage);
        }

        return (SemanticRouter.NormalizeEntities(parsed?.Entities), response.Usage);
    }

}
