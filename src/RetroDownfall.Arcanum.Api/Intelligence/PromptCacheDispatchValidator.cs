using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal enum PromptCacheDispatchStatus
{
    Current,
    OptimizationUnavailable,
    ReplanRequired,
}

/// <summary>One materialized candidate and its ordinary fallback. Never persisted or serialized.</summary>
internal sealed class PromptCachePreparedPrompt(
    PromptCacheClientIdentity identity,
    CanonicalCampaignContext campaign,
    PromptCacheAuthoritySnapshot authority,
    ResolvedModelTokenizationProfile tokenizer,
    SystemPromptDocument ordinary,
    SystemPromptDocument document,
    string scopeMarkerDigest,
    ContentSensitivity sensitivity)
{
    internal PromptCacheClientIdentity Identity { get; } = identity;

    internal CanonicalCampaignContext Campaign { get; } = campaign;

    internal PromptCacheAuthoritySnapshot Authority { get; } = authority;

    internal ResolvedModelTokenizationProfile Tokenizer { get; } = tokenizer;

    internal SystemPromptDocument Ordinary { get; } = ordinary;

    internal SystemPromptDocument Document { get; } = document;

    internal SystemPromptBuildResult Build { get; } = document.BuildResult();

    internal string ScopeMarkerDigest { get; } = scopeMarkerDigest;

    internal ContentSensitivity Sensitivity { get; } = sensitivity;

    internal bool Optimized => !ReferenceEquals(Ordinary, Document);

    internal PromptCachePreparedPrompt Rebuild(SystemPromptDocument ordinary) =>
        new(Identity, Campaign, Authority, Tokenizer, ordinary,
            Sensitivity == ContentSensitivity.None ? SystemPromptBuilder.BuildCacheLayout(ordinary, ScopeMarkerDigest) : ordinary,
            ScopeMarkerDigest, Sensitivity);

    internal bool CanBind(IReadOnlyList<ChatMessage> messages, ChatOptions options)
    {
        if (!HasEligibleOptions(options, Identity.ResolvedModel)
            || messages.Count == 0 || messages[0].GetType() != typeof(ChatMessage)
            || messages[0].Role != ChatRole.System || messages[0].AuthorName is not null
            || messages[0].MessageId is not null || messages[0].AdditionalProperties is { Count: > 0 }
            || messages[0].Contents is not [TextContent text] || text.GetType() != typeof(TextContent)
            || text.Text != Build.Prompt || text.AdditionalProperties is { Count: > 0 })
        {
            return false;
        }

        return messages.All(message => message.RawRepresentation is null
                && message.Contents.All(content => content.RawRepresentation is null
                    && content.GetType() is var type && (type == typeof(TextContent) || type == typeof(TextReasoningContent)
                        || type == typeof(FunctionCallContent) || type == typeof(FunctionResultContent)
                        || type == typeof(DataContent) || type == typeof(UriContent))))
            && PromptCacheDispatchBinding.Fingerprint(messages, options).Length > 0;
    }

    internal static bool HasEligibleOptions(ChatOptions options, string resolvedModel) =>
        (options.ModelId is null || options.ModelId == resolvedModel)
        && options.GetType() == typeof(ChatOptions) && options.RawRepresentationFactory is null
        && options.Instructions is null && options.ConversationId is null
        && (options.ResponseFormat is null || ReferenceEquals(options.ResponseFormat, ChatResponseFormat.Text))
        && options.AllowBackgroundResponses is not true && options.ContinuationToken is null
        // The pinned SDK's Boolean strict option can rewrite tool schemas before serialization.
        && !(options.Tools is { Count: > 0 } && options.AdditionalProperties is { } metadata
            && metadata.TryGetValue("strict", out object? strict) && strict is bool)
        && PromptCacheToolProvenance.AllCurrent(options.Tools);

    internal IPromptCacheDispatchBinding Bind(IReadOnlyList<ChatMessage> messages, ChatOptions options) =>
        new PromptCacheDispatchBinding(this, messages, options);

    internal PromptCachePlan CreatePlan(ChatOptions options, IModelTokenEstimator estimator)
    {
        ProviderSettings provider = Identity.Provider;

        if (!HasEligibleOptions(options, Identity.ResolvedModel))
        {
            return PromptCachePlan.NonCacheable(provider.Name, Identity.ResolvedModel, PromptCacheSemanticNamespace.Main,
                PromptCacheEligibility.NonCacheable, PromptCacheNonEligibilityReason.InvalidPlan);
        }

        PromptCachingProfile? profile = ProviderResolver.ResolvePromptCachingProfile(provider, Identity.ResolvedModel);

        PromptCachePrefixDecision decision = PromptCachePlanner.CreatePrefixDecision(Build, options, ScopeMarkerDigest);

        if (!Optimized || Sensitivity != ContentSensitivity.None || decision.Eligibility != PromptCacheEligibility.Eligible
            || profile is not { ControlMode: PromptCachingControlMode.Explicit, WireDialect: PromptCachingWireDialect.OpenAiPromptCacheRetention })
        {
            return PromptCachePlan.NonCacheable(provider.Name, Identity.ResolvedModel, PromptCacheSemanticNamespace.Main,
                PromptCacheEligibility.NonCacheable, Sensitivity != ContentSensitivity.None
                    ? PromptCacheNonEligibilityReason.SensitiveContent : PromptCacheNonEligibilityReason.ProviderManaged);
        }

        int last = Build.CacheSegments.TakeWhile(s => s.Utf16Start + s.Utf16Length <= decision.Utf16Length).Count() - 1;

        return new PromptCachePlan(
            "arcanum-pc-v2-" + PromptCacheDispatchBinding.Digest(decision.PrefixDigest, decision.ToolSchemaDigest, ScopeMarkerDigest),
            provider.Name, Identity.ResolvedModel, PromptCacheSemanticNamespace.Main, profile.Retention,
            [new PromptCacheBoundary(0, Math.Max(0, last))], decision.PrefixDigest, decision.ToolSchemaDigest,
            estimator.EstimateText(provider, Identity.ResolvedModel, Build.Prompt[..decision.Utf16Length]).TokenCount,
            PromptCacheEligibility.Eligible, PromptCacheNonEligibilityReason.None);
    }
}

