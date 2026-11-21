using System.Text.Json;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Code-owned invocation result. Only an exact host-owned transport may hydrate native codes;
/// an external result is reduced to the fixed external-operation classification.
/// </summary>
internal sealed record McpToolFailure(ToolOutcome Outcome, string? ReportedText = null)
{
    internal TurnFailureEvidence? FailureEvidence { get; init; }

    internal string ResultText => ReportedText ?? Outcome.Message;

    public override string ToString() => ResultText;

    internal static McpToolsCallResultWire Reported(string text, bool denied = false) => new()
    {
        Content = [new McpToolContentTextWire { Text = text }],
        IsError = true,
        Meta = new McpToolResultMetadata
        {
            Outcome = new ToolOutcome(denied ? ToolOutcomeCode.NativePolicyDenied : ToolOutcomeCode.NativeOperationFailed),
        },
    };

    internal static McpToolsCallResultWire Fault(string text) => new()
    {
        Content = [new McpToolContentTextWire { Text = text }],
        IsError = true,
    };

    internal static McpToolFailure? ReadReported(CallToolResult result, long maxUtf8Bytes)
    {
        if (result.Meta is not { } metadata
            || !metadata.TryGetPropertyValue("arcanum/toolOutcome", out var node)
            || node is null)
        {
            return null;
        }

        try
        {
            ToolOutcome? outcome = node.Deserialize(McpJsonSerializerContext.Default.ToolOutcome);

            if (outcome?.Code is not (ToolOutcomeCode.NativeOperationFailed or ToolOutcomeCode.NativePolicyDenied))
            {
                return null;
            }

            CallToolResult textResult = result.Content?.Any(static block => block is TextContentBlock { Text.Length: > 0 }) == true
                ? result
                : new CallToolResult { Content = [new TextContentBlock { Text = outcome.Message }] };

            string text = McpToolResultFormatter.FormatContentText(textResult, maxUtf8Bytes, allowStructuredFallback: false, strictUtf8Cap: true);

            return new McpToolFailure(outcome, text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static McpToolsCallResultWire Native(ToolOutcomeCode code)
    {
        ToolOutcome outcome = new(code);

        if (!outcome.IsKnownNativeFailure)
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        return new McpToolsCallResultWire
        {
            Content = [new McpToolContentTextWire { Text = outcome.Message }],
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(outcome, McpJsonSerializerContext.Default.ToolOutcome),
        };
    }

    internal static McpToolFailure? ReadNative(JsonElement? structuredContent)
    {
        if (structuredContent is not { ValueKind: JsonValueKind.Object } payload)
        {
            return null;
        }

        try
        {
            ToolOutcome? outcome = JsonSerializer.Deserialize(payload, McpJsonSerializerContext.Default.ToolOutcome);

            return outcome is { IsKnownNativeFailure: true } ? new McpToolFailure(outcome) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
