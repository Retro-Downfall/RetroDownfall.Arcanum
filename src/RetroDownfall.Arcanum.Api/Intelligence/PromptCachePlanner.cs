using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

internal static class PromptCachePlanner
{
    private const string CacheKeyPrefix = "arcanum-pc-v1-";

    public static PromptCachePlan Create(
        ProviderSettings provider,
        string model,
        SystemPromptDocument document,
        ChatOptions options,
        PromptCacheSemanticNamespace semanticNamespace,
        int eligiblePrefixTokenEstimate)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        PromptCachingProfile? profile =
            ModelCapabilityCatalog.ResolvePromptCaching(provider, model);

        if (profile is null)
        {
            return PromptCachePlan.NonCacheable(
                provider.Name,
                model,
                semanticNamespace,
                PromptCacheEligibility.ProviderManaged,
                PromptCacheNonEligibilityReason.ProfileAbsent);
        }

        if (profile.ControlMode == PromptCachingControlMode.ProviderManaged)
        {
            return PromptCachePlan.NonCacheable(
                provider.Name,
                model,
                semanticNamespace,
                PromptCacheEligibility.ProviderManaged,
                PromptCacheNonEligibilityReason.ProviderManaged);
        }

        if (profile.ControlMode == PromptCachingControlMode.None)
        {
            return PromptCachePlan.NonCacheable(
                provider.Name,
                model,
                semanticNamespace,
                PromptCacheEligibility.NonCacheable,
                PromptCacheNonEligibilityReason.DisabledByProfile);
        }

        if (profile.ControlMode != PromptCachingControlMode.Explicit
            || profile.WireDialect != PromptCachingWireDialect.OpenAiPromptCacheRetention)
        {
            return PromptCachePlan.NonCacheable(
                provider.Name,
                model,
                semanticNamespace,
                PromptCacheEligibility.NonCacheable,
                PromptCacheNonEligibilityReason.UnsupportedDialect);
        }

        if (document.OrderedSegments.Any(static segment => segment.Sensitive
            || IsProtectedKind(segment.Kind)
            || segment.ResolvedParts.Any(static part => SystemPromptAttributionMap.IsCovenant(part.Attribution))))
        {
            return PromptCachePlan.NonCacheable(
                provider.Name,
                model,
                semanticNamespace,
                PromptCacheEligibility.NonCacheable,
                PromptCacheNonEligibilityReason.SensitiveContent);
        }

        int lastEligibleSegment = -1;

        for (int index = 0; index < document.OrderedSegments.Count; index++)
        {
            PromptSegment segment = document.OrderedSegments[index];

            if (segment.Stability != PromptSegmentStability.Stable
                || !Enum.IsDefined(segment.Kind))
            {
                break;
            }

            if (segment.CacheBoundaryEligible)
            {
                lastEligibleSegment = index;
            }
        }

        if (lastEligibleSegment < 0)
        {
            return PromptCachePlan.NonCacheable(
                provider.Name,
                model,
                semanticNamespace,
                PromptCacheEligibility.NonCacheable,
                PromptCacheNonEligibilityReason.NoStablePrefix);
        }

        string stableDigest = ComputeStableSegmentDigest(document, lastEligibleSegment);
        string toolDigest = profile.ToolSchemasParticipate
            ? ComputeToolSchemaDigest(options.Tools)
            : string.Empty;
        string cacheKey = profile.CacheKeysSupported && profile.EmitCacheKey
            ? ComputeCacheKey(
                provider.Name,
                model,
                semanticNamespace,
                profile,
                stableDigest,
                toolDigest)
            : string.Empty;

