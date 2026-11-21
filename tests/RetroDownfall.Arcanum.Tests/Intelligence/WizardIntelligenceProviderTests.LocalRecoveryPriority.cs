using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed partial class WizardIntelligenceProviderTests
{
    [SkippableFact]
    public async Task Resident_local_runtime_keeps_maintenance_behind_the_repair_before_inference_and_during_a_tool_gap()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string? model = System.Environment.GetEnvironmentVariable("ARCANUM_TEST_RESIDENT_RECOVERY_MODEL");

        Skip.If(string.IsNullOrWhiteSpace(model), "Set ARCANUM_TEST_RESIDENT_RECOVERY_MODEL to a tool-capable resident Ollama model.");

        const string runtimeId = "recovery-witness-runtime";
        const string managementEndpoint = "http://127.0.0.1:11434";
        const string heldTool = "inspect_repair_fixture";
        const string resultMarker = "RECOVERY-READY-417";

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(4));

        TaskCompletionSource beforeInference = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseInference = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource inToolGap = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseTool = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        AsyncLocal<TaskCompletionSource?> currentMaintenance = new();

        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "RetroDownfall.Arcanum.ProviderWork"
                && instrument.Name == "arcanum.provider_work.waiting")
                observer.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "kind" && Equals(tag.Value, "background"))
                    currentMaintenance.Value?.TrySetResult();
            }
        });

        listener.Start();

        ConcurrentQueue<string> actualSends = new();

        ProviderSettings local = DefaultProvider() with
        {
            Name = "recovery-witness",
            Endpoint = managementEndpoint + "/v1/",
            LocalRuntimeId = runtimeId,
            ContextWindowLimit = 262_144,
            Models = [new ModelEntry(model!, SupportsTools: true)],
        };

        await using var host = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), configure: settings =>
        {
            settings.DefaultModel = model;
            settings.FastModel = model;
            settings.Providers = [local];
            settings.LocalRuntimes = [new() { Id = runtimeId, Adapter = LocalRuntimeAdapterCatalog.Ollama, ManagementEndpoint = managementEndpoint }];
            settings.Features = DefaultSettings().Features with { Embeddings = false, Saga = false, Covenant = false, Reasoning = false };
            settings.Cost.Budget.Enabled = false;
        });

        var previous = host.ServiceOverrides;

        host.ServiceOverrides = services =>
        {
            previous!(services);
            services.RemoveAll<IArcanumIntelligenceProvider>();
            services.AddScoped<IArcanumIntelligenceProvider>(sp => sp.GetRequiredService<WizardIntelligenceProvider>());
            services.AddHttpClient("OpenAiCompatibleProvider").AddHttpMessageHandler(() => new RecoveryWitnessSends(actualSends));

            ServiceDescriptor original = services.Last(item => item.ServiceType == typeof(IChatClientFactory));

            services.Remove(original);
            services.AddSingleton<IChatClientFactory>(sp => new RecoveryWitnessFactory(
                (IChatClientFactory)original.ImplementationFactory!(sp), beforeInference, releaseInference));

            FakeMcpConnectionManager mcp = new();

            mcp.Tools.Add(AIFunctionFactory.Create(async (CancellationToken ct) =>
            {
                inToolGap.TrySetResult();

                await releaseTool.Task.WaitAsync(ct);

                return resultMarker;
            }, heldTool, "Inspect the independent repair fixture and return its unique readiness marker."));

            services.RemoveAll<IMcpConnectionManager>();
            services.AddSingleton<IMcpConnectionManager>(mcp);
        };

        using HttpClient client = host.CreateAuthenticatedClient();

        Guid session = await CreateRecoverySessionAsync(client);

        using HttpRequestMessage request = RecoveryRequest("/api/intelligence/ping-stream", RepairRequest(session) with
        {
            Prompt = $"Call the {heldTool} tool exactly once now. After the tool returns its unique readiness marker, report that marker verbatim and stop.",
            Model = model,
            MaxOutputTokens = 384,
            Temperature = 0,
        });

        async Task RunRepairAsync()
        {
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

            using StreamReader reader = new(await response.Content.ReadAsStreamAsync(timeout.Token));

            IntelligenceEvent terminal = await ReadRecoveryFrameAsync(reader,
                frame => frame.Type is IntelligenceEventType.Result or IntelligenceEventType.Error, timeout.Token);

            Assert.True(terminal.Type == IntelligenceEventType.Result, terminal.Message);
        }

        async Task<Result<string>> RunMaintenanceAsync(string marker, TaskCompletionSource waiting)
        {
            currentMaintenance.Value = waiting;

            try
            {
                await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

                return await scope.ServiceProvider.GetRequiredService<ITapestrySummarizer>().SummarizeAsync(
                    new TapestrySummaryRequest(TapestryScopeKind.Workspace, marker, 1, [$"Scheduled summary source {marker}."]),
                    timeout.Token);
            }
            finally
            {
                currentMaintenance.Value = null;
            }
        }

        Task repair = RunRepairAsync();

        Task<Result<string>>? first = null;
        Task<Result<string>>? second = null;

        try
        {
            await beforeInference.Task.WaitAsync(timeout.Token);

            first = RunMaintenanceAsync("recovery-maintenance-before", firstWaiting);

            await firstWaiting.Task.WaitAsync(timeout.Token);

            Assert.Empty(actualSends);

            releaseInference.TrySetResult();

            Task reached = await Task.WhenAny(repair, inToolGap.Task).WaitAsync(timeout.Token);

            Assert.Same(inToolGap.Task, reached);

            second = RunMaintenanceAsync("recovery-maintenance-gap", secondWaiting);

            await secondWaiting.Task.WaitAsync(timeout.Token);

            Assert.Equal(new[] { "repair" }, actualSends.ToArray());

            releaseTool.TrySetResult();

            await repair.WaitAsync(timeout.Token);

            Result<string>[] summaries = await Task.WhenAll(first, second).WaitAsync(timeout.Token);

            Assert.All(summaries, result => Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null));

            string[] observed = actualSends.ToArray();

            Assert.Equal(4, observed.Length);

            Assert.Equal(new[] { "repair", "repair" }, observed[..2]);

            Assert.Equal(new[] { "before", "gap" }, observed[2..].OrderBy(value => value, StringComparer.Ordinal));
        }
        finally
        {
            releaseInference.TrySetResult();
            releaseTool.TrySetResult();

            await timeout.CancelAsync();

            foreach (Task task in new Task?[] { repair, first, second }.OfType<Task>())
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    private sealed class RecoveryWitnessFactory(IChatClientFactory original, TaskCompletionSource entered, TaskCompletionSource release) : IChatClientFactory
    {
        private int _resolutions;

        public async Task<ChatClientLease> ResolveClientAsync(string? model, CancellationToken ct)
        {
            await GateFirstAsync(ct);

            return await original.ResolveClientAsync(model, ct);
        }

        public async Task<ChatClientLease> ResolveClientAsync(ProviderSettings provider, string model, CancellationToken ct)
        {
            await GateFirstAsync(ct);

            return await original.ResolveClientAsync(provider, model, ct);
        }

        private async Task GateFirstAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _resolutions) != 1) return;

            entered.TrySetResult();

            await release.Task.WaitAsync(ct);
        }
    }

    private sealed class RecoveryWitnessSends(ConcurrentQueue<string> sends) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) is true
                && request.Content is { } content)
            {
                string body = await content.ReadAsStringAsync(ct);

                sends.Enqueue(body.Contains("recovery-maintenance-before", StringComparison.Ordinal) ? "before"
                    : body.Contains("recovery-maintenance-gap", StringComparison.Ordinal) ? "gap" : "repair");
            }

            return await base.SendAsync(request, ct);
        }
    }
}
