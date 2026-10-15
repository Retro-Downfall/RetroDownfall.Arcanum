using System.Collections.Immutable;

using System.Text;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.LongRest;

public enum LongRestProviderPayloadKind : byte
{
    Summary = 1,

    Adjudication = 2,

    Embedding = 3,
}

public enum LongRestProviderResultDisposition : byte
{
    Valid = 1,

    Invalid = 2,
}

/// <summary>Closed immutable transient input; exact provider message bytes are checked before send.</summary>
public sealed class LongRestProviderPayload
{
    private LongRestProviderPayload(LongRestProviderPayloadKind kind, LongRestWorkIdentity work, ImmutableArray<LongRestSourceLeaf> inputs, LongRestHierarchyFold? fold, string? text, int inputBudgetBytes)
    {
        Kind = kind;

        Work = work;

        _inputs = inputs;

        Fold = fold;

        EmbeddingText = text;

        InputBudgetBytes = inputBudgetBytes;
    }

    public LongRestProviderPayloadKind Kind { get; }

    public LongRestWorkIdentity Work { get; }

    private readonly ImmutableArray<LongRestSourceLeaf> _inputs;

    /// <summary>Historical snapshots retain array DTOs; each read protects the private frozen arrays.</summary>
    public ImmutableArray<LongRestSourceLeaf> Inputs => [.. _inputs.Select(input => input with
    {
        Snapshot = input.Snapshot with
        {
            Dependencies = [.. input.Snapshot.Dependencies],

            IncomingDependencies = [.. input.Snapshot.IncomingDependencies],
        },
    })];

    public LongRestHierarchyFold? Fold { get; }

    public string? EmbeddingText { get; }

    /// <summary>The rendered bytes the recipe lets this request take; the renderer refuses anything larger.</summary>
    public int InputBudgetBytes { get; }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Result<LongRestProviderPayload> Summary(LongRestHierarchyFold fold, int inputBudgetBytes)
    {
        if (fold is null || !ValidBudget(inputBudgetBytes) || LongRestDiscoveryHash.Work(fold.Work).IsFailure)
        {
            return Invalid("A summary payload requires one bounded exact hierarchy fold.");
        }

        Result<CovenantDigest> source = LongRestDiscoveryHash.HierarchyFold(fold);

        if (source.IsFailure || source.Value != fold.Work.SourceDigest || !SummaryFits(fold.Children, inputBudgetBytes))
        {
            return Invalid("The summary child manifest does not match its frozen work source or input bound.");
        }

        ImmutableArray<LongRestHierarchyChild> children = [.. fold.Children.Select(child => child with
        {
            LabelDigests = [.. child.LabelDigests],

            ExpandedTargets = [.. child.ExpandedTargets],
        })];

        return new LongRestProviderPayload(LongRestProviderPayloadKind.Summary, fold.Work, [], fold with { Children = children }, null, inputBudgetBytes);
    }

    public static Result<LongRestProviderPayload> Adjudication(LongRestWorkIdentity work, ImmutableArray<LongRestSourceLeaf> inputs,
        int inputBudgetBytes)
    {
        if (!ValidBudget(inputBudgetBytes) || LongRestDiscoveryHash.Work(work).IsFailure || inputs.IsDefaultOrEmpty || inputs.Length > LongRestDiscoveryLimits.Targets)
        {
            return Invalid("An adjudication payload requires one bounded exact source vector.");
        }

        Result<CovenantDigest> source = LongRestDiscoveryHash.SourcePage(work.Scope, inputs);

        if (source.IsFailure || source.Value != work.SourceDigest || !AdjudicationFits(inputs, inputBudgetBytes))
        {
            return Invalid("The adjudication source vector does not match its frozen work identity or input bound.");
        }

        ImmutableArray<LongRestSourceLeaf> frozen = [.. inputs.Select(input => input with
        {
            Snapshot = input.Snapshot with
            {
                Dependencies = [.. input.Snapshot.Dependencies],

                IncomingDependencies = [.. input.Snapshot.IncomingDependencies],
            },

            LabelDigests = [.. input.LabelDigests],

            Vector = input.Vector.IsDefault ? default : [.. input.Vector],
        })];

        return new LongRestProviderPayload(LongRestProviderPayloadKind.Adjudication, work, frozen, null, null, inputBudgetBytes);
    }

