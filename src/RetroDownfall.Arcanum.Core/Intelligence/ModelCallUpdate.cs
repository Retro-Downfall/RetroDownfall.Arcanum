using System.Text;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>Incremental update from a streaming (or buffered) model call.</summary>
public abstract record ModelCallUpdate(ModelCallPurpose Purpose, string ModelCallId);

public sealed record ModelCallTextDelta(ModelCallPurpose Purpose, string ModelCallId, string Text)
    : ModelCallUpdate(Purpose, ModelCallId);

/// <summary>
/// Semantic provider reasoning update. <see cref="VisibleText"/> contains only client-safe text
/// allowed by the effective output mode; protected provider data is represented only by presence.
/// </summary>
public sealed record ModelCallReasoningUpdate(
    ModelCallPurpose Purpose,
    string ModelCallId,
    string VisibleText,
    ReasoningOutputMode? RequestedOutput,
    ReasoningOutputMode EffectiveOutput,
    bool HasProtectedData)
    : ModelCallUpdate(Purpose, ModelCallId);

public sealed record ModelCallResponseUpdate(ModelCallPurpose Purpose, string ModelCallId, ChatResponseUpdate Update)
    : ModelCallUpdate(Purpose, ModelCallId);

public sealed record ModelCallUsageUpdate(ModelCallPurpose Purpose, string ModelCallId, UsageDetails? Usage)
    : ModelCallUpdate(Purpose, ModelCallId);

public sealed record ModelCallContextUpdate(
    ModelCallPurpose Purpose,
    string ModelCallId,
    ContextTokenBreakdown Breakdown)
    : ModelCallUpdate(Purpose, ModelCallId);

public sealed record ModelCallFailureUpdate(
    ModelCallPurpose Purpose,
    string ModelCallId,
    Error Error)
    : ModelCallUpdate(Purpose, ModelCallId);

/// <summary>
/// One normalized reasoning segment retained by the model/tool loop. <see cref="VisibleText"/> is
/// client-safe; opaque provider data is represented only by <see cref="HasProtectedData"/>.
/// </summary>
public sealed record ModelCallReasoningSegment(
    string VisibleText,
    ReasoningOutputMode? RequestedOutput,
    ReasoningOutputMode EffectiveOutput,
    bool HasProtectedData);

/// <summary>
/// Builds adjacent compatible reasoning runs without repeatedly materializing immutable strings.
/// Call <see cref="Materialize"/> only at a semantic boundary or when the completed segments are needed.
/// </summary>
public sealed class ModelCallReasoningAccumulator
{
    private readonly List<MutableRun> _runs = [];

    public void Append(ModelCallReasoningUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Append(
            update.VisibleText,
            update.RequestedOutput,
            update.EffectiveOutput,
            update.HasProtectedData);
    }

    public void Append(ModelCallReasoningSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        Append(
            segment.VisibleText,
            segment.RequestedOutput,
            segment.EffectiveOutput,
            segment.HasProtectedData);
    }

    public void Append(
        string visibleText,
        ReasoningOutputMode? requestedOutput,
        ReasoningOutputMode effectiveOutput,
        bool hasProtectedData)
    {
        ArgumentNullException.ThrowIfNull(visibleText);

        if (_runs.Count > 0 && _runs[^1].IsCompatible(
                requestedOutput,
                effectiveOutput,
                hasProtectedData))
        {
            _ = _runs[^1].VisibleText.Append(visibleText);
            return;
        }

        _runs.Add(new MutableRun(
            visibleText,
            requestedOutput,
            effectiveOutput,
            hasProtectedData));
    }

    public void Clear() => _runs.Clear();

    public IReadOnlyList<ModelCallReasoningSegment> Materialize()
    {
        ModelCallReasoningSegment[] segments = new ModelCallReasoningSegment[_runs.Count];
        for (int index = 0; index < _runs.Count; index++)
        {
            MutableRun run = _runs[index];
            segments[index] = new ModelCallReasoningSegment(
                run.VisibleText.ToString(),
                run.RequestedOutput,
                run.EffectiveOutput,
                run.HasProtectedData);
        }

        return segments;
    }

    private sealed class MutableRun(
        string visibleText,
        ReasoningOutputMode? requestedOutput,
        ReasoningOutputMode effectiveOutput,
        bool hasProtectedData)
    {
        public StringBuilder VisibleText { get; } = new(visibleText);

        public ReasoningOutputMode? RequestedOutput { get; } = requestedOutput;

        public ReasoningOutputMode EffectiveOutput { get; } = effectiveOutput;

        public bool HasProtectedData { get; } = hasProtectedData;

        public bool IsCompatible(
            ReasoningOutputMode? requested,
            ReasoningOutputMode effective,
            bool hasProtected) =>
            RequestedOutput == requested
            && EffectiveOutput == effective
            && HasProtectedData == hasProtected;
    }
}

/// <summary>
/// Normalized reasoning metadata for a buffered model call. Provider-specific reasoning remains on
/// the raw <see cref="ModelCallResult.Response"/> for same-provider continuation only.
/// </summary>
public sealed record ModelCallReasoningResult(
    IReadOnlyList<ModelCallReasoningSegment> Segments,
    ReasoningOutputMode? RequestedOutput,
    ReasoningOutputMode EffectiveOutput,
    bool HasProviderContent,
    bool HasProtectedData);

/// <summary>Final result of a buffered model call (or the combined streaming round).</summary>
public sealed record ModelCallResult(
    ModelCallPurpose Purpose,
    string ModelCallId,
    ChatResponse Response,
    UsageDetails? Usage,
    ModelCallReasoningResult Reasoning,
    ContextTokenBreakdown? ContextBreakdown = null);

/// <summary>Failure of a model call before a successful <see cref="ModelCallResult"/>.</summary>
public sealed record ModelCallFailure(
    ModelCallPurpose Purpose,
    string ModelCallId,
    Error Error,
    Exception? Cause);

/// <summary>Strongly typed success/failure outcome for a buffered provider call.</summary>
public sealed class ModelCallOutcome
{
    private readonly ModelCallResult? _value;
    private readonly ModelCallFailure? _failure;

    private ModelCallOutcome(ModelCallResult? value, ModelCallFailure? failure)
    {
        if ((value is null) == (failure is null))
        {
            throw new ArgumentException("A model-call outcome must contain exactly one arm.");
        }

        _value = value;
        _failure = failure;
    }

    public bool IsSuccess => _value is not null;

    public bool IsFailure => !IsSuccess;

    public ModelCallResult Value => _value
        ?? throw new InvalidOperationException("Cannot access Value on a failed model call.");

    public ModelCallFailure Failure => _failure
        ?? throw new InvalidOperationException("Cannot access Failure on a successful model call.");

    public Error Error => _failure?.Error ?? Error.None;

    public static ModelCallOutcome Success(ModelCallResult value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, failure: null);
    }

    public static ModelCallOutcome Failed(ModelCallFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(value: null, failure);
    }
}