internal sealed class PromptCacheDispatchBinding : IPromptCacheDispatchBinding
{
    internal PromptCacheDispatchBinding(PromptCachePreparedPrompt prepared, IReadOnlyList<ChatMessage> messages, ChatOptions options)
    {
        Prepared = prepared;

        PayloadDigest = prepared.CanBind(messages, options) ? Fingerprint(messages, options) : string.Empty;

        Tools = options.Tools?.ToArray() ?? [];

        RawOptionsFactory = options.RawRepresentationFactory;
    }

    internal PromptCachePreparedPrompt Prepared { get; }

    internal string PayloadDigest { get; }

    internal AITool[] Tools { get; }

    internal Delegate? RawOptionsFactory { get; }

    internal static string Fingerprint(IReadOnlyList<ChatMessage> messages, ChatOptions options)
    {
        string payload = ModelCallPayloadFingerprint.Compute(messages, options);

        return payload.Length == 0 ? string.Empty : Digest(payload, options.ModelId, options.Instructions,
            options.ConversationId, options.AllowBackgroundResponses?.ToString(), options.ContinuationToken?.ToString(),
            options.Temperature?.ToString(CultureInfo.InvariantCulture), options.TopP?.ToString(CultureInfo.InvariantCulture),
            options.TopK?.ToString(CultureInfo.InvariantCulture), options.FrequencyPenalty?.ToString(CultureInfo.InvariantCulture),
            options.PresencePenalty?.ToString(CultureInfo.InvariantCulture), options.Seed?.ToString(CultureInfo.InvariantCulture),
            options.AllowMultipleToolCalls?.ToString());
    }

    internal static string Digest(params string?[] fields)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> length = stackalloc byte[4];

