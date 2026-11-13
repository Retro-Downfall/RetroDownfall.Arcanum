using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSpendRefreshConfigurationTests
{
    [Fact]
    public void Effective_publication_changes_refuse_even_when_notifications_are_delayed()
    {
        ArcanumSettings original = Settings();

        PublishingOptions options = new(original);

        using GuildSpendRefreshConfiguration configuration = new(options);

        var captured = configuration.Capture("router", "provider/model");

        Assert.True(captured.IsSuccess, captured.IsFailure ? captured.Error.Message : null);

        Assert.True(configuration.IsCurrent(captured.Value));

        ArcanumSettings changed = Settings();

        changed.Providers[0].GuildUsageReconciliation = GuildUsageReconciliationSource.None;

        options.Publish(changed, notify: false);

        Assert.False(configuration.IsCurrent(captured.Value));

        options.Publish(Settings(), notify: false);

        Assert.False(configuration.IsCurrent(captured.Value));
    }

    [Fact]
    public void In_place_selection_change_and_notified_aba_invalidate_capture()
    {
        ArcanumSettings original = Settings();

        PublishingOptions options = new(original);

        using GuildSpendRefreshConfiguration configuration = new(options);

        var captured = configuration.Capture("router", "provider/model");

        Assert.True(captured.IsSuccess, captured.IsFailure ? captured.Error.Message : null);

        original.Providers[0].Endpoint = "https://other.example/v1";

        Assert.False(configuration.IsCurrent(captured.Value));

        original.Providers[0].Endpoint = "https://openrouter.ai/api/v1";

        options.Publish(Settings(), notify: true);

        options.Publish(original, notify: true);

        Assert.False(configuration.IsCurrent(captured.Value));
    }

    [Fact]
    public void Unavailable_options_publication_fails_closed()
    {
        PublishingOptions options = new(Settings());

        using GuildSpendRefreshConfiguration configuration = new(options);

        var captured = configuration.Capture("router", "provider/model");

        Assert.True(captured.IsSuccess);

        options.ThrowOnRead = true;

        Assert.False(configuration.IsCurrent(captured.Value));

        Assert.True(configuration.Capture("router", "provider/model").IsFailure);
    }

    private static ArcanumSettings Settings() => new()
    {
        Providers = [new() { Name = "router", Endpoint = "https://openrouter.ai/api/v1", Models = ["provider/model"],
            GuildUsageReconciliation = GuildUsageReconciliationSource.OpenRouterGeneration }],
    };

    private sealed class PublishingOptions(ArcanumSettings current) : IOptionsMonitor<ArcanumSettings>
    {
        private Action<ArcanumSettings, string?>? _changed;

        private ArcanumSettings _current = current;

        internal bool ThrowOnRead { get; set; }

        public ArcanumSettings CurrentValue => ThrowOnRead ? throw new InvalidOperationException("publication unavailable") : _current;

        public ArcanumSettings Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<ArcanumSettings, string?> listener)
        {
            _changed += listener;

            return new Subscription(() => _changed -= listener);
        }

        internal void Publish(ArcanumSettings value, bool notify)
        {
            _current = value;

            if (notify) _changed?.Invoke(value, null);
        }

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
