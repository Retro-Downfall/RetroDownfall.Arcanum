using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds;

public sealed class ProvingGroundsArbiter(
    IArcanumIntelligenceProvider intelligence,
    IOptionsMonitor<ArcanumSettings> optionsMonitor) : IProvingGroundsArbiter
{

    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyList<InquisitorVerdict>> AdjudicateAsync(
        string output,
        IReadOnlyList<Inquisitor> inquisitors,
        string? judgeModel,
        CancellationToken cancellationToken = default)
    {
        ArcanumSettings arc = optionsMonitor.CurrentValue;

        int maxInquisitors = ArcanumSettingClamps.MaxInquisitorsPerTrial(arc.ProvingGrounds.MaxInquisitorsPerTrial);

        if (inquisitors.Count > maxInquisitors)
        {
            throw new InvalidOperationException(
                $"Trial defines {inquisitors.Count} Inquisitors; the maximum is {maxInquisitors}.");
        }

        List<InquisitorVerdict> verdicts = new(inquisitors.Count);

        foreach (Inquisitor inquisitor in inquisitors)
        {
            InquisitorVerdict verdict = inquisitor switch
            {
                RegexInquisitor regex => AdjudicateRegex(regex, output),
                JsonSchemaInquisitor jsonSchema => AdjudicateJsonSchema(jsonSchema, output),
                SemanticInquisitor semantic => await AdjudicateSemanticAsync(
                    semantic,
                    output,
                    judgeModel,
                    arc,
                    cancellationToken).ConfigureAwait(false),
                _ => new InquisitorVerdict("unknown", inquisitor.Label, false, "Unknown Inquisitor kind."),
            };

            verdicts.Add(verdict);
        }

        return verdicts;
    }

    private static InquisitorVerdict AdjudicateRegex(RegexInquisitor inquisitor, string output)
    {
        if (string.IsNullOrWhiteSpace(inquisitor.Pattern))
        {
            return new InquisitorVerdict("regex", inquisitor.Label, false, "Regex pattern is required.");
        }

        try
        {
            RegexOptions options = RegexOptions.CultureInvariant;

            if (inquisitor.IgnoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            bool isMatch = Regex.IsMatch(output, inquisitor.Pattern, options, RegexMatchTimeout);

            bool passed = isMatch == inquisitor.ShouldMatch;

            string detail = passed
                ? inquisitor.ShouldMatch
                    ? "Output matched the pattern."
                    : "Output did not match the pattern (as expected)."
                : inquisitor.ShouldMatch
                    ? "Output did not match the required pattern."
                    : "Output matched a pattern it should not match.";

            return new InquisitorVerdict("regex", inquisitor.Label, passed, detail);
        }
        catch (RegexMatchTimeoutException)
        {
            return new InquisitorVerdict("regex", inquisitor.Label, false, "Regex match timed out.");
        }
        catch (ArgumentException ex)
        {
            return new InquisitorVerdict("regex", inquisitor.Label, false, $"Invalid regex pattern: {ex.Message}");
        }
    }

    private static InquisitorVerdict AdjudicateJsonSchema(JsonSchemaInquisitor inquisitor, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new InquisitorVerdict("jsonSchema", inquisitor.Label, false, "Output is empty; valid JSON was expected.");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException ex)
        {
            return new InquisitorVerdict("jsonSchema", inquisitor.Label, false, $"Output is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (inquisitor.Schema.ValueKind == JsonValueKind.Undefined
                || inquisitor.Schema.ValueKind == JsonValueKind.Null)
            {
                return new InquisitorVerdict("jsonSchema", inquisitor.Label, true, "Output is valid JSON.");
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new InquisitorVerdict(
                    "jsonSchema",
                    inquisitor.Label,
                    false,
                    "Output JSON root must be an object for schema validation.");
            }

            JsonElement schema = inquisitor.Schema;

            if (schema.TryGetProperty("required", out JsonElement required)
                && required.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in required.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? name = item.GetString();

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (!root.TryGetProperty(name, out _))
                    {
                        return new InquisitorVerdict(
                            "jsonSchema",
                            inquisitor.Label,
                            false,
                            $"Required property '{name}' is missing from output JSON.");
                    }
                }
            }

            if (schema.TryGetProperty("properties", out JsonElement properties)
                && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in properties.EnumerateObject())
                {
                    if (!property.Value.TryGetProperty("type", out JsonElement typeElement)
                        || typeElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string? expectedType = typeElement.GetString();

                    if (string.IsNullOrWhiteSpace(expectedType))
                    {
                        continue;
                    }

                    if (!root.TryGetProperty(property.Name, out JsonElement valueElement))
                    {
                        continue;
                    }

                    if (!JsonValueMatchesDeclaredType(valueElement, expectedType))
                    {
                        return new InquisitorVerdict(
                            "jsonSchema",
                            inquisitor.Label,
                            false,
                            $"Property '{property.Name}' has type '{DescribeJsonValueKind(valueElement.ValueKind)}' but schema expects '{expectedType}'.");
                    }
                }
            }

            return new InquisitorVerdict("jsonSchema", inquisitor.Label, true, "Output satisfies the lightweight JSON schema subset.");
        }
    }

    private async Task<InquisitorVerdict> AdjudicateSemanticAsync(
        SemanticInquisitor inquisitor,
        string output,
        string? judgeModel,
        ArcanumSettings arc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(inquisitor.Question))
        {
            return new InquisitorVerdict("semantic", inquisitor.Label, false, "Semantic question is required.");
        }

        int maxTokens = ArcanumSettingClamps.SemanticJudgeMaxTokens(arc.ProvingGrounds.SemanticJudgeMaxTokens);

        int timeoutSeconds = ArcanumSettingClamps.SemanticJudgeTimeoutSeconds(arc.ProvingGrounds.SemanticJudgeTimeoutSeconds);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        StringBuilder userPrompt = new();

        userPrompt.AppendLine(inquisitor.Question.Trim());

        userPrompt.AppendLine();

        userPrompt.AppendLine("OUTPUT:");

        userPrompt.Append(output);

        List<CoreChatMessage> messages =
        [
            new CoreChatMessage("system", "Answer with only YES or NO."),
            new CoreChatMessage("user", userPrompt.ToString()),
        ];

        PingRequest ping = new(
            Prompt: string.Empty,
            Model: judgeModel,
            WorkingDirectory: string.Empty,
            UnattendedMode: true,
            DisableMcpTools: true,
            SkipSpellRouting: true,
            Temperature: 0f,
            MaxOutputTokens: maxTokens,
            StatelessMessages: messages);

        Result<PromptTurnResult> result;

        try
        {
            result = await intelligence
                .ExecutePromptAsync(ping, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new InquisitorVerdict(
                "semantic",
                inquisitor.Label,
                false,
                $"Semantic judge inference timed out after {timeoutSeconds} seconds.");
        }

        if (result.IsFailure)
        {
            return new InquisitorVerdict(
                "semantic",
                inquisitor.Label,
                false,
                $"Semantic judge inference failed: {result.Error.Message}");
        }

        string answer = result.Value!.Text.Trim();

        bool answerIsYes = answer.StartsWith("YES", StringComparison.OrdinalIgnoreCase);

        bool answerIsNo = answer.StartsWith("NO", StringComparison.OrdinalIgnoreCase);

        if (!answerIsYes && !answerIsNo)
        {
            return new InquisitorVerdict(
                "semantic",
                inquisitor.Label,
                false,
                $"Semantic judge did not answer YES or NO (got: {Truncate(answer, 120)}).");
        }

        bool passed = answerIsYes == inquisitor.ExpectedAnswer;

        string detail = passed
            ? $"Judge answered {(answerIsYes ? "YES" : "NO")} as expected."
            : $"Judge answered {(answerIsYes ? "YES" : "NO")} but expected {(inquisitor.ExpectedAnswer ? "YES" : "NO")}.";

        return new InquisitorVerdict("semantic", inquisitor.Label, passed, detail);
    }

    private static bool JsonValueMatchesDeclaredType(JsonElement value, string expectedType)
    {
        return expectedType switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "boolean" => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true,
        };
    }

    private static string DescribeJsonValueKind(JsonValueKind kind)
    {
        return kind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.Null => "null",
            _ => kind.ToString(),
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

}
