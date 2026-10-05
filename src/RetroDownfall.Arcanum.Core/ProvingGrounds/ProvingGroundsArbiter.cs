using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds;

[ExcludeFromCodeCoverage] // Reason: orchestrates live LLM semantic Inquisitors for Proving Grounds trials; covered via ProvingGroundsArbiterTests and trial endpoint integration tests.
public sealed class ProvingGroundsArbiter(
    IArcanumIntelligenceProvider intelligence) : IProvingGroundsArbiter
{
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyList<InquisitorVerdict>> AdjudicateAsync(
        string output,
        IReadOnlyList<Inquisitor> inquisitors,
        string? judgeModel,
        CancellationToken cancellationToken = default)
    {
        // W3.6: clamp the trial output before any parse / judge-prompt assembly so a very large
        // target output cannot force an unbounded JsonDocument.Parse or an unbounded judge prompt
        // (the per-inference ping bounds do not apply to this internal adjudication path).
        int maxOutputChars = ArcanumSettingClamps.MaxPingPromptChars(
            ArcanumRuntimeDefaults.Intelligence.MaxPingPromptChars);

        string boundedOutput = output[..Utf8Truncation.SafeCharSliceLength(output, maxOutputChars)];

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
            if (inquisitor.Schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return new InquisitorVerdict("jsonSchema", inquisitor.Label, true, "Output is valid JSON.");
            }

            // The shared validator is the single definition of the supported keyword set (type,
            // properties, required, items, enum, additionalProperties); a schema it cannot read is a
            // failed verdict, never a silent pass.
            using JsonDocument schemaDocument = JsonDocument.Parse(inquisitor.Schema.GetRawText());

            Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schemaDocument);

            if (parsed.IsFailure)
            {
                return new InquisitorVerdict(
                    "jsonSchema",
                    inquisitor.Label,
                    false,
                    $"Schema is not valid: {parsed.Error.Message}");
            }

            // W3.6: fail closed on a declared type outside the supported set rather than passing it —
            // an author who declares an unrecognized type must not get a silent green verdict.
            if (FindUnsupportedType(parsed.Value!) is { } unsupportedType)
            {
                return new InquisitorVerdict(
                    "jsonSchema",
                    inquisitor.Label,
                    false,
                    $"Schema declares unsupported type '{unsupportedType}'.");
            }

            ValidationResult validation = JsonSchemaHelper.Validate(output, parsed.Value!);

            return validation.IsValid
                ? new InquisitorVerdict("jsonSchema", inquisitor.Label, true, "Output satisfies the JSON schema.")
                : new InquisitorVerdict(
                    "jsonSchema",
                    inquisitor.Label,
                    false,
                    string.Join(" ", validation.Errors));
        }
    }

    private static readonly HashSet<string> SupportedSchemaTypes =
        new(StringComparer.Ordinal) { "string", "number", "integer", "boolean", "object", "array", "null" };

    private static string? FindUnsupportedType(JsonSchemaDefinition schema)
    {
        // Every member of a type union, not only the first: a later unrecognized member must not be
        // accepted silently because an earlier one was fine.
        foreach (string declared in schema.AlternativeTypes.Prepend(schema.Type))
        {
            if (!string.IsNullOrEmpty(declared) && !SupportedSchemaTypes.Contains(declared))
            {
                return declared;
            }
        }

        foreach (JsonSchemaDefinition property in schema.Properties.Values)
        {
            if (FindUnsupportedType(property) is { } nested)
            {
                return nested;
            }
        }

        return schema.Items is null ? null : FindUnsupportedType(schema.Items);
    }

    private async Task<InquisitorVerdict> AdjudicateSemanticAsync(
        SemanticInquisitor inquisitor,
        string output,
        string? judgeModel,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(inquisitor.Question))
        {
            return new InquisitorVerdict("semantic", inquisitor.Label, false, "Semantic question is required.");
        }

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
            StatelessMessages: messages);

        // A semantic judge is unattended internal inference. It receives no Covenant context and
        // exposes no mutation tool, which is exactly what None means (§10.12).
        Result<PromptTurnResult> result = await intelligence
            .ExecutePromptAsync(ping, ArcanumInvocationContext.None, cancellationToken)
            .ConfigureAwait(false);

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

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Utf8Truncation.SafeCharSliceLength(value, maxLength)] + "...";
    }
}
