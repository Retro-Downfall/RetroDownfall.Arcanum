using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.TurnEngine;

/// <summary>Semantic engine event. Phase 1 events are not durable.</summary>
internal abstract record TurnEvent(TurnEventCorrelation Correlation)
{
    public bool IsTerminal => this is RunCompleted or RunFailed or RunAbandoned;
}

internal sealed record RunStarted(TurnEventCorrelation Correlation) : TurnEvent(Correlation);

internal sealed record TurnStatusChanged(TurnEventCorrelation Correlation, string Message) : TurnEvent(Correlation);

internal sealed record SessionBound(TurnEventCorrelation Correlation, Guid SessionId) : TurnEvent(Correlation);

internal sealed record ContextCompressed(TurnEventCorrelation Correlation, string Message) : TurnEvent(Correlation);

internal sealed record ProviderAttemptStarted(TurnEventCorrelation Correlation, string? ProviderName, string? Model)
    : TurnEvent(Correlation);

internal sealed record ProviderSelected(TurnEventCorrelation Correlation, string? ProviderName, string? Model)
    : TurnEvent(Correlation);

internal sealed record ProviderAttemptCommitted(TurnEventCorrelation Correlation) : TurnEvent(Correlation);

internal sealed record ProviderAttemptCompleted(TurnEventCorrelation Correlation) : TurnEvent(Correlation);

internal sealed record ProviderAttemptFailed(TurnEventCorrelation Correlation, Error Error, bool IsConnectivityFailure)
    : TurnEvent(Correlation);

internal sealed record ModelCallStarted(TurnEventCorrelation Correlation, Core.Intelligence.ModelCallPurpose Purpose)
    : TurnEvent(Correlation);

internal sealed record TextDelta(TurnEventCorrelation Correlation, string Text) : TurnEvent(Correlation);

internal sealed record ModelCallCompleted(TurnEventCorrelation Correlation, ChatCompletionUsage? Usage)
    : TurnEvent(Correlation);

internal sealed record ModelCallFailed(TurnEventCorrelation Correlation, Error Error, bool IsConnectivityFailure)
    : TurnEvent(Correlation);

internal sealed record ToolCallProposed(
    TurnEventCorrelation Correlation,
    string CallId,
    string ToolName,
    string ArgumentsJson,
    ToolCallDisposition Disposition) : TurnEvent(Correlation);

internal sealed record ApprovalRequested(
    TurnEventCorrelation Correlation,
    string WardId,
    string ToolName,
    string ArgumentsJson) : TurnEvent(Correlation);

internal sealed record ApprovalResolved(
    TurnEventCorrelation Correlation,
    string WardId,
    string ToolName,
    bool Allowed,
    string? Reason) : TurnEvent(Correlation);

internal sealed record HumanInputRequested(
    TurnEventCorrelation Correlation,
    string CallId,
    string Prompt) : TurnEvent(Correlation);

internal sealed record HumanInputReceived(
    TurnEventCorrelation Correlation,
    string CallId,
    string ResponseText) : TurnEvent(Correlation);

internal sealed record ToolInvocationStarted(
    TurnEventCorrelation Correlation,
    string CallId,
    string ToolName) : TurnEvent(Correlation);

internal sealed record ToolInvocationCompleted(
    TurnEventCorrelation Correlation,
    string CallId,
    string ToolName,
    string ArgumentsJson,
    string ResultText,
    bool Failed,
    bool Denied,
    bool ToleratedFailure,
    string? PublicErrorText,
    TimeSpan Duration,
    bool AttachmentPostProcessed) : TurnEvent(Correlation);

internal sealed record OutputValidated(
    TurnEventCorrelation Correlation,
    bool Passed,
    IReadOnlyList<string> Warnings) : TurnEvent(Correlation);

internal sealed record RunCompleted(
    TurnEventCorrelation Correlation,
    string FinalText,
    ChatCompletionUsage? Usage,
    IReadOnlyList<PromptToolCall>? ToolCalls,
    string? FinishReason,
    IReadOnlyList<string> Warnings,
    Guid? SessionId,
    bool StructuredOutputWarning,
    TurnTerminationReason Reason = TurnTerminationReason.Completed) : TurnEvent(Correlation);

internal sealed record RunFailed(
    TurnEventCorrelation Correlation,
    Error Error,
    TurnTerminationReason Reason,
    ChatCompletionUsage? Usage,
    IReadOnlyList<string> Warnings,
    bool Interrupted,
    string? PartialText) : TurnEvent(Correlation);

internal sealed record RunAbandoned(
    TurnEventCorrelation Correlation,
    Error? Error,
    TurnTerminationReason Reason,
    ChatCompletionUsage? Usage,
    IReadOnlyList<string> Warnings,
    bool Interrupted,
    string? PartialText) : TurnEvent(Correlation);