        foreach (string? field in fields)
        {
            byte[] bytes = field is null ? [] : Encoding.UTF8.GetBytes(field);

            BinaryPrimitives.WriteInt32LittleEndian(length, field is null ? -1 : bytes.Length);

            hash.AppendData(length);

            hash.AppendData(bytes);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

/// <summary>Revalidates optional optimization evidence at the physical provider boundary.</summary>
public sealed class PromptCacheDispatchValidator(
    PromptCacheConfigurationEpoch configuration,
    IProviderApiKeyResolver credentials,
    IPromptCacheAuthoritySnapshotProvider authority,
    IModelTokenEstimator estimator,
    IServiceScopeFactory scopeFactory)
{
    internal ValueTask<PromptCachePreparedPrompt?> PrepareAsync(ChatClientLease lease, SystemPromptDocument ordinary,
        ChatOptions options, CanonicalCampaignContext? campaign, ContentSensitivity sensitivity, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (lease.PromptCacheIdentity is not { CredentialStamp: not null } identity || campaign is not { } scope
            || !PromptCachePreparedPrompt.HasEligibleOptions(options, identity.ResolvedModel))
        {
            return ValueTask.FromResult<PromptCachePreparedPrompt?>(null);
        }

        PromptCacheAuthoritySnapshot captured = authority.Capture();

        ResolvedModelTokenizationProfile tokenizer = estimator.ResolveEffectiveProfile(identity.Provider, identity.ResolvedModel);

        string provider = JsonSerializer.Serialize(identity.Provider, ConfigurationJsonContext.Default.ProviderSettings);

        string marker = PromptCacheDispatchBinding.Digest(PromptCachePolicyV2.Version, provider, identity.ResolvedModel,
            tokenizer.ToString(), identity.ConfigurationStamp.ToString(), identity.CredentialStamp.ToString(),
            scope.CampaignId?.ToString(), scope.CampaignAvailabilityGeneration?.ToString(CultureInfo.InvariantCulture),
            scope.PathIdentityPolicyVersion?.ToString(CultureInfo.InvariantCulture), scope.PathIdentityRevision?.ToString(CultureInfo.InvariantCulture),
            scope.RootIdentityDigest?.ToString(), captured.RuntimeAuthorityGeneration.ToString(CultureInfo.InvariantCulture),
            captured.Authority?.ToString(), captured.Availability.Generation.ToString(CultureInfo.InvariantCulture));

        SystemPromptDocument document = sensitivity == ContentSensitivity.None
            ? SystemPromptBuilder.BuildCacheLayout(ordinary, marker)
            : ordinary;

        return ValueTask.FromResult<PromptCachePreparedPrompt?>(new(identity, scope, captured, tokenizer, ordinary, document, marker, sensitivity));
    }

    internal async ValueTask<Result<PromptCacheDispatchStatus>> ValidateAsync(IChatClient actualClient, IList<ChatMessage> messages,
        ChatOptions options, ModelCallContext? context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (context?.PromptCacheBinding is null)
        {
            return PromptCacheDispatchStatus.OptimizationUnavailable;
        }

        if (context.PromptCacheBinding is not PromptCacheDispatchBinding binding)
        {
            return PromptCacheDispatchStatus.ReplanRequired;
        }

        PromptCachePreparedPrompt prepared = binding.Prepared;

        PromptCacheClientIdentity identity = prepared.Identity;

        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];

        bool PayloadCurrent() => prepared.CanBind(list, options) && binding.PayloadDigest.Length > 0
            && binding.PayloadDigest == PromptCacheDispatchBinding.Fingerprint(list, options)
            && ReferenceEquals(options.RawRepresentationFactory, binding.RawOptionsFactory)
            && binding.Tools.SequenceEqual(options.Tools ?? [], ReferenceEqualityComparer.Instance)
            && PromptCacheToolProvenance.AllCurrent(options.Tools)
            && messages.Count > 0 && messages[0].Role == ChatRole.System
            && string.Equals(messages[0].Text, prepared.Build.Prompt, StringComparison.Ordinal);

        if (!ReferenceEquals(actualClient, identity.Client) || context.Model != identity.ResolvedModel
            || context.Sensitivity != prepared.Sensitivity
            || JsonSerializer.Serialize(context.Provider, ConfigurationJsonContext.Default.ProviderSettings)
                != JsonSerializer.Serialize(identity.Provider, ConfigurationJsonContext.Default.ProviderSettings)
            || estimator.ResolveEffectiveProfile(context.Provider, context.Model) != prepared.Tokenizer
            || !PayloadCurrent())
        {
            return PromptCacheDispatchStatus.ReplanRequired;
        }

        if (context.PromptCachePlan is { Eligibility: PromptCacheEligibility.Eligible } supplied)
        {
            PromptCachePlan expected = prepared.CreatePlan(options, estimator);

            if (supplied.CacheKey != expected.CacheKey || supplied.StableSegmentDigest != expected.StableSegmentDigest
                || supplied.ToolSchemaDigest != expected.ToolSchemaDigest || supplied.Retention != expected.Retention
                || supplied.Provider != expected.Provider || supplied.Model != expected.Model || supplied.Namespace != expected.Namespace
                || supplied.Eligibility != expected.Eligibility || !supplied.Boundaries.SequenceEqual(expected.Boundaries))
            {
                return PromptCacheDispatchStatus.ReplanRequired;
            }
        }

        Result<PromptCacheDispatchStatus> campaign = await CampaignCurrentAsync(prepared.Campaign, ct).ConfigureAwait(false);

        if (campaign.IsFailure || campaign.Value == PromptCacheDispatchStatus.ReplanRequired)
        {
            return campaign;
        }

        ProviderCredentialCurrentness credential = identity.CredentialStamp is { } stamp
            ? await credentials.GetCurrentnessAsync(identity.Provider, stamp, ct).ConfigureAwait(false)
            : ProviderCredentialCurrentness.ConfirmedStale;

        if (credential is not (ProviderCredentialCurrentness.Current or ProviderCredentialCurrentness.Unavailable))
        {
            return PromptCacheDispatchStatus.ReplanRequired;
        }

        // Campaign registry/path metadata is owned independently of the Covenant runtime publication.
        // Credential validation may yield after the first observation, so repeat those owning reads.
        bool optimizationUnavailable = campaign.Value == PromptCacheDispatchStatus.OptimizationUnavailable
            || credential == ProviderCredentialCurrentness.Unavailable;

        campaign = await CampaignCurrentAsync(prepared.Campaign, ct).ConfigureAwait(false);

        if (campaign.IsFailure || campaign.Value == PromptCacheDispatchStatus.ReplanRequired)
        {
            return campaign;
        }

        ct.ThrowIfCancellationRequested();

        // Async cache-proof reads may yield. Recheck the materialized payload and the two composite
        // publications after the final await, immediately before returning to the physical sender.
        return configuration.IsCurrent(identity.ConfigurationStamp) && authority.Capture() == prepared.Authority && PayloadCurrent()
            ? optimizationUnavailable ? PromptCacheDispatchStatus.OptimizationUnavailable : campaign.Value
            : PromptCacheDispatchStatus.ReplanRequired;
    }

    private async ValueTask<Result<PromptCacheDispatchStatus>> CampaignCurrentAsync(CanonicalCampaignContext campaign, CancellationToken ct)
    {
        if (campaign.CampaignId is not { } campaignId)
        {
            return PromptCacheDispatchStatus.Current;
        }

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();

        ICampaignAvailabilityReader? campaigns = scope.ServiceProvider.GetService<ICampaignAvailabilityReader>();

        ICampaignPathIdentityReader? paths = scope.ServiceProvider.GetService<ICampaignPathIdentityReader>();

        if (campaigns is null || paths is null)
        {
            return PromptCacheDispatchStatus.OptimizationUnavailable;
        }

        var available = await campaigns.FindAvailabilityGenerationAsync(campaignId, ct).ConfigureAwait(false);

        if (available.IsFailure)
        {
            return available.Error;
        }

        var path = await paths.FindByCampaignAsync(campaignId, ct).ConfigureAwait(false);

        if (path.IsFailure)
        {
            return path.Error;
        }

        return available.Value == campaign.CampaignAvailabilityGeneration
            && path.Value?.Revision == campaign.PathIdentityRevision
            && path.Value?.PhysicalIdentityDigest == campaign.RootIdentityDigest
            && (path.Value is null || path.Value.CampaignId == campaignId
                && path.Value.PolicyVersion == campaign.PathIdentityPolicyVersion)
            ? PromptCacheDispatchStatus.Current : PromptCacheDispatchStatus.ReplanRequired;
    }

}
