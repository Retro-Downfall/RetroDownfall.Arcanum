using Microsoft.Extensions.AI;
using OpenAI.Chat;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Applies the verified OpenAI Chat Completions prompt-cache root contract to a provider-bound
/// <see cref="ChatOptions"/> clone.
/// </summary>
internal static class PromptCachingChatOptionsAdapter
{
    internal static void Apply(
        ChatOptions options,
        PromptCachingProfile profile,
        PromptCachePlan plan)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Eligibility != PromptCacheEligibility.Eligible
            || profile.ControlMode != PromptCachingControlMode.Explicit
            || profile.WireDialect != PromptCachingWireDialect.OpenAiPromptCacheRetention)
        {
            return;
        }

        Func<IChatClient, object?>? previousFactory = options.RawRepresentationFactory;

        options.RawRepresentationFactory = client =>
        {
            object? previous = previousFactory?.Invoke(client);
            ChatCompletionOptions rawOptions = previous switch
            {
                null => new ChatCompletionOptions(),
                ChatCompletionOptions typed => typed,
                _ => throw new InvalidOperationException(
                    $"Prompt caching cannot compose with raw chat options type '{previous.GetType().FullName}'."),
            };

            ApplyVerifiedFields(rawOptions, profile, plan);

            return rawOptions;
        };
    }

#pragma warning disable SCME0001 // JsonPatch is OpenAI 2.12's code-owned additional-property API.
    private static void ApplyVerifiedFields(
        ChatCompletionOptions options,
        PromptCachingProfile profile,
        PromptCachePlan plan)
    {
        if (profile.CacheKeysSupported
            && profile.EmitCacheKey
            && !string.IsNullOrEmpty(plan.CacheKey))
        {
            options.Patch.Set("$.prompt_cache_key"u8, plan.CacheKey);
        }

        string? retention = plan.Retention switch
        {
            PromptCacheRetentionPolicy.ProviderDefault => null,
            PromptCacheRetentionPolicy.InMemory => "in_memory",
            PromptCacheRetentionPolicy.TwentyFourHours => "24h",
            PromptCacheRetentionPolicy.ThirtyMinutes => throw new InvalidOperationException(
                "Thirty-minute retention requires the deferred explicit-breakpoint dialect."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(plan),
                plan.Retention,
                "Unknown prompt-cache retention policy."),
        };

        if (profile.RetentionSelectionSupported && retention is not null)
        {
            options.Patch.Set("$.prompt_cache_retention"u8, retention);
        }
    }
#pragma warning restore SCME0001
}
