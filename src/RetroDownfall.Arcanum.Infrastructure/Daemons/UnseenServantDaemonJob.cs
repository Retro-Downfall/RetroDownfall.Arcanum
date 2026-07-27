using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Daemons;

public sealed class UnseenServantDaemonJob : IDaemonJob
{

    private readonly UnseenServantJob _job;

    private readonly IServiceScopeFactory _scopeFactory;

    private readonly IOptionsMonitor<ArcanumSettings> _optionsMonitor;

    private readonly IUnseenServantPacer _pacer;

    private readonly ILogger<UnseenServantDaemonJob> _logger;

    public UnseenServantDaemonJob(
        UnseenServantJob job,
        IServiceProvider serviceProvider)
    {
        _job = job;

        _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        _optionsMonitor = serviceProvider.GetRequiredService<IOptionsMonitor<ArcanumSettings>>();

        _pacer = serviceProvider.GetRequiredService<IUnseenServantPacer>();

        _logger = serviceProvider.GetRequiredService<ILogger<UnseenServantDaemonJob>>();
    }

    public string Id => UnseenServantDaemonIds.ForJobName(_job.Name);

    public string Name => _job.Name;

    public string? Description =>
        string.IsNullOrWhiteSpace(_job.TargetSpell)
            ? "Unseen Servant headless inference job."
            : $"Unseen Servant headless inference for spell '{_job.TargetSpell.Trim()}'.";

    public bool CanRunOnDemand => _job.Enabled;

    public string TargetSpell =>
        string.IsNullOrWhiteSpace(_job.TargetSpell) ? string.Empty : _job.TargetSpell.Trim();

    public async Task RunAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

        IArcanumIntelligenceProvider intelligence =
            scope.ServiceProvider.GetRequiredService<IArcanumIntelligenceProvider>();

        int clampedInterval = ArcanumSettingClamps.UnseenServantIntervalMinutes(_pacer.GetEffectiveInterval(_job));

        bool lexiconEnabled =
            _optionsMonitor.CurrentValue.ResolveIntelligence().EnableLexiconSystem;

        LexiconEntryDto? priorState = null;

        string stateName = string.Empty;

        if (lexiconEnabled)
        {
            ILexiconService lexicon = scope.ServiceProvider.GetRequiredService<ILexiconService>();

            stateName = BuildDaemonStateName(_job.Name, TargetSpell);

            try
            {
                Result<LexiconEntryDto?> lookup = await lexicon
                    .GetByNameAsync(stateName, ct)
                    .ConfigureAwait(false);

                priorState = lookup.IsSuccess ? lookup.Value : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Unseen Servant job {JobName} could not read Lexicon daemon state for {StateName}.",
                    _job.Name,
                    stateName);

                priorState = null;
            }
        }

        string kickoff;

        if (!lexiconEnabled)
        {
            kickoff =
                $"""
                Execute Unseen Servant background protocol. Current polling interval is {clampedInterval} minutes.

                If you detect a high-alpha or critical condition requiring the user's immediate attention, you MUST use the `send_commlink_alert` tool to send an alert (set severity appropriately: Info, Warning, or Critical).
                """;
        }
        else
        {
            string previousState = FormatPreviousState(priorState);

            kickoff =
                $"""
                Unseen Servant background protocol.
                Job Name: '{_job.Name}'
                Current polling interval: {clampedInterval} minutes.

                ### Previous State
                {previousState}

                Instructions: Analyze the environment. If you calculate new moving averages, trends, or state that you need for your next waking cycle, you MUST use the `scribe_lexicon` tool to update the entity named `{stateName}` (type `{LexiconLimits.DaemonStateType}`) with concise facts before you complete your turn.
                If you detect a high-alpha or critical condition requiring the user's immediate attention, you MUST use the `send_commlink_alert` tool to send an alert (set severity appropriately: Info, Warning, or Critical).
                """;
        }

        PingRequest ping = new(
            Prompt: kickoff,
            WorkingDirectory: string.Empty,
            UnattendedMode: true,
            OverrideSpellName: string.IsNullOrWhiteSpace(_job.TargetSpell) ? null : _job.TargetSpell.Trim());

        Result<PromptTurnResult> result = await intelligence
            .ExecutePromptAsync(ping, ct)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "Unseen Servant job {JobName} completed (spell {Spell}).",
                _job.Name,
                _job.TargetSpell);

            return;
        }

        _logger.LogWarning(
            "Unseen Servant job {JobName} failed: {Code} {Message}",
            _job.Name,
            result.Error.Code,
            result.Error.Message);

        throw new InvalidOperationException($"{result.Error.Code}: {result.Error.Message}");
    }

    /// <summary>
    /// Deterministic, low-cardinality Lexicon state key for a daemon job:
    /// <c>daemon_state:{job.Name}:{shortHash(targetSpell)}</c>. The target-spell suffix is hashed so
    /// long or odd-character spell names stay within the Lexicon name length bound while remaining
    /// stable across runs.
    /// </summary>
    internal static string BuildDaemonStateName(string jobName, string targetSpell)
    {

        string trimmedJob = jobName.Trim();

        string spellSuffix = string.IsNullOrEmpty(targetSpell)
            ? "default"
            : ShortHash(targetSpell);

        string candidate = $"daemon_state:{trimmedJob}:{spellSuffix}";

        if (candidate.Length <= LexiconLimits.MaxNameLength)
        {
            return candidate;
        }

        // Job names can exceed MaxNameLength; hash the job portion so the key stays stable and valid.
        return $"daemon_state:{ShortHash(trimmedJob)}:{spellSuffix}";
    }

    private static string ShortHash(string value)
    {

        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));

        StringBuilder sb = new(12);

        for (int i = 0; i < 6; i++)
        {
            _ = sb.Append(bytes[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static string FormatPreviousState(LexiconEntryDto? priorState)
    {

        if (priorState is null || priorState.Facts.Length == 0)
        {
            return "No previous state recorded.";
        }

        StringBuilder sb = new();

        foreach (string fact in priorState.Facts)
        {
            string sanitized = SystemPromptBuilder.SanitizeLexiconText(fact);

            if (sanitized.Length == 0)
            {
                continue;
            }

            _ = sb.Append("- ").AppendLine(sanitized);
        }

        string formatted = sb.ToString().TrimEnd();

        return formatted.Length == 0 ? "No previous state recorded." : formatted;
    }

}
