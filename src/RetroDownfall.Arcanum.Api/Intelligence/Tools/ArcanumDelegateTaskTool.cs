using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

internal sealed class ArcanumDelegateTaskTool(
    ISubagentRunner runner,
    string? inheritedModel) : AIFunction
{
    private const int MaxPromptCharacters = 262_144;

    private const int MaxFiles = 16;

    private const int MaxFileCharacters = 262_144;

    private const int MaxTurns = 16;

    private static readonly JsonDocument SchemaDocument = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "prompt": {
              "type": "string",
              "description": "A self-contained task prompt for the isolated child.",
              "minLength": 1,
              "maxLength": 262144
            },
            "files": {
              "type": "array",
              "description": "Optional explicit file content to pass to the child. No other parent files or chat history are inherited.",
              "maxItems": 16,
              "items": {
                "type": "object",
                "properties": {
                  "path": { "type": "string", "minLength": 1, "maxLength": 4096 },
                  "content": { "type": "string", "maxLength": 262144 }
                },
                "required": ["path", "content"],
                "additionalProperties": false
              }
            },
            "max_tokens": {
              "type": "integer",
              "description": "Hard provider-reported token ceiling delegated to the child.",
              "minimum": 1
            },
            "max_cost_usd": {
              "type": "number",
              "description": "Optional hard USD cost ceiling delegated to the child.",
              "exclusiveMinimum": 0
            },
            "max_turns": {
              "type": "integer",
              "description": "Maximum provider calls allowed in the child loop.",
              "minimum": 1,
              "maximum": 16,
              "default": 4
            }
          },
          "required": ["prompt"],
          "anyOf": [
            { "required": ["max_tokens"] },
            { "required": ["max_cost_usd"] }
          ],
          "additionalProperties": false
        }
        """);

    public override string Name => ArcanumBuiltInToolNames.DelegateTask;

    public override string Description =>
        "Runs a self-contained task in one isolated child agent with an explicit token/cost/turn budget and returns only its final summary.";

    public override JsonElement JsonSchema => SchemaDocument.RootElement;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (!TryReadString(arguments, "prompt", out string prompt)
            || prompt.Length > MaxPromptCharacters)
        {
            return "Subagent task failed: A bounded, non-empty prompt is required.";
        }

        long? maxTokens = TryReadInt64(arguments, "max_tokens", out long tokenValue)
            && tokenValue > 0
                ? tokenValue
                : null;
        decimal? maxCostUsd = TryReadDecimal(arguments, "max_cost_usd", out decimal costValue)
            && costValue > 0
                ? costValue
                : null;

        if (maxTokens is null && maxCostUsd is null)
        {
            return "Subagent task failed: A delegated token or cost budget is required.";
        }

        int maxTurns = TryReadInt64(arguments, "max_turns", out long turnValue)
            ? int.CreateSaturating(turnValue)
            : 4;

        if (maxTurns is < 1 or > MaxTurns)
        {
            return $"Subagent task failed: max_turns must be between 1 and {MaxTurns}.";
        }

        if (!TryReadFiles(arguments, out IReadOnlyList<AttachedFileDto> files))
        {
            return "Subagent task failed: Explicit files were invalid or exceeded their bounds.";
        }

        SubagentRunResult result = await runner
            .RunAsync(
                new SubagentRunRequest(
                    prompt,
                    inheritedModel,
                    files,
                    maxTokens,
                    maxCostUsd,
                    maxTurns),
                cancellationToken)
            .ConfigureAwait(false);

        return result.FailureCode switch
        {
            null when result.Success => result.Summary,
            SubagentFailureCodes.BudgetExhausted =>
                SubagentParentContextInjector.BudgetExhaustedMessage,
            SubagentFailureCodes.MaximumDepth =>
                "Subagent task failed: Maximum subagent depth reached.",
            _ => "Subagent task failed.",
        };
    }

    private static bool TryReadFiles(
        AIFunctionArguments arguments,
        out IReadOnlyList<AttachedFileDto> files)
    {
        files = [];

        if (!arguments.TryGetValue("files", out object? raw)
            || raw is null)
        {
            return true;
        }

        if (raw is not JsonElement element)
        {
            return false;
        }

        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > MaxFiles)
        {
            return false;
        }

        List<AttachedFileDto> parsed = new(element.GetArrayLength());

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("path", out JsonElement pathElement)
                || pathElement.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("content", out JsonElement contentElement)
                || contentElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string path = pathElement.GetString() ?? string.Empty;
            string content = contentElement.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path)
                || path.Length > 4096
                || content.Length > MaxFileCharacters)
            {
                return false;
            }

            parsed.Add(new AttachedFileDto(path, content));
        }

        files = parsed;

        return true;
    }

    private static bool TryReadString(
        AIFunctionArguments arguments,
        string name,
        out string value)
    {
        value = string.Empty;

        if (!arguments.TryGetValue(name, out object? raw)
            || raw is null)
        {
            return false;
        }

        value = raw switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element =>
                element.GetString() ?? string.Empty,
            _ => string.Empty,
        };

        value = value.Trim();

        return value.Length > 0;
    }

    private static bool TryReadInt64(
        AIFunctionArguments arguments,
        string name,
        out long value)
    {
        value = 0;

        if (!arguments.TryGetValue(name, out object? raw)
            || raw is null)
        {
            return false;
        }

        return raw switch
        {
            int number => Assign(number, out value),
            long number => Assign(number, out value),
            JsonElement { ValueKind: JsonValueKind.Number } element =>
                element.TryGetInt64(out value),
            _ => long.TryParse(
                raw.ToString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value),
        };
    }

    private static bool TryReadDecimal(
        AIFunctionArguments arguments,
        string name,
        out decimal value)
    {
        value = 0;

        if (!arguments.TryGetValue(name, out object? raw)
            || raw is null)
        {
            return false;
        }

        return raw switch
        {
            decimal number => Assign(number, out value),
            double number when double.IsFinite(number) =>
                Assign((decimal)number, out value),
            JsonElement { ValueKind: JsonValueKind.Number } element =>
                element.TryGetDecimal(out value),
            _ => decimal.TryParse(
                raw.ToString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out value),
        };
    }

    private static bool Assign<T>(T source, out T destination)
    {
        destination = source;
        return true;
    }
}
