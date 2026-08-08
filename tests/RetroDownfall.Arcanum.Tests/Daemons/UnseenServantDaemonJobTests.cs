using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Daemons;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Daemons;

public sealed class UnseenServantDaemonJobTests
{

    [Fact]
    public void BuildDaemonStateName_IsDeterministicAndBounded()
    {
        string a = UnseenServantDaemonJob.BuildDaemonStateName("MarketWatcher", "KalshiSpread");
        string b = UnseenServantDaemonJob.BuildDaemonStateName("MarketWatcher", "KalshiSpread");

        Assert.Equal(a, b);

        Assert.StartsWith("daemon_state:MarketWatcher:", a);

        // Different spells yield different suffixes.
        string c = UnseenServantDaemonJob.BuildDaemonStateName("MarketWatcher", "OtherSpell");

        Assert.NotEqual(a, c);
    }

    [Fact]
    public void BuildDaemonStateName_EmptySpell_UsesDefaultSuffix()
    {
        string name = UnseenServantDaemonJob.BuildDaemonStateName("Watcher", "");

        Assert.StartsWith("daemon_state:Watcher:", name);

        Assert.EndsWith(":default", name);
    }

    [Fact]
    public async Task RunAsync_WhenLexiconEnabled_InstructsScribeLexiconAndInjectsPreviousState()
    {
        CapturingIntelligenceProvider intelligence = new();

        FakeLexiconService lexicon = new();

        await lexicon.UpsertAsync("daemon_state:Watcher:default", "DaemonState", ["last average was 42"], CancellationToken.None);

        UnseenServantDaemonJob job = await RunJobAsync(intelligence, lexicon, enableLexicon: true);

        await job.RunAsync(CancellationToken.None);

        Assert.Contains("scribe_lexicon", intelligence.LastPrompt, StringComparison.Ordinal);

        Assert.Contains("daemon_state:Watcher:default", intelligence.LastPrompt, StringComparison.Ordinal);

        Assert.Contains("DaemonState", intelligence.LastPrompt, StringComparison.Ordinal);

        Assert.Contains("last average was 42", intelligence.LastPrompt, StringComparison.Ordinal);

        Assert.DoesNotContain("scribe_lore", intelligence.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenLexiconDisabled_DoesNotInstructScribeLexicon()
    {
        CapturingIntelligenceProvider intelligence = new();

        FakeLexiconService lexicon = new();

        UnseenServantDaemonJob job = await RunJobAsync(intelligence, lexicon, enableLexicon: false);

        await job.RunAsync(CancellationToken.None);

        Assert.DoesNotContain("scribe_lexicon", intelligence.LastPrompt, StringComparison.Ordinal);

        Assert.DoesNotContain("scribe_lore", intelligence.LastPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenLexiconEnabledButStateMissing_DoesNotFailKickoff()
    {
        CapturingIntelligenceProvider intelligence = new();

        FakeLexiconService lexicon = new();

        UnseenServantDaemonJob job = await RunJobAsync(intelligence, lexicon, enableLexicon: true);

        await job.RunAsync(CancellationToken.None);

        Assert.Contains("No previous state recorded.", intelligence.LastPrompt, StringComparison.Ordinal);

        Assert.Contains("scribe_lexicon", intelligence.LastPrompt, StringComparison.Ordinal);
    }

    private static async Task<UnseenServantDaemonJob> RunJobAsync(
        CapturingIntelligenceProvider intelligence,
        FakeLexiconService lexicon,
        bool enableLexicon)
    {
        UnseenServantJob job = new()
        {
            Name = "Watcher",
            Enabled = true,
            TargetSpell = string.Empty,
            IntervalMinutes = 10,
        };

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Lexicon = enableLexicon },
        };

        ServiceCollection services = new();

        services.AddSingleton<IArcanumIntelligenceProvider>(intelligence);

        services.AddSingleton<ILexiconService>(lexicon);

        services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(settings));

        services.AddSingleton<IUnseenServantPacer, FakeUnseenServantPacer>();

        services.AddLogging();

        IServiceProvider sp = services.BuildServiceProvider();

        await using AsyncServiceScope _ = sp.CreateAsyncScope();

        return new UnseenServantDaemonJob(job, sp);
    }

    private sealed class CapturingIntelligenceProvider : IArcanumIntelligenceProvider
    {

        public string LastPrompt { get; private set; } = string.Empty;

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            CancellationToken cancellationToken = default,
            InferenceAuditContext? auditContext = null)
        {
            LastPrompt = request.Prompt;

            return Task.FromResult(Result<PromptTurnResult>.Success(new PromptTurnResult("ok", null)));
        }

        public IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            CancellationToken cancellationToken = default,
            InferenceAuditContext? auditContext = null) =>
            throw new NotSupportedException();
    }

    private sealed class FakeUnseenServantPacer : IUnseenServantPacer
    {

        public bool SetDynamicInterval(string jobName, int intervalMinutes) => true;

        public int GetEffectiveInterval(UnseenServantJob job) => job.IntervalMinutes;

        public Task HydrateAsync(IReadOnlyList<UnseenServantWatermark> watermarks, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

}
