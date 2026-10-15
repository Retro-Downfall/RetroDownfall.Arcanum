using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Packaging;

/// <summary>
/// One Long Rest qualification: an isolated installation of the published apphost, the recording forwarder in front of Ollama,
/// and the scenarios that run against them in order. State is kept on the instance because every scenario builds on the last
/// (the corpus the seeding wrote, the sweep the first scenario completed, the host the second scenario killed and restarted).
/// </summary>
internal sealed partial class LongRestQualificationRun(
    LongRestQualificationOptions options,
    PublishedTestProcessIsolation isolation,
    LongRestQualificationLog log)
{
    private static readonly TimeSpan HostStartupBudget = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan CommandBudget = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan TurnBudget = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan SweepBudget = TimeSpan.FromMinutes(45);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly LongRestQualificationOptions _options = options;

    private readonly PublishedTestProcessIsolation _isolation = isolation;

    private readonly LongRestQualificationLog _log = log;

    private readonly List<string> _observations = [];

    private LongRestQualificationEnvironment? _environment;

    private LongRestQualificationProxy? _proxy;

    private LongRestQualificationApi? _api;

    private LongRestQualificationGrimoire? _grimoire;

    private LongRestPublishedHost? _host;

    private TcpListener? _portReservation;

    private int _hostPort;

    private string? _masterApiKey;

    private IReadOnlyList<LongRestSeededMemory> _corpus = [];

    private LongRestQualificationEnvironment Installation => _environment ?? throw new InvalidOperationException("The installation was not prepared.");

    private LongRestQualificationProxy Proxy => _proxy ?? throw new InvalidOperationException("The forwarder was not started.");

    private LongRestQualificationApi Api => _api ?? throw new InvalidOperationException("The host API was not reached.");

    private LongRestQualificationGrimoire Grimoire => _grimoire ?? throw new InvalidOperationException("The Grimoire was not opened.");

    /// <summary>What the run measured, in the order it measured it. The report prints these beside the calibration.</summary>
    public IReadOnlyList<string> Observations => _observations;

    public string?[] SensitiveValues() => [_masterApiKey, _host?.DisclosedMasterApiKey];

    public string? MasterApiKey => _masterApiKey;

    /// <summary>
    /// Creates the isolated home, starts the forwarder and the first host. The first host runs with Saga extraction on and Long Rest
    /// off, so the seeding turns schedule no sweep.
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        _isolation.Prepare();

        string workspace = Path.Combine(_isolation.TestHome, "workspace");

        Directory.CreateDirectory(workspace);

        _environment = new LongRestQualificationEnvironment(_isolation, workspace);

        Directory.CreateDirectory(Installation.ConfigurationDirectory);

        _proxy = await LongRestQualificationProxy.StartAsync(_options.Endpoint);

        _log.Info($"Forwarder listening at {Proxy.Endpoint} in front of {_options.Endpoint}");

        _portReservation = new TcpListener(IPAddress.Loopback, 0);

        _portReservation.Start();

        _hostPort = ((IPEndPoint)_portReservation.LocalEndpoint).Port;

        await WriteConfigurationAsync(sagaExtraction: true, longRest: false);

        // The reservation closes immediately before launch. A concurrent claimant can still win this unavoidable hand-off; startup is
        // allowed to fail rather than silently testing a different port.
        _portReservation.Stop();

        _portReservation.Dispose();

        _portReservation = null;

        // Fail closed before the host first writes a secret: the product falls back to the operating-system credential store (and so
        // the keychain) whenever its testing gate does not hold, and nothing else would notice until it prompted.
        LongRestQualificationEnvironment.AssertInMemoryCredentialBackend(Installation.CreateStartInfo(_options.Executable, ["serve", "--plain"]));

        _log.Info("The isolated environment selects the in-memory credential store (the product's testing gate holds); no operating-system credential store can be reached");

        _host = LongRestPublishedHost.Start(_options.Executable, Installation, knownMasterApiKey: null);

        _masterApiKey = await _host.WaitForInitialReadyAsync(HostStartupBudget);

        Installation.MasterApiKey = _masterApiKey;

        _api = new LongRestQualificationApi(_hostPort, _masterApiKey);

        await AssertNativeAotMetadataAsync(cancellationToken);

        _log.Info($"Published host ready on port {_hostPort} (Native AOT confirmed by /api/meta)");
    }

    /// <summary>
    /// Stops the host gracefully, switches the installation to Long Rest on and Saga extraction off, and starts the same published
    /// host again on the same home. The master key must still authenticate and no replacement key may be disclosed.
    /// </summary>
    public async Task EnableLongRestAsync(CancellationToken cancellationToken)
    {
        await StopHostGracefullyAsync(cancellationToken);

        await WriteConfigurationAsync(sagaExtraction: false, longRest: true);

        await StartKnownHostAsync("Long Rest enabled", cancellationToken);

        _grimoire ??= await LongRestQualificationGrimoire.OpenAsync(Installation.ConfigurationDirectory, cancellationToken);
    }

    /// <summary>
    /// Stops the last host gracefully (a run that ends with an unclean exit is not a pass), then fails with every product-level finding the
    /// scenarios recorded, so one run reports all of them.
    /// </summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        await StopHostGracefullyAsync(cancellationToken);

        Assert.True(
            _findings.Count == 0,
            "The scenarios completed, but Long Rest has product-level findings:\n- " + string.Join("\n- ", _findings));
    }

    public async Task<IReadOnlyList<Exception>> CleanupAsync(string? masterApiKey)
    {
        List<Exception> failures = [];

        if (_host is not null)
        {
            try
            {
                failures.AddRange(await _host.CleanupAsync("current"));
            }
            catch (Exception exception)
            {
                failures.Add(CreateCleanupFailure("Long Rest qualification published-host cleanup failed.", exception, masterApiKey));
            }
        }

        foreach (LongRestPublishedHost retired in _retiredHosts)
        {
            try
            {
                failures.AddRange(await retired.CleanupAsync("retired"));
            }
            catch (Exception exception)
            {
                failures.Add(CreateCleanupFailure("Long Rest qualification retired-host cleanup failed.", exception, masterApiKey));
            }
        }

        try
        {
            _api?.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(CreateCleanupFailure("Long Rest qualification API-client cleanup failed.", exception, masterApiKey));
        }

        if (_proxy is not null)
        {
            try
            {
                await _proxy.DisposeAsync();
            }
            catch (Exception exception)
            {
                failures.Add(CreateCleanupFailure("Long Rest qualification forwarder cleanup failed.", exception, masterApiKey));
            }
        }

        if (_portReservation is not null)
        {
            try
            {
                _portReservation.Stop();

                _portReservation.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(CreateCleanupFailure("Long Rest qualification port-reservation cleanup failed.", exception, masterApiKey));
            }
        }

        return failures;
    }

    private readonly List<LongRestPublishedHost> _retiredHosts = [];

    private InvalidOperationException CreateCleanupFailure(string context, Exception failure, string? masterApiKey) =>
        LocalOllamaQualificationGuards.CreateSanitizedFailure(
            context,
            failure,
            LongRestQualificationConfiguration.ChatCredentialPlaceholder,
            masterApiKey,
            LongRestQualificationConfiguration.EmbeddingCredentialPlaceholder);

    private Task WriteConfigurationAsync(bool sagaExtraction, bool longRest) =>
        LongRestQualificationConfiguration.WriteAsync(
            Installation.ConfigurationPath,
            LongRestQualificationConfiguration.Create(
                _options,
                _hostPort,
                Proxy.Endpoint,
                Installation.Workspace,
                sagaExtraction,
                longRest));

    private async Task StartKnownHostAsync(string description, CancellationToken cancellationToken)
    {
        LongRestPublishedHost host = LongRestPublishedHost.Start(_options.Executable, Installation, _masterApiKey);

        _host = host;

        await host.WaitForRestartReadyAsync(HostStartupBudget);

        Assert.True(
            host.DisclosedMasterApiKey is null,
            "A restarted host disclosed a replacement master API key.");

        await AssertNativeAotMetadataAsync(cancellationToken);

        // The exporter should listen from the host's start. Authenticated requests are answered, then the first scrape must carry them:
        // when it does not, the exporter only starts at that scrape and loses everything measured before it, which is a product defect
        // and a finding on every restart.
        _ = await Api.GetMetaAsync(cancellationToken);

        _ = await Api.GetMetaAsync(cancellationToken);

        IReadOnlyDictionary<string, double> firstScrape = await Api.ReadMetricsAsync(cancellationToken);

        string? lateExporter = LongRestQualificationChecks.ExporterStartFinding(description, firstScrape);

        if (lateExporter is not null)
        {
            RecordFinding(lateExporter);
        }

        // That scrape is also a temporary workaround, kept only until the exporter starts with the host: the listener never replays earlier
        // measurements, so the counters a sweep moves can be compared only if a scrape came before it. The finding above is what shows
        // whether the workaround is still needed; the product defect is reported with the #91 delivery, not fixed in this stream.

        _log.Info($"Published host restarted on the same home ({description})");
    }

    private async Task StopHostGracefullyAsync(CancellationToken cancellationToken)
    {
        LongRestPublishedHost host = _host ?? throw new InvalidOperationException("There is no host to stop.");

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline.CancelAfter(TimeSpan.FromSeconds(60));

        using HttpClient client = new(LocalOllamaQualificationGuards.CreateLoopbackOnlyHttpHandler())
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_hostPort}/"),
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.Add(RetroDownfall.Arcanum.Api.Security.ArcanumApiHeaders.ApiKey, _masterApiKey!);

        using HttpResponseMessage response = await client.PostAsync("api/server/quit", content: null, deadline.Token);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        await host.WaitForExitAsync(deadline.Token);

        Assert.Equal(0, host.ExitCode);

        _retiredHosts.Add(host);

        _log.Info("Published host stopped gracefully (exit 0)");
    }

    private async Task AssertNativeAotMetadataAsync(CancellationToken cancellationToken)
    {
        System.Text.Json.JsonElement meta = await Api.GetMetaAsync(cancellationToken);

        Assert.True(meta.GetProperty("nativeAot").GetBoolean(), "The published host is not Native AOT.");

        Assert.Equal(Installation.ConfigurationDirectory, meta.GetProperty("grimoireDirectory").GetString());

        Assert.Equal(Installation.ConfigurationPath, meta.GetProperty("configPath").GetString());

        Assert.Equal(_hostPort, meta.GetProperty("port").GetInt32());

        Assert.False(meta.GetProperty("listenAny").GetBoolean());
    }

    private void Observe(string observation)
    {
        _observations.Add(observation);

        _log.Info("OBSERVED " + observation);
    }

    /// <summary>The wall clock a phase took, for the report.</summary>
    private static string Took(Stopwatch clock) =>
        clock.Elapsed.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
}
