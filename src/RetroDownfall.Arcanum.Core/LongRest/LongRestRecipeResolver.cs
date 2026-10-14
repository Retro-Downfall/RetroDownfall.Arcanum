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

        LongRestRecipeIdentity recipe = new(1, 1, chat!.Name, chatModel, Destination(chat),
            embeddingProvider!.Name, embedding.Model!, Destination(embeddingProvider), embedding.Dimensions);

        return LongRestDiscoveryHash.Recipe(recipe).IsFailure
            ? Result<LongRestRecipeIdentity>.Failure(new Error(ErrorCodes.LongRest.InvalidRequest, "The configured Long Rest recipe exceeds its supported bounds."))
            : Result<LongRestRecipeIdentity>.Success(recipe);
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

        if (problems.Count == 0 && Resolve(settings).IsFailure)
        {
            problems.Add("the configured recipe exceeds its supported bounds");
        }

        return problems;
    }
}
