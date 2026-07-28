using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds;

[ExcludeFromCodeCoverage] // Reason: orchestrates live LLM semantic Inquisitors for Proving Grounds trials; covered via ProvingGroundsArbiterTests and trial endpoint integration tests.
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
            // W4.1: return a failed verdict rather than throwing, so a direct arbiter caller that
            // skips the API runner's pre-validation gets the same Result-shaped outcome (one error
            // model, not two).
            return [new InquisitorVerdict(
                "trial",
                "trial",
                false,
                $"Trial defines {inquisitors.Count} Inquisitors; the maximum is {maxInquisitors}.")];
        }

        // W3.6: clamp the trial output before any parse / judge-prompt assembly so a very large
        // target output cannot force an unbounded JsonDocument.Parse or an unbounded judge prompt
        // (the per-inference ping bounds do not apply to this internal adjudication path).
        int maxOutputChars = ArcanumSettingClamps.MaxPingPromptChars(
            (arc.Intelligence ?? new IntelligenceSettings()).MaxPingPromptChars);

        string boundedOutput = output.Length > maxOutputChars ? output[..maxOutputChars] : output;

        List<InquisitorVerdict> verdicts = new(inquisitors.Count);

        foreach (Inquisitor inquisitor in inquisitors)
        {
            // W3.6: synchronous regex/json inquisitors can run for up to ~1s each across many
            // inquisitors; honor cooperative cancellation between each so client cancel / shutdown
            // is not delayed.
            cancellationToken.ThrowIfCancellationRequested();

            InquisitorVerdict verdict = inquisitor switch
            {
                RegexInquisitor regex => AdjudicateRegex(regex, boundedOutput),
                JsonSchemaInquisitor jsonSchema => AdjudicateJsonSchema(jsonSchema, boundedOutput),
                SemanticInquisitor semantic => await AdjudicateSemanticAsync(
                    semantic,
                    boundedOutput,
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

        // W4.1: require an exact YES/NO first token (punctuation-trimmed) rather than StartsWith, so
        // "NOPE"/"NOT"/"YESSIR" no longer classify as NO/YES.
        string[] answerTokens = answer.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        string firstToken = answerTokens.Length > 0
            ? answerTokens[0].Trim('.', '!', '?', ',', ';', ':', '"', '\'')
            : string.Empty;

        bool answerIsYes = string.Equals(firstToken, "YES", StringComparison.OrdinalIgnoreCase);

        bool answerIsNo = string.Equals(firstToken, "NO", StringComparison.OrdinalIgnoreCase);

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
            // W3.6: fail closed on an unrecognized declared type rather than passing it — an author
            // who declares a type outside the supported subset must not get a silent green verdict.
            _ => false,
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
