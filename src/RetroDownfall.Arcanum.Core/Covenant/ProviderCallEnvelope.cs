using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Covenant;

public sealed class ProviderCallSensitivity
{
    public ProviderCallSensitivity(
        ContentSensitivity level,
        GenerationProvenance provenance,
        CovenantDigest digest)
    {
        if (level is not ContentSensitivity.None and not ContentSensitivity.CovenantDerived)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));

        if (level == ContentSensitivity.None && !provenance.IsEmpty)
        {
            throw new ArgumentException("Nonsensitive calls cannot carry Covenant provenance.", nameof(provenance));
        }

        if (level == ContentSensitivity.CovenantDerived && provenance.IsEmpty)
        {
            throw new ArgumentException("Covenant-derived calls require provenance.", nameof(provenance));
        }

        Level = level;
        Digest = CovenantValidation.RequireDigest(digest, nameof(digest));

        CovenantDigest expected = CovenantDigests.Sensitivity(new SensitivityDigestInput(level, provenance.Mode, provenance.ExactGenerationIds, provenance.BloomBits));

        if (Digest != expected)
        {
            throw new ArgumentException("The provider-call sensitivity digest does not match its frozen level and provenance.", nameof(digest));
        }
    }

    public ContentSensitivity Level { get; }

    public GenerationProvenance Provenance { get; }

    public CovenantDigest Digest { get; }
}

public sealed record ProviderPromptSpanDigestInput(
    CovenantPromptAttribution Attribution,
    uint Utf16Start,
    uint Utf16Length,
    CovenantDigest SegmentDigest);

public sealed record ProviderToolDefinitionDigestInput(
    string Name,
    CovenantDigest DescriptionDigest,
    CovenantDigest InputSchemaDigest,
    CovenantDigest? OutputSchemaDigest,
    CovenantToolRiskIdentity RiskIdentity);

public sealed record ProviderMessageDigestInput(
    CovenantProviderRole Role,
    string? MessageId,
    string? Name,
    ImmutableArray<ProviderContentPartDigestInput> ContentParts);

