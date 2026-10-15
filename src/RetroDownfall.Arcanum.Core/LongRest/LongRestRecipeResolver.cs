using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.LongRest;

/// <summary>
/// The one place Long Rest turns configuration into provider choices and into its recipe. The runtime configuration epoch,
/// the configuration validator's warning, the doctor row and the health row all read these predicates, so the question
/// "is this recipe resolvable" has exactly one answer.
/// </summary>
public static class LongRestRecipeResolver
{
    /// <summary>
    /// The chat choice: an explicit <paramref name="model"/> when one is named (an admitted choice resolves exactly), else the
    /// fast model when it is set, else the default model. A fast model that is set but matches nothing fails; it never falls
    /// back to the default model.
    /// </summary>
    public static bool TryResolveChat(ArcanumSettings settings, string? model, out ProviderSettings? provider, out string resolvedModel)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? target = !string.IsNullOrWhiteSpace(model) ? model
            : string.IsNullOrWhiteSpace(settings.FastModel) ? null : settings.FastModel.Trim();

        return ProviderResolver.TryResolveProviderForModel(settings, target, out provider, out resolvedModel) && provider is not null;
    }

    /// <summary>The embedding choice: the configured provider, model and positive dimensions, all present.</summary>
    public static bool TryResolveEmbedding(ArcanumSettings settings, out ProviderSettings? provider, out EmbeddingSettings embedding)
    {
        ArgumentNullException.ThrowIfNull(settings);

        embedding = settings.ResolveEmbeddings();

        provider = null;

        return embedding.Dimensions > 0 && !string.IsNullOrWhiteSpace(embedding.Provider) && !string.IsNullOrWhiteSpace(embedding.Model)
            && ProviderResolver.TryResolveProviderByName(settings, embedding.Provider, out provider) && provider is not null;
    }

    /// <summary>Long Rest speaks only to an OpenAI-compatible provider at an absolute http or https endpoint.</summary>
    public static bool IsSupported(ProviderSettings provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return provider.Type == AiProviderKind.OpenAICompatible
            && Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out Uri? endpoint)
            && endpoint.Scheme is "https" or "http";
    }

    /// <summary>The content-free digest of where a provider is reached; it is what a disclosure receipt names.</summary>
    public static CovenantDigest Destination(ProviderSettings provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return new(SHA256.HashData(Encoding.UTF8.GetBytes("Arcanum.Covenant.ProviderDestinationIdentity.v1\0" + provider.Name + "|" + provider.Endpoint)));
    }

    /// <summary>
    /// The recipe these settings would run, without regard to whether the feature is on. Failure means the chat or embedding
    /// choice cannot be resolved or used, or the recipe exceeds its supported bounds.
    /// </summary>
    public static Result<LongRestRecipeIdentity> Resolve(ArcanumSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!TryResolveChat(settings, null, out ProviderSettings? chat, out string chatModel) || !IsSupported(chat!)
            || !TryResolveEmbedding(settings, out ProviderSettings? embeddingProvider, out EmbeddingSettings embedding) || !IsSupported(embeddingProvider!))
        {
            return Result<LongRestRecipeIdentity>.Failure(new Error(ErrorCodes.LongRest.Unavailable, "The exact Long Rest provider recipe is unavailable."));
        }

        ProviderSettings chatProvider = chat!;

        ProviderSettings embeddingHost = embeddingProvider!;

        // The recipe carries the rendered-request budget the chat provider's window allows, so what the planner admits is what the
        // model can take, and the summary bound the embedding provider's window allows, so what the model is asked for is what can be
        // embedded. A window too small for the smallest unit, or an embedding window too small for the smallest summary, is refused here.
        if (ChatWindowTooSmall(chatProvider) || EmbeddingWindowTooSmall(embeddingHost))
        {
            return Result<LongRestRecipeIdentity>.Failure(new Error(ErrorCodes.LongRest.ModelContextTooSmall,
                "The configured Long Rest provider windows are too small for the smallest recipe."));
        }

        LongRestRecipeIdentity recipe = new(1, 1, chatProvider.Name, chatModel, Destination(chatProvider),
            embeddingHost.Name, embedding.Model!, Destination(embeddingHost), embedding.Dimensions)
        {
            ProviderInputUtf8Bytes = LongRestPromptLayout.InputBudgetUtf8Bytes(chatProvider.ContextWindowLimit),
            SummaryUtf8Bytes = LongRestPromptLayout.SummaryBoundUtf8Bytes(embeddingHost.ContextWindowLimit),
        };

        return LongRestDiscoveryHash.Recipe(recipe).IsFailure
            ? Result<LongRestRecipeIdentity>.Failure(new Error(ErrorCodes.LongRest.InvalidRequest, "The configured Long Rest recipe exceeds its supported bounds."))
            : Result<LongRestRecipeIdentity>.Success(recipe);
    }

    // The chat window is clamped the way the context-budget check clamps it, so an unset or tiny window is too small.
    private static bool ChatWindowTooSmall(ProviderSettings provider) =>
        ArcanumSettingClamps.ContextWindowLimit(provider.ContextWindowLimit) < LongRestPromptLayout.MinimumChatWindowTokens;

    // The embedding admission skips a window of zero or less, so an unset window is not refused here either.
    private static bool EmbeddingWindowTooSmall(ProviderSettings provider) =>
        provider.ContextWindowLimit > 0 && ArcanumSettingClamps.ContextWindowLimit(provider.ContextWindowLimit) < LongRestPromptLayout.MinimumEmbeddingWindowTokens;

    /// <summary>The head every report of an enabled-but-unresolved recipe starts with.</summary>
    public const string UnresolvedHeadline = "LongRest: enabled but recipe unresolved";

    /// <summary>
    /// The one sentence every reader of an unresolved recipe (the configuration validator, the doctor row, the health row) gives.
    /// It states what the condition costs, exactly: Long Rest does not run, an ordinary turn and startup are unaffected, and a
    /// Session turn that must finish protected Long Rest maintenance first still ends with <see cref="ErrorCodes.LongRest.Unavailable"/>,
    /// because protected failures are fatal to the turn and a protected request needs the recipe.
    /// </summary>
    public static string Describe(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);

        return $"{UnresolvedHeadline}: {string.Join("; ", problems)}. Long Rest will not run until the recipe resolves. "
            + $"Until then a Session turn that reads Covenant-protected memory in a scope with protected Long Rest sources fails with {ErrorCodes.LongRest.Unavailable}, "
            + "because protected maintenance must finish before such a turn begins; every other turn proceeds.";
    }

    /// <summary>
    /// Content-free reasons the recipe is unresolved, naming configuration keys and model names but never a credential, an
    /// endpoint or any user content. Empty when the recipe resolves.
    /// </summary>
    public static IReadOnlyList<string> Problems(ArcanumSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        List<string> problems = [];

        string chatSource = string.IsNullOrWhiteSpace(settings.FastModel) ? "DefaultModel" : "FastModel";

        if (!TryResolveChat(settings, null, out ProviderSettings? chat, out _))
        {
            problems.Add($"no configured provider advertises the chat model named by {chatSource}");
        }
        else if (!IsSupported(chat!))
        {
            problems.Add($"the chat model named by {chatSource} is served by provider '{chat!.Name}', which is not an OpenAI-compatible provider with an absolute http or https endpoint");
        }

        if (!TryResolveEmbedding(settings, out ProviderSettings? embeddingProvider, out _))
        {
            problems.Add("Integrations:Embeddings needs a provider that is configured, a model, and dimensions above zero");
        }
        else if (!IsSupported(embeddingProvider!))
        {
            problems.Add($"the embedding provider '{embeddingProvider!.Name}' is not an OpenAI-compatible provider with an absolute http or https endpoint");
        }

        if (problems.Count == 0)
        {
            ProviderSettings chatProvider = chat!;

            ProviderSettings embeddingHost = embeddingProvider!;

            if (ChatWindowTooSmall(chatProvider))
            {
                problems.Add("the chat model named by " + chatSource + " is served by provider '" + chatProvider.Name + "', whose contextWindowLimit of "
                    + ArcanumSettingClamps.ContextWindowLimit(chatProvider.ContextWindowLimit).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " tokens is below the " + LongRestPromptLayout.MinimumChatWindowTokens.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " the smallest Long Rest request needs; raise that provider's contextWindowLimit or choose a model with a larger window");
            }

            if (EmbeddingWindowTooSmall(embeddingHost))
            {
                problems.Add("the embedding provider '" + embeddingHost.Name + "' has a contextWindowLimit of "
                    + ArcanumSettingClamps.ContextWindowLimit(embeddingHost.ContextWindowLimit).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " tokens, below the " + LongRestPromptLayout.MinimumEmbeddingWindowTokens.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " the smallest Long Rest summary bound (" + LongRestPromptLayout.MinimumSummaryUtf8Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " bytes) needs; raise its contextWindowLimit or choose an embedding provider with a larger window");
            }
        }

        if (problems.Count == 0 && Resolve(settings).IsFailure)
        {
            problems.Add("the configured recipe exceeds its supported bounds");
        }

        return problems;
    }
}