    public static Result<LongRestProviderPayload> Embedding(LongRestWorkIdentity work, string text)
    {
        if (LongRestDiscoveryHash.Work(work).IsFailure || !WithinInputBound([text]))
        {
            return Invalid("An embedding payload requires bounded strict UTF-8 input and exact work identity.");
        }

        return new LongRestProviderPayload(LongRestProviderPayloadKind.Embedding, work, [], null, text, LongRestDiscoveryLimits.ProviderInputUtf8Bytes);
    }

    private static bool ValidBudget(int inputBudgetBytes) =>
        inputBudgetBytes is >= LongRestPromptLayout.MinimumInputUtf8Bytes and <= LongRestDiscoveryLimits.ProviderInputUtf8Bytes;

    // The planner and the renderer measure the same layout, so a payload that fits here renders within the same budget.
    private static bool SummaryFits(IEnumerable<LongRestHierarchyChild> children, int inputBudgetBytes)
    {
        try
        {
            long total = LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Summary);

            foreach (LongRestHierarchyChild child in children)
            {
                if (string.IsNullOrWhiteSpace(child.Content))
                {
                    return false;
                }

                total += LongRestPromptLayout.SummaryObservationBytes(child.Content);

                if (total > inputBudgetBytes)
                {
                    return false;
                }
            }

            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool AdjudicationFits(ImmutableArray<LongRestSourceLeaf> inputs, int inputBudgetBytes)
    {
        try
        {
            long total = LongRestPromptLayout.FixedBytes(LongRestProviderPayloadKind.Adjudication);

            foreach (LongRestSourceLeaf input in inputs)
            {
                if (string.IsNullOrWhiteSpace(input.Content))
                {
                    return false;
                }

                total += LongRestPromptLayout.AdjudicationMemoryBytes(input);

                if (total > inputBudgetBytes)
                {
                    return false;
                }
            }

            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool WithinInputBound(IEnumerable<string?> content)
    {
        int total = 0;

        foreach (string? text in content)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > LongRestDiscoveryLimits.ProviderInputUtf8Bytes)
            {
                return false;
            }

            try
            {
                total = checked(total + StrictUtf8.GetByteCount(text));
            }
            catch (Exception exception) when (exception is EncoderFallbackException or OverflowException)
            {
                return false;
            }

            if (total > LongRestDiscoveryLimits.ProviderInputUtf8Bytes)
            {
                return false;
            }
        }

        return true;
    }

    private static Result<LongRestProviderPayload> Invalid(string message) => new Error(ErrorCodes.LongRest.InvalidRequest, message);
}

/// <summary>Every successful paid result retains usage even when its projection is invalid.</summary>
public sealed record LongRestProviderUsage(long InputTokens, long OutputTokens, long TotalTokens, long CachedInputTokens, long ReasoningTokens);

public sealed record LongRestProviderResult(
    LongRestProviderPayloadKind Kind,
    LongRestProviderResultDisposition Disposition,
    string? Summary,
    LongRestAdjudicationDecision? Decision,
    ImmutableArray<float> Vector,
    LongRestProviderUsage Usage,
    string? InvalidReason);

/// <summary>Separate closed accounting arms for chat and embedding work.</summary>
public abstract record LongRestProviderAdmission
{
    private LongRestProviderAdmission()
    {
    }

    public sealed record Chat(ModelCallContext Context) : LongRestProviderAdmission;

    public sealed record Embedding(ProviderSettings Provider, string Model, int Dimensions, int ReservedInputTokens, CovenantDigest InputFingerprint) : LongRestProviderAdmission;
}