public sealed record ProviderContentPartDigestInput
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private ProviderContentPartDigestInput(
        CovenantProviderContentPart kind,
        string? text,
        string? mediaType,
        string? name,
        CovenantImageDetail? imageDetail,
        ImmutableArray<byte> bytes,
        string? normalizedUri,
        string? toolCallId,
        ImmutableArray<byte> protectedData)
    {
        Kind = kind;
        TextValue = text;
        MediaType = mediaType;
        Name = name;
        ImageDetail = imageDetail;
        Bytes = bytes;
        NormalizedUri = normalizedUri;
        ToolCallId = toolCallId;
        ProtectedData = protectedData;
    }

    public CovenantProviderContentPart Kind { get; }

    public string? TextValue { get; }

    public string? MediaType { get; }

    public string? Name { get; }

    public CovenantImageDetail? ImageDetail { get; }

    public ImmutableArray<byte> Bytes { get; }

    public string? NormalizedUri { get; }

    public string? ToolCallId { get; }

    public ImmutableArray<byte> ProtectedData { get; }

    public bool HasProtectedData => !ProtectedData.IsDefault;

    public static ProviderContentPartDigestInput Text(string text) =>
        new(CovenantProviderContentPart.Text, RequireText(text, nameof(text), allowEmpty: true), null, null, null, [], null, null, default);

    public static ProviderContentPartDigestInput Binary(
        string mediaType,
        string? name,
        CovenantImageDetail? imageDetail,
        ReadOnlySpan<byte> bytes) =>
        new(CovenantProviderContentPart.Binary, null, RequireText(mediaType, nameof(mediaType)), ValidateOptionalText(name, nameof(name)), imageDetail, [.. bytes], null, null, default);

    public static ProviderContentPartDigestInput ToolCall(string toolCallId, string toolName, ReadOnlySpan<byte> bytes)
    {
        ImmutableArray<byte> canonicalArguments = RequireCanonicalJson(bytes, nameof(bytes));

        return new(CovenantProviderContentPart.ToolCall, null, null, RequireText(toolName, nameof(toolName)), null, canonicalArguments, null, RequireText(toolCallId, nameof(toolCallId)), default);
    }

    public static ProviderContentPartDigestInput ToolResult(string toolCallId, ReadOnlySpan<byte> bytes) =>
        new(CovenantProviderContentPart.ToolResult, null, null, null, null, [.. bytes], null, RequireText(toolCallId, nameof(toolCallId)), default);

    public static ProviderContentPartDigestInput Json(ReadOnlySpan<byte> canonicalJsonBytes)
    {
        ImmutableArray<byte> canonicalJson = RequireCanonicalJson(canonicalJsonBytes, nameof(canonicalJsonBytes));

        return new(CovenantProviderContentPart.Json, null, null, null, null, canonicalJson, null, null, default);
    }

    public static ProviderContentPartDigestInput Uri(
        string normalizedUri,
        string? mediaType,
        CovenantImageDetail? imageDetail) =>
        new(CovenantProviderContentPart.Uri, null, ValidateOptionalText(mediaType, nameof(mediaType)), null, imageDetail, [], RequireText(normalizedUri, nameof(normalizedUri)), null, default);

    public static ProviderContentPartDigestInput TextReasoning(string reasoningText) =>
        new(
            CovenantProviderContentPart.TextReasoning,
            RequireText(reasoningText, nameof(reasoningText), allowEmpty: true),
            null,
            null,
            null,
            [],
            null,
            null,
            default);

    public static ProviderContentPartDigestInput TextReasoning(
        string reasoningText,
        ReadOnlySpan<byte> protectedData) =>
        new(
            CovenantProviderContentPart.TextReasoning,
            RequireText(reasoningText, nameof(reasoningText), allowEmpty: true),
            null,
            null,
            null,
            [],
            null,
            null,
            [.. protectedData]);

    private static string RequireText(string value, string parameterName, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (!allowEmpty && value.Length == 0)
        {
            throw new ArgumentException("The provider text identity cannot be empty.", parameterName);
        }

        _ = StrictUtf8.GetByteCount(value);

        return value;
    }

    private static string? ValidateOptionalText(string? value, string parameterName) =>
        value is null ? null : RequireText(value, parameterName);

    private static ImmutableArray<byte> RequireCanonicalJson(
        ReadOnlySpan<byte> value,
        string parameterName)
    {
        byte[] canonical;

        try
        {
            canonical = ArcanumCanonicalJsonV1.Canonicalize(value);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The provider content part must contain valid RFC 8785 JSON.", parameterName, exception);
        }

        if (!canonical.AsSpan().SequenceEqual(value))
        {
            throw new ArgumentException("The provider content part bytes must already be in canonical RFC 8785 form.", parameterName);
        }

        return ImmutableCollectionsMarshal.AsImmutableArray(canonical);
    }
}

public sealed record ProviderPromptSpan
{
    public ProviderPromptSpan(
        CovenantPromptAttribution attribution,
        uint utf16Start,
        uint utf16Length,
        CovenantDigest segmentDigest)
    {
        _ = CovenantPolicyV1Manifest.GetCode(attribution);
        Attribution = attribution;
        Utf16Start = utf16Start;
        Utf16Length = utf16Length;
        SegmentDigest = CovenantValidation.RequireDigest(segmentDigest, nameof(segmentDigest));
    }

    public CovenantPromptAttribution Attribution { get; }

    public uint Utf16Start { get; }

    public uint Utf16Length { get; }

    public CovenantDigest SegmentDigest { get; }

    internal ProviderPromptSpanDigestInput ToDigestInput() =>
        new(Attribution, Utf16Start, Utf16Length, SegmentDigest);
}

