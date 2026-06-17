using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

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

        bool loreEnabled = _optionsMonitor.CurrentValue.Intelligence.EnableLoreSystem;

        LoreDto? prior = null;

        string jobKey = string.Empty;

        if (loreEnabled)
        {
            IGrimoireRepository repository =
                scope.ServiceProvider.GetRequiredService<IGrimoireRepository>();

            jobKey = $"daemon_state_{_job.Name}";

            try
            {
                prior = await repository
                    .GetLoreAsync(jobKey, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Unseen Servant job {JobName} could not read daemon lore for key {JobKey}.",
                    _job.Name,
                    jobKey);

                prior = null;
            }
        }

        string kickoff;

        if (!loreEnabled)
        {
            kickoff =
                $"""
                Execute Unseen Servant background protocol. Current polling interval is {clampedInterval} minutes.

                If you detect a high-alpha or critical condition requiring the user's immediate attention, you MUST use the `use_commlink` tool to send an alert (set severity appropriately: Info, Warning, or Critical).
                """;
        }
        else
        {
            string previousState = prior?.Value ?? "No previous state recorded.";

            kickoff =
                $"""
                Unseen Servant background protocol.
                Job Name: '{_job.Name}'
                Current polling interval: {clampedInterval} minutes.

                ### Previous State
                {previousState}

                Instructions: Analyze the environment. If you calculate new moving averages, trends, or state that you need for your next waking cycle, you MUST use the `scribe_lore` tool to update the key `{jobKey}` before you complete your turn.
                If you detect a high-alpha or critical condition requiring the user's immediate attention, you MUST use the `use_commlink` tool to send an alert (set severity appropriately: Info, Warning, or Critical).
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

}