        return new PromptCachePlan(
            cacheKey,
            provider.Name,
            model,
            semanticNamespace,
            profile.Retention,
            [new PromptCacheBoundary(MessageIndex: 0, SegmentIndex: lastEligibleSegment)],
            stableDigest,
            toolDigest,
            Math.Max(0, eligiblePrefixTokenEstimate),
            PromptCacheEligibility.Eligible,
            PromptCacheNonEligibilityReason.None);
    }

    /// <summary>
    /// Plans a local v2 prefix over the existing render. Tool hashing binds order and bytes;
    /// trusted tool provenance and dispatch authority remain the responsibility of later gates.
    /// An optional scope marker is already present in that render; this method inserts no text.
    /// </summary>
    public static PromptCachePrefixDecision CreatePrefixDecision(
        SystemPromptBuildResult buildResult,
        ChatOptions options,
        string scopeMarkerDigest = "")
    {
        ArgumentNullException.ThrowIfNull(buildResult);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopeMarkerDigest);

        if (buildResult.HasCovenantContent
            || buildResult.CacheSegments.Any(static segment => segment.Sensitive
                || segment.SourceClass == PromptCacheSourceClass.Protected))
        {
            return NonCacheablePrefix(PromptCacheNonEligibilityReason.SensitiveContent);
        }

        if (scopeMarkerDigest.Length != 0
            && (scopeMarkerDigest.Length != 64
                || scopeMarkerDigest.Any(static character => !char.IsAsciiHexDigit(character))))
        {
            return NonCacheablePrefix(PromptCacheNonEligibilityReason.InvalidPlan);
        }

        int prefixEnd = 0;

        foreach (PromptCacheSegmentDescriptor segment in buildResult.CacheSegments)
        {
            if (!PromptCachePolicyV2.IsEligible(
                segment.SourceClass,
                segment.Stability == PromptSegmentStability.Stable,
                segment.Sensitive))
            {
                break;
            }

            if (segment.CacheBoundaryEligible)
            {
                prefixEnd = segment.Utf16Start + segment.Utf16Length;
            }
        }

        if (prefixEnd == 0)
        {
            return NonCacheablePrefix(PromptCacheNonEligibilityReason.NoStablePrefix);
        }

        if (prefixEnd < buildResult.Prompt.Length
            && char.IsHighSurrogate(buildResult.Prompt[prefixEnd - 1])
            && char.IsLowSurrogate(buildResult.Prompt[prefixEnd]))
        {
            return NonCacheablePrefix(PromptCacheNonEligibilityReason.InvalidPlan);
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Append(hash, "arcanum-dci-prefix-v2");
        Append(hash, buildResult.Prompt.AsSpan(0, prefixEnd));

        return new PromptCachePrefixDecision(
            PromptCachePolicyV2.Version,
            0,
            prefixEnd,
            FinishHex(hash),
            scopeMarkerDigest,
            ComputeToolSchemaDigest(options.Tools),
            PromptCacheEligibility.Eligible,
            PromptCacheNonEligibilityReason.None);
    }

    private static PromptCachePrefixDecision NonCacheablePrefix(PromptCacheNonEligibilityReason reason) =>
        new(
            PromptCachePolicyV2.Version,
            0,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            PromptCacheEligibility.NonCacheable,
            reason);

    private static bool IsProtectedKind(PromptSegmentKind kind) =>
        kind is PromptSegmentKind.CovenantProposed
            or PromptSegmentKind.CovenantGlobalConfirmed
            or PromptSegmentKind.CovenantCampaignConfirmed;

    private static string ComputeStableSegmentDigest(
        SystemPromptDocument document,
        int lastEligibleSegment)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        for (int index = 0; index <= lastEligibleSegment; index++)
        {
            PromptSegment segment = document.OrderedSegments[index];

            Append(hash, segment.Kind.ToString());
            Append(hash, segment.Text);
        }

        return FinishHex(hash);
    }

    private static string ComputeToolSchemaDigest(IList<AITool>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return string.Empty;
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (AITool tool in tools)
        {
            Append(hash, tool.Name);
            Append(hash, tool.Description);
            Append(hash, ModelTokenEstimator.FormatValue(tool.AdditionalProperties));

            if (tool is AIFunctionDeclaration function)
            {
                Append(hash, function.JsonSchema.GetRawText());
                Append(hash, function.ReturnJsonSchema?.GetRawText());
            }
        }

        return FinishHex(hash);
    }

    private static string ComputeCacheKey(
        string provider,
        string model,
        PromptCacheSemanticNamespace semanticNamespace,
        PromptCachingProfile profile,
        string stableDigest,
        string toolDigest)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Append(hash, "arcanum-prompt-cache-key-v1");
        Append(hash, semanticNamespace.ToString());
        Append(hash, provider);
        Append(hash, model);
        Append(hash, profile.WireDialect.ToString());
        Append(hash, profile.Retention.ToString());
        Append(hash, stableDigest);
        Append(hash, toolDigest);

        byte[] digest = hash.GetHashAndReset();
        string encoded = Convert.ToBase64String(digest)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return CacheKeyPrefix + encoded;
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            Span<byte> missing = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(missing, -1);
            hash.AppendData(missing);
            return;
        }

        Append(hash, value.AsSpan());
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<char> value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, byteCount);
        hash.AppendData(length);

        if (byteCount == 0)
        {
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);

        try
        {
            int written = Encoding.UTF8.GetBytes(value, rented.AsSpan());
            hash.AppendData(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static string FinishHex(IncrementalHash hash) =>
        Convert.ToHexString(hash.GetHashAndReset());
}