public sealed class ProviderToolDefinitionEnvelope
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ProviderToolDefinitionEnvelope(
        string name,
        string description,
        CovenantDigest descriptionDigest,
        ReadOnlySpan<byte> canonicalInputSchemaBytes,
        CovenantDigest inputSchemaDigest,
        ReadOnlySpan<byte> canonicalOutputSchemaBytes,
        CovenantDigest? outputSchemaDigest,
        CovenantToolRiskIdentity riskIdentity)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        _ = StrictUtf8.GetByteCount(name);

        _ = CovenantPolicyV1Manifest.GetCode(riskIdentity);
        Name = name;
        Description = ProviderProjectionVerifier.VerifyDescription(description, descriptionDigest, nameof(description));
        DescriptionDigest = descriptionDigest;
        CanonicalInputSchemaBytes = ProviderProjectionVerifier.VerifyCanonicalJson(canonicalInputSchemaBytes, inputSchemaDigest, nameof(canonicalInputSchemaBytes));
        InputSchemaDigest = inputSchemaDigest;

        if (outputSchemaDigest is { } digest)
        {
            CanonicalOutputSchemaBytes = ProviderProjectionVerifier.VerifyCanonicalJson(canonicalOutputSchemaBytes, digest, nameof(canonicalOutputSchemaBytes));
            OutputSchemaDigest = digest;
        }
        else
        {
            if (!canonicalOutputSchemaBytes.IsEmpty)
            {
                throw new ArgumentException("Canonical output-schema bytes require an output-schema digest.", nameof(canonicalOutputSchemaBytes));
            }

            CanonicalOutputSchemaBytes = default;
            OutputSchemaDigest = null;
        }

        RiskIdentity = riskIdentity;
    }

    public string Name { get; }

    public string Description { get; }

    public CovenantDigest DescriptionDigest { get; }

    public ImmutableArray<byte> CanonicalInputSchemaBytes { get; }

    public CovenantDigest InputSchemaDigest { get; }

    public ImmutableArray<byte> CanonicalOutputSchemaBytes { get; }

    public CovenantDigest? OutputSchemaDigest { get; }

    public bool HasOutputSchema => !CanonicalOutputSchemaBytes.IsDefault;

    public CovenantToolRiskIdentity RiskIdentity { get; }

    internal ProviderToolDefinitionDigestInput ToDigestInput() =>
        new(Name, DescriptionDigest, InputSchemaDigest, OutputSchemaDigest, RiskIdentity);
}

public sealed class ProviderContentPartEnvelope
{
    private ProviderContentPartEnvelope(ProviderContentPartDigestInput input)
    {
        DigestInput = input;
    }

    private ProviderContentPartDigestInput DigestInput { get; }

    public CovenantProviderContentPart Kind => DigestInput.Kind;

    public string? TextValue => DigestInput.TextValue;

    public string? MediaType => DigestInput.MediaType;

    public string? Name => DigestInput.Name;

    public CovenantImageDetail? ImageDetail => DigestInput.ImageDetail;

    public ImmutableArray<byte> Bytes => DigestInput.Bytes;

    public string? NormalizedUri => DigestInput.NormalizedUri;

    public string? ToolCallId => DigestInput.ToolCallId;

    public ImmutableArray<byte> ProtectedData => DigestInput.ProtectedData;

    public bool HasProtectedData => !ProtectedData.IsDefault;

    public static ProviderContentPartEnvelope Text(string text) =>
        new(ProviderContentPartDigestInput.Text(text));

    public static ProviderContentPartEnvelope Binary(
        string mediaType,
        string? name,
        CovenantImageDetail? imageDetail,
        ReadOnlySpan<byte> bytes) =>
        new(ProviderContentPartDigestInput.Binary(mediaType, name, imageDetail, bytes));

    public static ProviderContentPartEnvelope ToolCall(string toolCallId, string toolName, ReadOnlySpan<byte> bytes) =>
        new(ProviderContentPartDigestInput.ToolCall(toolCallId, toolName, bytes));

    public static ProviderContentPartEnvelope ToolResult(string toolCallId, ReadOnlySpan<byte> bytes) =>
        new(ProviderContentPartDigestInput.ToolResult(toolCallId, bytes));

    public static ProviderContentPartEnvelope Json(ReadOnlySpan<byte> canonicalJsonBytes) =>
        new(ProviderContentPartDigestInput.Json(canonicalJsonBytes));

    public static ProviderContentPartEnvelope Uri(
        string normalizedUri,
        string? mediaType,
        CovenantImageDetail? imageDetail) =>
        new(ProviderContentPartDigestInput.Uri(normalizedUri, mediaType, imageDetail));

    public static ProviderContentPartEnvelope TextReasoning(string reasoningText) =>
        new(ProviderContentPartDigestInput.TextReasoning(reasoningText));

    public static ProviderContentPartEnvelope TextReasoning(
        string reasoningText,
        ReadOnlySpan<byte> protectedData) =>
        new(ProviderContentPartDigestInput.TextReasoning(reasoningText, protectedData));

