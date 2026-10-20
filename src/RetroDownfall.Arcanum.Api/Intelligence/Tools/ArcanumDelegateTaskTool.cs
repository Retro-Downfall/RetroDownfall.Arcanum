using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Api.Intelligence.Subagents;
using RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Api.Intelligence.Tools;

internal sealed class ArcanumDelegateTaskTool : AIFunction
{
    private readonly ISubagentRunner runner;

    private readonly string? inheritedModel;

    private readonly MemoryDelegationRootBinding? memoryDelegation;

    internal ArcanumDelegateTaskTool(ISubagentRunner runner, string? inheritedModel, MemoryDelegationRootBinding? memoryDelegation = null)
    {
        this.runner = runner;

        this.inheritedModel = inheritedModel;

        this.memoryDelegation = memoryDelegation;

        RetroDownfall.Arcanum.Infrastructure.Intelligence.PromptCacheToolProvenance.Issue(this);
    }

    private const int MaxPromptCharacters = 262_144;

    private const int MaxFileCharacters = 262_144;

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
              "items": {
                "type": "object",
                "properties": {
                  "path": { "type": "string", "minLength": 1, "maxLength": 4096 },
                  "content": { "type": "string", "maxLength": 262144 },
                  "attachment_id": {
                    "type": "string",
                    "format": "uuid",
                    "description": "Required for attachment-derived content and limited to attachment ids materialized in the parent turn."
                  }
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
        "Runs a self-contained task in one isolated child agent with an explicit token or cost budget and returns only its final summary.";

    private static readonly JsonDocument MemorySchemaDocument = CreateMemorySchema();

    private static JsonDocument CreateMemorySchema()
    {
        var schema = System.Text.Json.Nodes.JsonNode.Parse(SchemaDocument.RootElement.GetRawText())!;

        schema["properties"]!["memory_grant"] = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"string","enum":["memory"],"description":"Select the one operator-authorized memory grant for this child."}""");

        return JsonDocument.Parse(schema.ToJsonString());
    }

    public override JsonElement JsonSchema => memoryDelegation is null ? SchemaDocument.RootElement : MemorySchemaDocument.RootElement;

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

        FileReadResult fileResult = TryReadFiles(
            arguments,
            out IReadOnlyList<AttachedFileDto> files,
            out IReadOnlyList<Guid?> attachmentIds);

        if (fileResult == FileReadResult.ParentPolicyDenied)
        {

            return "Subagent task failed: Explicit files were not delegated by the parent attachment policy.";

        }

        if (fileResult != FileReadResult.Success)
        {
            return "Subagent task failed: Explicit files were invalid or exceeded their bounds.";
        }

        SubagentRunRequest request = new(prompt, inheritedModel, files, maxTokens, maxCostUsd)
        {
            InputProtection = memoryDelegation?.CaptureOutputProtection(),
        };

        if (arguments.TryGetValue("memory_grant", out object? selector))
        {
            if (memoryDelegation is null || !TryReadString(arguments, "memory_grant", out string selected)
                || selected != "memory" || selector is string literal && literal != selected
                || selector is JsonElement json && json.GetString() != selected)
            {
                return "Subagent task failed: Memory delegation is unavailable.";
            }

            Core.Covenant.CovenantDigest digest;

            try
            {
                digest = MemoryDelegationTaskDigest.Compute(request, attachmentIds);
            }
            catch (ArgumentException)
            {
                return "Subagent task failed: The executed task could not be bound.";
            }

            Guid runId = Guid.NewGuid();

            var consumed = await memoryDelegation.ConsumeAsync(runId, digest, cancellationToken).ConfigureAwait(false);

            if (consumed.IsFailure) return "Subagent task failed: Memory delegation is unavailable.";

            request = request with { MemoryDelegation = new(runId, consumed.Value) };
        }

        SubagentRunResult result = await runner
            .RunAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);

        string text = result.FailureCode switch
        {
            null when result.Success => result.Summary,
            SubagentFailureCodes.BudgetExhausted =>
                SubagentParentContextInjector.BudgetExhaustedMessage,
            _ => "Subagent task failed.",
        };

        var sensitivity = result.Sensitivity ?? request.MemoryDelegation?.Child.Sensitivity ?? request.InputProtection?.Sensitivity;

        if (sensitivity is null) return text;

        memoryDelegation?.RecordDerived(sensitivity);

        return new MemoryDelegationToolResult(text, sensitivity);
    }

    private static FileReadResult TryReadFiles(
        AIFunctionArguments arguments,
        out IReadOnlyList<AttachedFileDto> files,
        out IReadOnlyList<Guid?> attachmentIds)
    {
        files = [];

        attachmentIds = [];

        if (!arguments.TryGetValue("files", out object? raw)
            || raw is null)
        {
            return FileReadResult.Success;
        }

        if (raw is not JsonElement element)
        {
            return FileReadResult.Invalid;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return FileReadResult.Invalid;
        }

        List<AttachedFileDto> parsed = new(element.GetArrayLength());

        List<Guid?> identities = new(element.GetArrayLength());

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("path", out JsonElement pathElement)
                || pathElement.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("content", out JsonElement contentElement)
                || contentElement.ValueKind != JsonValueKind.String)
            {
                return FileReadResult.Invalid;
            }

            string path = pathElement.GetString() ?? string.Empty;
            string content = contentElement.GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path)
                || path.Length > 4096
                || content.Length > MaxFileCharacters)
            {
                return FileReadResult.Invalid;
            }

            bool hasAttachmentId = item.TryGetProperty(
                "attachment_id",
                out JsonElement attachmentIdElement);

            Guid? parsedAttachmentId = null;

            if (hasAttachmentId)
            {

                if (attachmentIdElement.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(attachmentIdElement.GetString(), out Guid attachmentId)
                    || !AttachmentMemoryGateAmbient.TryResolve(attachmentId, out _))
                {

                    return FileReadResult.ParentPolicyDenied;

                }

                parsedAttachmentId = attachmentId;

            }
            else if (AttachmentMemoryGateAmbient.HasMaterializedAttachmentContent)
            {

                return FileReadResult.ParentPolicyDenied;

            }

            parsed.Add(new AttachedFileDto(path, content));

            identities.Add(parsedAttachmentId);
        }

        files = parsed.AsReadOnly();

        attachmentIds = identities.AsReadOnly();

        return FileReadResult.Success;
    }

    private enum FileReadResult
    {

        Success,

        Invalid,

        ParentPolicyDenied,

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
