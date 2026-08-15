using System.Collections.Immutable;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Covenant;

public readonly record struct FrozenLogitBias
{
    public FrozenLogitBias(int tokenId, double bias)
    {
        if (!double.IsFinite(bias))
        {
            throw new ArgumentOutOfRangeException(nameof(bias));
        }

        TokenId = tokenId;
        Bias = bias == 0d ? 0d : bias;
    }

    public int TokenId { get; }

    public double Bias { get; }
}

public sealed class FrozenProviderOptions
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private FrozenProviderOptions(
        ProviderOptionsDigestInput input,
        bool hasCanonicalJsonSchema,
        ReadOnlySpan<byte> canonicalJsonSchemaBytes)
    {
        CovenantDigest digest = CovenantDigests.ProviderOptions(input);

        if (hasCanonicalJsonSchema && input.ResponseFormat != ProviderResponseFormat.JsonSchema)
        {
            throw new ArgumentException("Only JSON-schema response options can carry canonical schema bytes.", nameof(canonicalJsonSchemaBytes));
        }

        MaxOutputTokens = input.MaxOutputTokens;
        Temperature = NormalizeFinite(input.Temperature, nameof(input.Temperature));
        TopP = NormalizeFinite(input.TopP, nameof(input.TopP));
        FrequencyPenalty = NormalizeFinite(input.FrequencyPenalty, nameof(input.FrequencyPenalty));
        PresencePenalty = NormalizeFinite(input.PresencePenalty, nameof(input.PresencePenalty));
        Seed = input.Seed;
        EndUserIdentity = ValidateOptionalUtf8(input.EndUserIdentity, CovenantLimits.MaxEndUserIdentityBytes, true, nameof(input.EndUserIdentity));
        Stop = ValidateStop(input.Stop);
        ToolChoice = RequireCode(input.ToolChoice, nameof(input.ToolChoice));
        NamedTool = ValidateOptionalUtf8(input.NamedTool, null, false, nameof(input.NamedTool));
        ParallelToolCalls = RequireCode(input.ParallelToolCalls, nameof(input.ParallelToolCalls));
        ResponseFormat = RequireCode(input.ResponseFormat, nameof(input.ResponseFormat));
        JsonSchemaName = ValidateOptionalUtf8(input.JsonSchemaName, null, false, nameof(input.JsonSchemaName));
        JsonSchemaDescription = ValidateOptionalUtf8(input.JsonSchemaDescription, null, true, nameof(input.JsonSchemaDescription));
        CanonicalJsonSchemaDigest = ValidateOptionalDigest(input.CanonicalJsonSchemaDigest, nameof(input.CanonicalJsonSchemaDigest));
        CanonicalJsonSchemaBytes = hasCanonicalJsonSchema
            ? ProviderProjectionVerifier.VerifyCanonicalJson(
                canonicalJsonSchemaBytes,
                CanonicalJsonSchemaDigest ?? throw new ArgumentException("Canonical schema bytes require a schema digest.", nameof(input)),
                nameof(canonicalJsonSchemaBytes))
            : default;
        JsonSchemaStrict = RequireCode(input.JsonSchemaStrict, nameof(input.JsonSchemaStrict));
        ReasoningEffort = ValidateOptionalCode(input.ReasoningEffort, nameof(input.ReasoningEffort));
        ReasoningBudget = input.ReasoningBudget;
        ReasoningOutput = ValidateOptionalCode(input.ReasoningOutput, nameof(input.ReasoningOutput));
        ReasoningWireDialect = RequireCode(input.ReasoningWireDialect, nameof(input.ReasoningWireDialect));
        LogitBias = ValidateLogitBias(input.LogitBias);

        ValidateCrossFields();

        Digest = digest;
    }

    public ulong? MaxOutputTokens { get; }

    public double? Temperature { get; }

    public double? TopP { get; }

    public double? FrequencyPenalty { get; }

    public double? PresencePenalty { get; }

    public long? Seed { get; }

    public string? EndUserIdentity { get; }

    public ImmutableArray<string> Stop { get; }

    public ProviderToolChoice ToolChoice { get; }

    public string? NamedTool { get; }

    public CovenantTriStateBoolean ParallelToolCalls { get; }

    public ProviderResponseFormat ResponseFormat { get; }

    public string? JsonSchemaName { get; }

    public string? JsonSchemaDescription { get; }

    public CovenantDigest? CanonicalJsonSchemaDigest { get; }

    public ImmutableArray<byte> CanonicalJsonSchemaBytes { get; }

    public bool HasCanonicalJsonSchema => !CanonicalJsonSchemaBytes.IsDefault;

    public CovenantTriStateBoolean JsonSchemaStrict { get; }

    public CovenantReasoningEffort? ReasoningEffort { get; }

    public uint? ReasoningBudget { get; }

    public CovenantReasoningOutput? ReasoningOutput { get; }

    public CovenantReasoningWireDialect ReasoningWireDialect { get; }

    public ImmutableArray<FrozenLogitBias> LogitBias { get; }

    public CovenantDigest Digest { get; }

    public static FrozenProviderOptions Create(ProviderOptionsDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new FrozenProviderOptions(input, false, default);
    }

    public static FrozenProviderOptions Create(
        ProviderOptionsDigestInput input,
        ReadOnlySpan<byte> canonicalJsonSchemaBytes)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new FrozenProviderOptions(input, true, canonicalJsonSchemaBytes);
    }

    public ProviderOptionsDigestInput ToDigestInput() =>
        new(
            MaxOutputTokens,
            Temperature,
            TopP,
            FrequencyPenalty,
            PresencePenalty,
            Seed,
            EndUserIdentity,
            Stop,
            ToolChoice,
            NamedTool,
            ParallelToolCalls,
            ResponseFormat,
            JsonSchemaName,
            JsonSchemaDescription,
            CanonicalJsonSchemaDigest,
            JsonSchemaStrict,
            ReasoningEffort,
            ReasoningBudget,
            ReasoningOutput,
            ReasoningWireDialect,
            LogitBias);

    private static double? NormalizeFinite(double? value, string parameterName)
    {
        if (value is not { } present)
        {
            return null;
        }

        if (!double.IsFinite(present))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return present == 0d ? 0d : present;
    }

    private static string? ValidateOptionalUtf8(
        string? value,
        int? maximumBytes,
        bool allowEmpty,
        string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        int bytes;

        try
        {
            bytes = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("The value must contain strict UTF-8 text.", parameterName, exception);
        }

        if (!allowEmpty && value.Length == 0)
        {
            throw new ArgumentException("The value cannot be empty.", parameterName);
        }

        if (maximumBytes is { } maximum && bytes > maximum)
        {
            throw new ArgumentException("The value exceeds its bounded UTF-8 allocation.", parameterName);
        }

        return value;
    }

    private static ImmutableArray<string> ValidateStop(ImmutableArray<string> values)
    {
        if (values.IsDefault)
        {
            throw new ArgumentException("The ordered stop vector must be initialized.", nameof(values));
        }

        ImmutableArray<string>.Builder builder = ImmutableArray.CreateBuilder<string>(values.Length);

        foreach (string value in values)
        {
            builder.Add(ValidateOptionalUtf8(value, null, true, nameof(values))
                ?? throw new ArgumentException("Stop values cannot contain null.", nameof(values)));
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<FrozenLogitBias> ValidateLogitBias(ImmutableArray<FrozenLogitBias> values)
    {
        if (values.IsDefault)
        {
            return default;
        }

        FrozenLogitBias[] sorted = values.ToArray();

        Array.Sort(sorted, static (left, right) => left.TokenId.CompareTo(right.TokenId));

        for (int index = 0; index < sorted.Length; index++)
        {
            FrozenLogitBias current = new(sorted[index].TokenId, sorted[index].Bias);

            if (index > 0 && current.TokenId == sorted[index - 1].TokenId)
            {
                throw new ArgumentException("Logit-bias token identities must be distinct.", nameof(values));
            }

            sorted[index] = current;
        }

        return [.. sorted];
    }

    private static T RequireCode<T>(T value, string parameterName)
        where T : struct, Enum
    {
        try
        {
            _ = CovenantPolicyV1Manifest.GetCode(value);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(parameterName, exception);
        }

        return value;
    }

    private static T? ValidateOptionalCode<T>(T? value, string parameterName)
        where T : struct, Enum =>
        value is { } present ? RequireCode(present, parameterName) : null;

    private static CovenantDigest? ValidateOptionalDigest(CovenantDigest? value, string parameterName) =>
        value is { } present ? CovenantValidation.RequireDigest(present, parameterName) : null;

    private void ValidateCrossFields()
    {
        if (ToolChoice == ProviderToolChoice.Named != (NamedTool is not null))
        {
            throw new ArgumentException("Named tool choice and named-tool identity must be present together.");
        }

        if (ResponseFormat == ProviderResponseFormat.JsonSchema)
        {
            if (JsonSchemaName is null
                || CanonicalJsonSchemaDigest is null
                || CanonicalJsonSchemaBytes.IsDefault)
            {
                throw new ArgumentException("JSON-schema format requires its frozen name, canonical schema bytes, and canonical schema digest.");
            }
        }
        else if (JsonSchemaName is not null
            || JsonSchemaDescription is not null
            || CanonicalJsonSchemaDigest is not null
            || !CanonicalJsonSchemaBytes.IsDefault
            || JsonSchemaStrict != CovenantTriStateBoolean.Absent)
        {
            throw new ArgumentException("JSON-schema details require JSON-schema response format.");
        }
    }
}