    internal ProviderContentPartDigestInput ToDigestInput() =>
        DigestInput;
}

public sealed class ProviderMessageEnvelope
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ProviderMessageEnvelope(
        CovenantProviderRole role,
        string? messageId,
        string? name,
        IEnumerable<ProviderContentPartEnvelope> contentParts)
    {
        _ = CovenantPolicyV1Manifest.GetCode(role);
        ArgumentNullException.ThrowIfNull(contentParts);

        ProviderContentPartEnvelope[] frozenParts = contentParts.ToArray();

        if (frozenParts.Any(static value => value is null))
        {
            throw new ArgumentException("Provider content parts cannot contain null.", nameof(contentParts));
        }

        Role = role;
        MessageId = ValidateOptionalIdentity(messageId, nameof(messageId));
        Name = ValidateOptionalIdentity(name, nameof(name));
        ContentParts = [.. frozenParts];
    }

    public CovenantProviderRole Role { get; }

    public string? MessageId { get; }

    public string? Name { get; }

    public ImmutableArray<ProviderContentPartEnvelope> ContentParts { get; }

    internal ProviderMessageDigestInput ToDigestInput() =>
        new(Role, MessageId, Name, [.. ContentParts.Select(static part => part.ToDigestInput())]);

    private static string? ValidateOptionalIdentity(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Length == 0)
        {
            throw new ArgumentException("The provider identity cannot be empty.", parameterName);
        }

        _ = StrictUtf8.GetByteCount(value);

        return value;
    }
}

public sealed class ProviderCallEnvelope
{
    public ProviderCallEnvelope(
        string providerIdentity,
        string modelIdentity,
        CovenantProviderDispatchMode dispatchMode,
        string tokenizerProfile,
        ulong contextWindowIdentity,
        ulong compressionGeneration,
        ProviderCallSensitivity sensitivity,
        FrozenProviderOptions options,
        ReadOnlySpan<byte> systemPromptBytes,
        IEnumerable<ProviderPromptSpan> promptSpans,
        ProviderCallMaterializationSnapshot materialization,
        IEnumerable<ProviderMessageEnvelope> messages,
        IEnumerable<ProviderToolDefinitionEnvelope> toolDefinitions,
        CovenantDigest? structuredOutputSchemaDigest,
        ReadOnlySpan<byte> canonicalStructuredOutputSchemaBytes = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerIdentity);
        ArgumentException.ThrowIfNullOrEmpty(modelIdentity);
        ArgumentException.ThrowIfNullOrEmpty(tokenizerProfile);
        ArgumentNullException.ThrowIfNull(sensitivity);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(promptSpans);
        ArgumentNullException.ThrowIfNull(materialization);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(toolDefinitions);

        _ = CovenantPolicyV1Manifest.GetCode(dispatchMode);

        ProviderIdentity = providerIdentity;
        ModelIdentity = modelIdentity;
        DispatchMode = dispatchMode;
        TokenizerProfile = tokenizerProfile;
        ContextWindowIdentity = contextWindowIdentity;
        CompressionGeneration = compressionGeneration;
        Sensitivity = sensitivity;
        Options = options;
        SystemPromptBytes = [.. systemPromptBytes];
        PromptSpans = [.. promptSpans];
        Materialization = materialization;
        Messages = [.. messages];
        ToolDefinitions = [.. toolDefinitions];
        if (structuredOutputSchemaDigest is { } digest)
        {
            CanonicalStructuredOutputSchemaBytes = ProviderProjectionVerifier.VerifyCanonicalJson(
                canonicalStructuredOutputSchemaBytes,
                digest,
                nameof(canonicalStructuredOutputSchemaBytes));
            StructuredOutputSchemaDigest = digest;
        }
        else
        {
            if (!canonicalStructuredOutputSchemaBytes.IsEmpty)
            {
                throw new ArgumentException("Canonical structured-output schema bytes require a schema digest.", nameof(canonicalStructuredOutputSchemaBytes));
            }

            CanonicalStructuredOutputSchemaBytes = default;
            StructuredOutputSchemaDigest = null;
        }

        ValidateCollections();

