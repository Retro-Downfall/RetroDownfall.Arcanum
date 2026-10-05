using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.Guardrails;

/// <summary>
/// Optional context carried from the inference hub into the guardrails scan so the audit record can
/// name the session and model a violation blocked. Both fields are <see langword="null"/> for
/// stateless turns (e.g. <c>/v1/chat/completions</c> with no Grimoire thread).
/// </summary>
public sealed record GuardrailAuditContext(string? SessionId, string? Model);

/// <summary>
/// Content guardrails pipeline (Tier 3 Phase 4, §8.x) — a singleton scanning input messages and
/// output text for PII, toxicity, and topic-policy violations before/after inference. A complete
/// pass-through when <c>Arcanum:Features:Guardrails</c> is <see langword="false"/> (the default): no
/// scanning, no audit logging, success returned immediately. Authored policy comes from
/// <c>Arcanum:Security:Guardrails</c>. PII detection uses
/// <see cref="GeneratedRegexAttribute"/> source generators (AOT-clean); toxicity is a configurable
/// case-insensitive substring blocklist; topics are operator-supplied regex allow/block lists.
/// </summary>
public sealed partial class GuardrailsPipeline(
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IGuardrailAuditLogger auditLogger,
    ILogger<GuardrailsPipeline> logger)
{
    /// <summary>
    /// One PII detector: the violation type it reports, the caller-facing description, and the
    /// compiled pattern. Internal so a test can substitute a pattern with a tiny match budget.
    /// </summary>
    internal sealed record PiiPattern(string Type, string Description, Regex Pattern);

    private static readonly PiiPattern[] DefaultPiiPatterns =
    [
        new(TypePiiEmail, "Email address detected in input.", EmailPattern()),
        new(TypePiiSsn, "Social Security Number detected in input.", SsnPattern()),
        new(TypePiiCreditCard, "Credit card number detected in input.", CreditCardPattern()),
        new(TypePiiPhone, "Phone number detected in input.", PhonePattern()),
    ];

    /// <summary>The PII detectors the input scan runs, in report order.</summary>
    internal IReadOnlyList<PiiPattern> PiiPatterns { get; init; } = DefaultPiiPatterns;

    private const string StageInput = "Input";

    private const string StageOutput = "Output";

    // PII violation type tags — surfaced in the audit record and the GuardrailsViolation.Type field.
    private const string TypePiiEmail = "pii-email";

    private const string TypePiiPhone = "pii-phone";

    private const string TypePiiSsn = "pii-ssn";

    private const string TypePiiCreditCard = "pii-credit-card";

    // Reported when a PII pattern exhausted its match budget; blocks, but is not a PII hit.
    private const string TypePiiUndetermined = "pii-undetermined";

    private const string TypeToxicity = "toxicity";

    private const string TypeTopicAllowed = "topic-allowed";

    private const string TypeTopicBlocked = "topic-blocked";

    /// <summary>
    /// Scans inbound messages before inference. PII (when <c>DetectPii</c>) rejects with
    /// <see cref="ErrorCodes.Guardrails.PiiDetected"/>; toxicity/topic hits reject with
    /// <see cref="ErrorCodes.Guardrails.Blocked"/>. Returns <see cref="GuardrailsResult.Allowed"/>
    /// when the pipeline is disabled or no violation matched.
    /// </summary>
    public async Task<Result<GuardrailsResult>> FilterInputAsync(
        IReadOnlyList<CoreChatMessage>? messages,
        CancellationToken cancellationToken,
        GuardrailAuditContext? auditContext = null)
    {
        GuardrailsSettings settings = optionsMonitor.CurrentValue.ResolveGuardrails();

        if (!settings.Enabled || messages is null or { Count: 0 })
        {
            return Result<GuardrailsResult>.Success(GuardrailsResult.Allowed);
        }

        string text = ConcatenateMessageText(messages);

        List<GuardrailsViolation> violations = ScanInput(text, settings);

        if (violations.Count == 0)
        {
            return Result<GuardrailsResult>.Success(GuardrailsResult.Allowed);
        }

        GuardrailsResult result = new(false, [.. violations]);

        await LogViolationsAsync(StageInput, result.Violations, auditContext).ConfigureAwait(false);

        Error error = BuildError(result.Violations[0], StageInput);

        return Result<GuardrailsResult>.Failure(error);
    }

    /// <summary>
    /// Scans the model's completed output text. PII is not re-scanned here (the input gate already
    /// ran); toxicity (when <c>BlockToxicity</c>) and <c>BlockedTopics</c> reject with
    /// <see cref="ErrorCodes.Guardrails.Blocked"/>. Returns <see cref="GuardrailsResult.Allowed"/>
    /// when the pipeline is disabled or no violation matched.
    /// </summary>
    public async Task<Result<GuardrailsResult>> FilterOutputAsync(
        string text,
        CancellationToken cancellationToken,
        GuardrailAuditContext? auditContext = null)
    {
        GuardrailsSettings settings = optionsMonitor.CurrentValue.ResolveGuardrails();

        if (!settings.Enabled || string.IsNullOrEmpty(text))
        {
            return Result<GuardrailsResult>.Success(GuardrailsResult.Allowed);
        }

        List<GuardrailsViolation> violations = ScanOutput(text, settings);

        if (violations.Count == 0)
        {
            return Result<GuardrailsResult>.Success(GuardrailsResult.Allowed);
        }

        GuardrailsResult result = new(false, [.. violations]);

        await LogViolationsAsync(StageOutput, result.Violations, auditContext).ConfigureAwait(false);

        Error error = BuildError(result.Violations[0], StageOutput);

        return Result<GuardrailsResult>.Failure(error);
    }

    private static string ConcatenateMessageText(IReadOnlyList<CoreChatMessage> messages)
    {
        if (messages.Count == 1)
        {
            return messages[0].Content ?? string.Empty;
        }

        StringBuilder builder = new();

        foreach (CoreChatMessage message in messages)
        {
            if (!string.IsNullOrEmpty(message.Content))
            {
                builder.Append(message.Content).Append('\n');
            }
        }

        return builder.ToString();
    }

    private List<GuardrailsViolation> ScanInput(string text, GuardrailsSettings settings)
    {
        List<GuardrailsViolation> violations = [];

        if (settings.DetectPii)
        {
            AddPiiViolations(violations, text);
        }

        if (settings.BlockToxicity && settings.ToxicityBlocklist is { Length: > 0 } blocklist)
        {
            AddToxicityViolations(violations, text, blocklist);
        }

        AddTopicViolations(violations, text, settings, stageIsInput: true);

        return violations;
    }

    private List<GuardrailsViolation> ScanOutput(string text, GuardrailsSettings settings)
    {
        List<GuardrailsViolation> violations = [];

        if (settings.BlockToxicity && settings.ToxicityBlocklist is { Length: > 0 } blocklist)
        {
            AddToxicityViolations(violations, text, blocklist);
        }

        AddTopicViolations(violations, text, settings, stageIsInput: false);

        return violations;
    }

    private void AddPiiViolations(List<GuardrailsViolation> violations, string text)
    {
        foreach (PiiPattern pii in PiiPatterns)
        {
            switch (MatchPattern(pii.Pattern, text, pii.Type, out string? matched))
            {
                case PatternMatch.Matched:
                    violations.Add(new GuardrailsViolation(
                        pii.Type,
                        pii.Description,
                        RedactMatch(pii.Type, matched)));

                    break;

                // The match budget elapsed, so whether this input carries PII is unknown. Reading
                // that as "no PII" would let crafted input choose its own answer, so an evaluation
                // that did not finish fails closed and is audited, like a blocked-topic timeout.
                case PatternMatch.Undetermined:
                    violations.Add(new GuardrailsViolation(
                        TypePiiUndetermined,
                        "A PII pattern could not be evaluated within its match budget.",
                        RedactMatch(TypePiiUndetermined, null)));

                    break;

                default:
                    break;
            }
        }
    }

    private static void AddToxicityViolations(
        List<GuardrailsViolation> violations,
        string text,
        string[] blocklist)
    {
        foreach (string term in blocklist)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                continue;
            }

            int index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);

            if (index >= 0)
            {
                violations.Add(new GuardrailsViolation(
                    TypeToxicity,
                    $"Blocked term matched guardrail policy.",
                    RedactMatch(TypeToxicity, term)));
            }
        }
    }

    private void AddTopicViolations(
        List<GuardrailsViolation> violations,
        string text,
        GuardrailsSettings settings,
        bool stageIsInput)
    {
        // Allowed-topics only apply to input — an output can be any topic not explicitly blocked.
        if (stageIsInput && settings.AllowedTopics is { Length: > 0 } allowed)
        {
            bool matchedAny = false;

            foreach (string pattern in allowed)
            {
                // Undetermined stays unmatched here, which is already the fail-closed direction for
                // an allow list.
                if (MatchTopic(pattern, text, out string? matched) == PatternMatch.Matched)
                {
                    matchedAny = true;

                    break;
                }
            }

            if (!matchedAny)
            {
                violations.Add(new GuardrailsViolation(
                    TypeTopicAllowed,
                    "Input did not match any allowed-topic pattern.",
                    null));
            }
        }

        if (settings.BlockedTopics is { Length: > 0 } blocked)
        {
            foreach (string pattern in blocked)
            {
                switch (MatchTopic(pattern, text, out string? matched))
                {
                    case PatternMatch.Matched:
                        violations.Add(new GuardrailsViolation(
                            TypeTopicBlocked,
                            "Content matched a blocked-topic pattern.",
                            RedactMatch(TypeTopicBlocked, matched)));

                        break;

                    // The match budget elapsed, so whether the block applies is unknown. Treating
                    // that as "no violation" would let crafted input choose its own answer, so a
                    // blocked-topic evaluation that did not finish fails closed and is audited.
                    case PatternMatch.Undetermined:
                        violations.Add(new GuardrailsViolation(
                            TypeTopicBlocked,
                            "A blocked-topic pattern could not be evaluated within its match budget.",
                            RedactMatch(TypeTopicBlocked, null)));

                        break;

                    default:
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Outcome of evaluating one pattern. <see cref="Undetermined"/> is deliberately distinct
    /// from <see cref="NoMatch"/> so each caller can pick its own fail-safe direction.
    /// </summary>
    private enum PatternMatch
    {
        NoMatch,

        Matched,

        Undetermined,
    }

    private PatternMatch MatchTopic(string pattern, string text, out string? matched)
    {
        matched = null;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return PatternMatch.NoMatch;
        }

        Regex regex;

        try
        {
            regex = GetTopicRegex(pattern);
        }
        catch (ArgumentException ex)
        {
            // An operator typo is a configuration mistake, not an attack surface: the pattern never
            // matches anything, so skipping it is the same answer at every call site.
            logger.LogWarning(ex, "Guardrails topic pattern '{Pattern}' is invalid; skipped.", pattern);

            return PatternMatch.NoMatch;
        }

        return MatchPattern(regex, text, $"topic '{pattern}'", out matched);
    }

    /// <summary>
    /// Runs one pattern once. A match budget that elapses is reported as
    /// <see cref="PatternMatch.Undetermined"/> rather than thrown, so every caller picks its own
    /// fail-safe direction. The input text is never logged.
    /// </summary>
    private PatternMatch MatchPattern(Regex regex, string text, string label, out string? matched)
    {
        matched = null;

        try
        {
            Match m = regex.Match(text);

            if (m.Success)
            {
                matched = m.Value;

                return PatternMatch.Matched;
            }
        }
        catch (RegexMatchTimeoutException ex)
        {
            logger.LogWarning(ex, "Guardrails pattern {Pattern} exceeded its match budget; the result is undetermined.", label);

            return PatternMatch.Undetermined;
        }

        return PatternMatch.NoMatch;
    }

    private static Regex GetTopicRegex(string pattern)
    {
        if (TopicRegexCache.Count >= MaxTopicRegexCacheSize)
        {
            TopicRegexCache.Clear();
        }

        return TopicRegexCache.GetOrAdd(
            pattern,
            static p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, s_matchTimeout));
    }

    private async Task LogViolationsAsync(
        string stage,
        IReadOnlyList<GuardrailsViolation> violations,
        GuardrailAuditContext? auditContext)
    {
        const int maxAuditEntriesPerTurn = 10;

        int logged = 0;

        foreach (GuardrailsViolation violation in violations)
        {
            if (logged >= maxAuditEntriesPerTurn)
            {
                logger.LogWarning(
                    "Guardrails detected {ViolationCount} violations for this turn; only the first {MaxAuditEntries} are audited.",
                    violations.Count,
                    maxAuditEntriesPerTurn);

                break;
            }

            await LogViolationAsync(stage, violation, auditContext).ConfigureAwait(false);

            logged++;
        }
    }

    private async Task LogViolationAsync(
        string stage,
        GuardrailsViolation violation,
        GuardrailAuditContext? auditContext)
    {
        // Never throw — the turn is already being rejected; an audit-trail failure must not escalate.
        try
        {
            GuardrailAuditRecord record = new(
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                auditContext?.SessionId,
                stage,
                violation.Type,
                violation.MatchedText,
                auditContext?.Model);

            // The rejection is already decided, so a request token that was cancelled meanwhile
            // (client disconnect, shutdown) must not drop the only evidence a guardrail fired.
            await auditLogger.LogAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write guardrails audit log entry.");
        }
    }

    /// <summary>
    /// Builds the caller-facing failure for the first violation. The <see cref="ErrorCodes.Guardrails"/>
    /// code is stage-independent (the wire contract is unchanged); only the human-readable text names
    /// the stage, so an output-gate rejection is not misreported as a rejected prompt.
    /// </summary>
    private static Error BuildError(GuardrailsViolation violation, string stage)
    {
        bool isOutput = string.Equals(stage, StageOutput, StringComparison.Ordinal);

        if (string.Equals(violation.Type, TypePiiUndetermined, StringComparison.Ordinal))
        {
            return new Error(
                ErrorCodes.Guardrails.Blocked,
                "Input rejected: the content could not be scanned for personally identifiable information within its time budget.");
        }

        return violation.Type.StartsWith("pii-", StringComparison.Ordinal)
            ? new Error(
                ErrorCodes.Guardrails.PiiDetected,
                isOutput
                    ? "Response blocked: personally identifiable information detected in the model output."
                    : "Input rejected: personally identifiable information detected. Redact PII and retry.")
            : new Error(
                ErrorCodes.Guardrails.Blocked,
                isOutput
                    ? "Response blocked: model output matched a guardrail policy (toxicity or topic)."
                    : "Input rejected: content matched a guardrail policy (toxicity or topic).");
    }

    /// <summary>
    /// Redacts a matched span so the audit log and any error envelope never persist raw PII. PII
    /// types collapse to a fixed masked shape (e.g. <c>***@***.***</c>); toxicity/topic matches keep
    /// only their first and last character with a masked interior.
    /// </summary>
    private static string RedactMatch(string type, string? match)
    {
        if (string.IsNullOrEmpty(match))
        {
            return "***";
        }

        return type switch
        {
            TypePiiEmail => "***@***.***",
            TypePiiSsn => "***-**-****",
            TypePiiCreditCard => "****-****-****-****",
            TypePiiPhone => "***-***-****",
            _ => MaskInterior(match),
        };
    }

    private static string MaskInterior(string match)
    {
        if (match.Length <= 2)
        {
            return "***";
        }

        return string.Concat(match[0], new string('*', Math.Min(match.Length - 2, 8)), match[^1]);
    }

    // Every guardrail pattern, operator-supplied or built in, gets the same match budget; a
    // [GeneratedRegex] needs it as a constant.
    private const int MatchTimeoutMilliseconds = 500;

    private static readonly TimeSpan s_matchTimeout = TimeSpan.FromMilliseconds(MatchTimeoutMilliseconds);

    private static readonly ConcurrentDictionary<string, Regex> TopicRegexCache = new();

    private const int MaxTopicRegexCacheSize = 100;

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex SsnPattern();

    [GeneratedRegex(@"\b(?:\d[ \-]?){13,19}\b", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex CreditCardPattern();

    [GeneratedRegex(@"(?<!\d)(?:\+?\d{1,2}[\s.\-]?)?(?:\(\d{3}\)|\d{3})[\s.\-]?\d{3}[\s.\-]?\d{4}(?!\d)", RegexOptions.None, MatchTimeoutMilliseconds)]
    private static partial Regex PhonePattern();
}
