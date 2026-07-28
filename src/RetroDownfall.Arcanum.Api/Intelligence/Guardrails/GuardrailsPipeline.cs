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
/// pass-through when <c>Arcanum:Guardrails:Enabled</c> is <see langword="false"/> (the default): no
/// scanning, no audit logging, success returned immediately. PII detection uses
/// <see cref="GeneratedRegexAttribute"/> source generators (AOT-clean); toxicity is a configurable
/// case-insensitive substring blocklist; topics are operator-supplied regex allow/block lists.
/// </summary>
public sealed partial class GuardrailsPipeline(
    IOptionsMonitor<ArcanumSettings> optionsMonitor,
    IGuardrailAuditLogger auditLogger,
    ILogger<GuardrailsPipeline> logger)
{

    private const string StageInput = "Input";

    private const string StageOutput = "Output";

    // PII violation type tags — surfaced in the audit record and the GuardrailsViolation.Type field.
    private const string TypePiiEmail = "pii-email";

    private const string TypePiiPhone = "pii-phone";

    private const string TypePiiSsn = "pii-ssn";

    private const string TypePiiCreditCard = "pii-credit-card";

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

        GuardrailsSettings settings = optionsMonitor.CurrentValue.Guardrails;

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

        await LogViolationsAsync(StageInput, result.Violations, auditContext, cancellationToken).ConfigureAwait(false);

        Error error = BuildError(result.Violations[0]);

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

        GuardrailsSettings settings = optionsMonitor.CurrentValue.Guardrails;

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

        await LogViolationsAsync(StageOutput, result.Violations, auditContext, cancellationToken).ConfigureAwait(false);

        Error error = BuildError(result.Violations[0]);

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

    private static void AddPiiViolations(List<GuardrailsViolation> violations, string text)
    {

        if (EmailPattern().IsMatch(text))
        {

            violations.Add(new GuardrailsViolation(
                TypePiiEmail,
                "Email address detected in input.",
                RedactMatch(TypePiiEmail, EmailPattern().Match(text).Value)));

        }

        if (SsnPattern().IsMatch(text))
        {

            violations.Add(new GuardrailsViolation(
                TypePiiSsn,
                "Social Security Number detected in input.",
                RedactMatch(TypePiiSsn, SsnPattern().Match(text).Value)));

        }

        if (CreditCardPattern().IsMatch(text))
        {

            violations.Add(new GuardrailsViolation(
                TypePiiCreditCard,
                "Credit card number detected in input.",
                RedactMatch(TypePiiCreditCard, CreditCardPattern().Match(text).Value)));

        }

        if (PhonePattern().IsMatch(text))
        {

            violations.Add(new GuardrailsViolation(
                TypePiiPhone,
                "Phone number detected in input.",
                RedactMatch(TypePiiPhone, PhonePattern().Match(text).Value)));

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

                if (TryMatchTopic(pattern, text, out string? matched))
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

                if (TryMatchTopic(pattern, text, out string? matched))
                {

                    violations.Add(new GuardrailsViolation(
                        TypeTopicBlocked,
                        "Content matched a blocked-topic pattern.",
                        RedactMatch(TypeTopicBlocked, matched)));

                }

            }

        }

    }

    private bool TryMatchTopic(string pattern, string text, out string? matched)
    {

        matched = null;

        if (string.IsNullOrWhiteSpace(pattern))
        {

            return false;

        }

        try
        {

            Regex regex = GetTopicRegex(pattern);

            Match m = regex.Match(text);

            if (m.Success)
            {

                matched = m.Value;

                return true;

            }

        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {

            logger.LogWarning(ex, "Guardrails topic pattern '{Pattern}' is invalid or timed out; skipped.", pattern);

        }

        return false;

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
        GuardrailAuditContext? auditContext,
        CancellationToken cancellationToken)
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

            await LogViolationAsync(stage, violation, auditContext, cancellationToken).ConfigureAwait(false);

            logged++;

        }

    }

    private async Task LogViolationAsync(
        string stage,
        GuardrailsViolation violation,
        GuardrailAuditContext? auditContext,
        CancellationToken cancellationToken)
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

            await auditLogger.LogAsync(record, cancellationToken).ConfigureAwait(false);

        }
        catch (Exception ex)
        {

            logger.LogWarning(ex, "Failed to write guardrails audit log entry.");

        }

    }

    private static Error BuildError(GuardrailsViolation violation) =>
        violation.Type.StartsWith("pii-", StringComparison.Ordinal)
            ? new Error(ErrorCodes.Guardrails.PiiDetected, "Input rejected: personally identifiable information detected. Redact PII and retry.")
            : new Error(ErrorCodes.Guardrails.Blocked, "Input rejected: content matched a guardrail policy (toxicity or topic).");

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

    private static readonly TimeSpan s_matchTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly ConcurrentDictionary<string, Regex> TopicRegexCache = new();

    private const int MaxTopicRegexCacheSize = 100;

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.None)]
    private static partial Regex SsnPattern();

    [GeneratedRegex(@"\b(?:\d[ \-]?){13,19}\b", RegexOptions.None)]
    private static partial Regex CreditCardPattern();

    [GeneratedRegex(@"(?<!\d)(?:\+?\d{1,2}[\s.\-]?)?(?:\(\d{3}\)|\d{3})[\s.\-]?\d{3}[\s.\-]?\d{4}(?!\d)", RegexOptions.None)]
    private static partial Regex PhonePattern();

}
