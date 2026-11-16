using System.Diagnostics.Metrics;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WeaveServiceTests
{
    [Theory]
    [InlineData("endpoint-path")]
    [InlineData("model")]
    [InlineData("feature")]
    [InlineData("runtime-binding")]
    [InlineData("runtime-group")]
    [InlineData("unchanged")]
    public async Task Queued_embedding_revalidates_the_full_resolved_configuration_before_physical_send(string change)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));

        ProviderSettings provider = IdentitySettings().Providers[0] with
        {
            Endpoint = "http://127.0.0.1:11434/v1/",
            LocalRuntimeId = "original-runtime",
        };

        ArcanumSettings initial = IdentitySettings() with
        {
            Providers = [provider],
            LocalRuntimes =
            [
                new() { Id = "original-runtime", Adapter = LocalRuntimeAdapterCatalog.Ollama, ManagementEndpoint = "http://127.0.0.1:11434", ResourceGroup = "original-group" },
                new() { Id = "replacement-runtime", Adapter = LocalRuntimeAdapterCatalog.Ollama, ManagementEndpoint = "http://127.0.0.1:11434", ResourceGroup = "original-group" },
            ],
        };

        MutableTestOptionsMonitor<ArcanumSettings> options = new(initial);

        using ScriptedEmbeddingHttpFactory http = new();

        EmbeddingGeneratorFactory factory = new(http, options);

        RecordingTurnRunWriter writer = new();

        using ProviderWorkCoordinator coordinator = new();

        using ProviderWorkCoordinator.ForegroundTurn foreground = coordinator.BeginForegroundTurn();

        await foreground.ReserveAsync(ProviderResourceKey.FromProvider(provider, initial)!.Value, deadline.Token);

        TaskCompletionSource queued = new(TaskCreationOptions.RunContinuationsAsynchronously);

        AsyncLocal<bool> observing = new();

        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "RetroDownfall.Arcanum.ProviderWork"
                && instrument.Name == "arcanum.provider_work.waiting") observer.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (!observing.Value) return;

            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "kind" && Equals(tag.Value, "background")) queued.TrySetResult();
            }
        });

        listener.Start();

        WeaveService weave = CreateService(initial, factory, writer, options: options, providerWorkCoordinator: coordinator);

        Task<Result<Microsoft.Extensions.AI.Embedding<float>>> pending;

        observing.Value = true;

        try
        {
            pending = weave.EmbedAsync("A queued paragraph.", deadline.Token);
        }
        finally
        {
            observing.Value = false;
        }

        try
        {
            await queued.Task.WaitAsync(deadline.Token);

            Assert.Empty(http.RequestedModels);

            options.CurrentValue = change switch
            {
                "endpoint-path" => initial with
                {
                    Providers = [provider with { Endpoint = "http://127.0.0.1:11434/alternate/v1/" }],
                },
                "model" => initial with
                {
                    Integrations = initial.Integrations with
                    {
                        Embeddings = initial.Integrations.Embeddings with { Model = "model-beta" },
                    },
                },
                "feature" => initial with { Features = initial.Features with { Embeddings = false } },
                "runtime-binding" => initial with { Providers = [provider with { LocalRuntimeId = "replacement-runtime" }] },
                "runtime-group" => initial with
                {
                    LocalRuntimes =
                    [
                        initial.LocalRuntimes[0] with { ResourceGroup = "replacement-group" },
                        initial.LocalRuntimes[1],
                    ],
                },
                _ => initial,
            };

            foreground.Dispose();

            Result<Microsoft.Extensions.AI.Embedding<float>> result = await pending.WaitAsync(deadline.Token);

            if (change == "unchanged")
            {
                Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

                Assert.Single(http.RequestedModels);

                Assert.Single(writer.Operations);
            }
            else
            {
                Assert.True(result.IsFailure, "The queued stale generator reached physical dispatch.");

                Assert.Equal(ErrorCodes.Embeddings.ProviderUnavailable, result.Error.Code);

                Assert.Empty(http.RequestedModels);

                Assert.Empty(writer.Operations);
            }
        }
        finally
        {
            foreground.Dispose();

            await deadline.CancelAsync();

            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }
}