        Digest = CovenantDigests.ProviderCall(ToDigestInput());
    }

    public string ProviderIdentity { get; }

    public string ModelIdentity { get; }

    public CovenantProviderDispatchMode DispatchMode { get; }

    public string TokenizerProfile { get; }

    public ulong ContextWindowIdentity { get; }

    public ulong CompressionGeneration { get; }

    public ProviderCallSensitivity Sensitivity { get; }

    public FrozenProviderOptions Options { get; }

    public ImmutableArray<byte> SystemPromptBytes { get; }

    public ImmutableArray<ProviderPromptSpan> PromptSpans { get; }

    public ProviderCallMaterializationSnapshot Materialization { get; }

    public ImmutableArray<ProviderMessageEnvelope> Messages { get; }

    public ImmutableArray<ProviderToolDefinitionEnvelope> ToolDefinitions { get; }

    public CovenantDigest? StructuredOutputSchemaDigest { get; }

    public ImmutableArray<byte> CanonicalStructuredOutputSchemaBytes { get; }

    public bool HasStructuredOutputSchema => !CanonicalStructuredOutputSchemaBytes.IsDefault;

    public CovenantDigest Digest { get; }

    public ProviderCallDigestInput ToDigestInput() =>
        new(
            ProviderIdentity,
            ModelIdentity,
            DispatchMode,
            TokenizerProfile,
            ContextWindowIdentity,
            CompressionGeneration,
            Sensitivity.Digest,
            Options.Digest,
            SystemPromptBytes,
            [.. PromptSpans.Select(static span => span.ToDigestInput())],
            Materialization.Digest,
            [.. Messages.Select(static message => message.ToDigestInput())],
            [.. ToolDefinitions.Select(static tool => tool.ToDigestInput())],
            StructuredOutputSchemaDigest);

    private void ValidateCollections()
    {
        if (PromptSpans.Any(static value => value is null)
            || Messages.Any(static value => value is null)
            || ToolDefinitions.Any(static value => value is null))
        {
            throw new ArgumentException("Frozen provider collections cannot contain null.");
        }

        ulong end = 0;

        foreach (ProviderPromptSpan span in PromptSpans)
        {
            if (span.Utf16Start < end)
            {
                throw new ArgumentException("Provider prompt spans must be ordered and nonoverlapping.", nameof(PromptSpans));
            }

            end = checked((ulong)span.Utf16Start + span.Utf16Length);
        }
    }
}

internal static class ProviderProjectionVerifier
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string VerifyDescription(
        string description,
        CovenantDigest expectedDigest,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(description, parameterName);
        CovenantDigest expected = CovenantValidation.RequireDigest(expectedDigest, nameof(expectedDigest));
        byte[] encoded;

        try
        {
            encoded = StrictUtf8.GetBytes(description);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("The provider-visible description must be strict UTF-8 text.", parameterName, exception);
        }

        VerifyDigest(encoded, expected, parameterName);

        return description;
    }

    public static ImmutableArray<byte> VerifyCanonicalJson(
        ReadOnlySpan<byte> canonicalJsonBytes,
        CovenantDigest expectedDigest,
        string parameterName)
    {
        CovenantDigest expected = CovenantValidation.RequireDigest(expectedDigest, nameof(expectedDigest));
        byte[] canonical;

        try
        {
            canonical = ArcanumCanonicalJsonV1.Canonicalize(canonicalJsonBytes);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("The provider-visible schema must be valid RFC 8785 JSON.", parameterName, exception);
        }

        if (!canonical.AsSpan().SequenceEqual(canonicalJsonBytes))
        {
            throw new ArgumentException("The provider-visible schema bytes must already be in canonical RFC 8785 form.", parameterName);
        }

        VerifyDigest(canonical, expected, parameterName);

        return [.. canonical];
    }

    private static void VerifyDigest(
        ReadOnlySpan<byte> value,
        CovenantDigest expectedDigest,
        string parameterName)
    {
        Span<byte> digestBytes = stackalloc byte[CovenantLimits.DigestBytes];

        if (!SHA256.TryHashData(value, digestBytes, out int bytesWritten)
            || bytesWritten != CovenantLimits.DigestBytes)
        {
            throw new InvalidOperationException("SHA-256 did not produce a complete provider projection digest.");
        }

        CovenantDigest actual = new(digestBytes.ToArray());

        if (actual != expectedDigest)
        {
            throw new ArgumentException("The provider-visible value does not match its frozen digest.", parameterName);
        }
    }
}
