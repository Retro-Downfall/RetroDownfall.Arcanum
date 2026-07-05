using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Events;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Infrastructure.LlamaCpp;

[ExcludeFromCodeCoverage] // Reason: spawns and manages llama-server child processes
public sealed class LlamaServerManager : ILlamaServerManager
{

    private const int MaxDiagnosticLines = 100;

    private const int MaxPortAttempts = 32;

    private readonly IReliquary _modelCache;

    private readonly IEventBus _eventBus;

    private readonly IOptionsMonitor<ArcanumSettings> _optionsMonitor;

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly ILogger<LlamaServerManager> _logger;

    private readonly ConcurrentDictionary<string, Lazy<Task<ManagedLlamaServer>>> _servers = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _executableLock = new();

    private string? _resolvedExecutablePath;

    public LlamaServerManager(
        IReliquary modelCache,
        IEventBus eventBus,
        IOptionsMonitor<ArcanumSettings> optionsMonitor,
        IHttpClientFactory httpClientFactory,
        ILogger<LlamaServerManager> logger)
    {

        _modelCache = modelCache;

        _eventBus = eventBus;

        _optionsMonitor = optionsMonitor;

        _httpClientFactory = httpClientFactory;

        _logger = logger;

    }

    public async Task<Result<LlamaServerInfo>> EnsureServerAsync(
        string cacheKey,
        string? sourceUrl,
        int? gpuLayersOverride,
        int? portOverride,
        CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(cacheKey))
        {
            return Result<LlamaServerInfo>.Failure(new Error("Llama.InvalidCacheKey", "Cache key is required."));
        }

        LlamaServerInfo? running = TryGetRunningServer(cacheKey);

        if (running is not null)
        {
            if (gpuLayersOverride.HasValue || portOverride.HasValue)
            {
                _logger.LogWarning(
                    "Llama server for {CacheKey} is already running; ignoring gpuLayers/port overrides.",
                    cacheKey);
            }

            return Result<LlamaServerInfo>.Success(running);
        }

        LlamaCppSettings configuredLlama = _optionsMonitor.CurrentValue.LlamaCpp ?? new LlamaCppSettings();

        if (LlamaAdditionalArgumentsPolicy.ContainsReservedBindingArgument(configuredLlama.AdditionalArguments, out string? rejectedToken))
        {

            return Result<LlamaServerInfo>.Failure(new Error(
                "Llama.InvalidArgument",
                $"AdditionalArguments must not include '{rejectedToken}'. Host and port are managed by Arcanum."));

        }

        if (!_modelCache.IsCached(cacheKey))
        {
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                return Result<LlamaServerInfo>.Failure(new Error(
                    "Llama.ModelSourceMissing",
                    $"Model '{cacheKey}' is not cached and no source URL is configured. Pull the model first or add it to llamaCpp.modelMap."));
            }

            if (!LlamaSourceUrl.TryValidate(sourceUrl, out string normalizedUrl))
            {
                return Result<LlamaServerInfo>.Failure(new Error(
                    "Llama.InvalidSourceUrl",
                    "Source URL must be an absolute http or https URI."));
            }

            LlamaCppSettings llamaSettings = _optionsMonitor.CurrentValue.LlamaCpp ?? new LlamaCppSettings();

            string? expectedSha256 = null;

            if (llamaSettings.ModelSha256Map?.TryGetValue(cacheKey, out string? pinnedSha256) == true
                && !string.IsNullOrWhiteSpace(pinnedSha256))
            {

                expectedSha256 = pinnedSha256.Trim();

            }

            Result<string> download = await _modelCache.EnsureModelAsync(
                cacheKey,
                normalizedUrl,
                expectedSha256: expectedSha256,
                progress: null,
                cancellationToken).ConfigureAwait(false);

            if (download.IsFailure)
            {
                return Result<LlamaServerInfo>.Failure(download.Error);
            }
        }

        string? executable = TryResolveExecutablePath();

        if (executable is null)
        {
            return Result<LlamaServerInfo>.Failure(new Error(
                "Llama.ExecutableNotFound",
                "llama-server executable was not found. Install llama.cpp or set Arcanum:LlamaCpp:ServerExecutablePath."));
        }

        // The factory delegate runs at most once (LazyThreadSafetyMode.ExecutionAndPublication) and
        // is shared by every caller racing to start this cache key — not just the first one whose
        // EnsureServerAsync call happens to win the GetOrAdd. It must therefore use a server-lifetime
        // token (CancellationToken.None), never this specific call's cancellationToken: otherwise the
        // first caller cancelling their own request (e.g. an aborted HTTP request) would cancel the
        // spawn/health-check for every other concurrent or later caller waiting on the same lazy
        // value, even though they never asked to cancel anything. Each caller's own cancellationToken
        // still governs only their own wait below via WaitAsync. StartManagedServerAsync's own
        // start/health-probe timeouts (not this token) bound how long the spawn attempt can run.
        Lazy<Task<ManagedLlamaServer>> lazy = _servers.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<ManagedLlamaServer>>(
                () => StartManagedServerAsync(cacheKey, executable, gpuLayersOverride, portOverride, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            ManagedLlamaServer server = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);

            return Result<LlamaServerInfo>.Success(server.ToInfo());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The shared startup task is governed by a server-lifetime token, not this caller's
            // token (see the factory above) — it is deliberately NOT removed or stopped here. Other
            // concurrent or future callers may be waiting on, or depending on, that same in-flight
            // (or already-succeeded) startup; tearing it down just because this one caller's own
            // request was cancelled would wrongly take the server away from everyone else. This
            // simply lets this caller's own wait observe its own cancellation.
            throw;
        }
        catch (Exception ex)
        {
            if (lazy.IsValueCreated)
            {
                Task task = lazy.Value;

                if (task.IsFaulted || task.IsCanceled)
                {
                    _servers.TryRemove(new KeyValuePair<string, Lazy<Task<ManagedLlamaServer>>>(cacheKey, lazy));
                }
            }

            _logger.LogError(ex, "Failed to ensure llama-server for {CacheKey}.", cacheKey);

            return Result<LlamaServerInfo>.Failure(new Error("Llama.StartFailed", ex.Message));
        }

    }

    public async Task<IDisposable> AcquireSlotAsync(string cacheKey, CancellationToken cancellationToken)
    {

        if (!_servers.TryGetValue(cacheKey, out Lazy<Task<ManagedLlamaServer>>? lazy) || !lazy.IsValueCreated)
        {
            throw new InvalidOperationException($"No running llama-server for cache key '{cacheKey}'.");
        }

        ManagedLlamaServer server = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);

        int maxConcurrent = ArcanumSettingClamps.LlamaMaxConcurrentRequests(
            _optionsMonitor.CurrentValue.LlamaCpp?.MaxConcurrentRequests ?? new LlamaCppSettings().MaxConcurrentRequests);

        int queueDepth = maxConcurrent * 4;

        if (!await server.WaitQueueAdmissionAsync(queueDepth, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Llama.Overloaded");
        }

        try
        {
            await server.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            server.ReleaseQueueAdmission();

            throw;
        }

        server.ReleaseQueueAdmission();

        return new LlamaConcurrencySlot(server.Semaphore);

    }

    public bool IsModelInUse(string cacheKey)
    {

        if (!_servers.TryGetValue(cacheKey, out Lazy<Task<ManagedLlamaServer>>? lazy) || !lazy.IsValueCreated)
        {
            return false;
        }

        if (!lazy.Value.IsCompletedSuccessfully)
        {
            return true;
        }

        ManagedLlamaServer server = lazy.Value.Result;

        return server.State is LlamaServerState.Starting or LlamaServerState.Running;

    }

    public bool IsLlamaServerAvailable() => TryResolveExecutablePath() is not null;

    public LlamaServerInfo? TryGetRunningServer(string cacheKey)
    {

        if (!_servers.TryGetValue(cacheKey, out Lazy<Task<ManagedLlamaServer>>? lazy) || !lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully)
        {
            return null;
        }

        ManagedLlamaServer server = lazy.Value.Result;

        if (server.State is LlamaServerState.Running or LlamaServerState.Starting)
        {
            return server.ToInfo();
        }

        return null;

    }

    public async Task<Result> StopAsync(string cacheKey, CancellationToken cancellationToken)
    {

        if (!_servers.TryRemove(cacheKey, out Lazy<Task<ManagedLlamaServer>>? lazy))
        {
            return Result.Failure(new Error("Llama.ServerNotFound", $"No managed server for cache key '{cacheKey}'."));
        }

        if (lazy.IsValueCreated)
        {
            try
            {
                ManagedLlamaServer server = await lazy.Value.ConfigureAwait(false);

                await server.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping llama-server for {CacheKey}.", cacheKey);
            }
        }

        return Result.Success();

    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {

        string[] keys = _servers.Keys.ToArray();

        foreach (string key in keys)
        {
            await StopAsync(key, cancellationToken).ConfigureAwait(false);
        }

    }

    public IReadOnlyList<LlamaServerInfo> ListServers()
    {

        List<LlamaServerInfo> results = [];

        foreach (KeyValuePair<string, Lazy<Task<ManagedLlamaServer>>> pair in _servers)
        {
            if (!pair.Value.IsValueCreated || !pair.Value.Value.IsCompletedSuccessfully)
            {
                continue;
            }

            results.Add(pair.Value.Value.Result.ToInfo());
        }

        return results;

    }

    private async Task<ManagedLlamaServer> StartManagedServerAsync(
        string cacheKey,
        string executablePath,
        int? gpuLayersOverride,
        int? portOverride,
        CancellationToken cancellationToken)
    {

        string? modelPath = _modelCache.GetModelPath(cacheKey);

        if (modelPath is null)
        {
            throw new InvalidOperationException($"Cached model not found for key '{cacheKey}'.");
        }

        LlamaCppSettings settings = _optionsMonitor.CurrentValue.LlamaCpp ?? new LlamaCppSettings();

        int contextSize = ArcanumSettingClamps.LlamaContextSize(settings.ContextSize);

        int gpuLayers = ArcanumSettingClamps.LlamaGpuLayers(gpuLayersOverride ?? settings.GpuLayers);

        int startTimeoutSeconds = ArcanumSettingClamps.LlamaStartTimeoutSeconds(settings.StartTimeoutSeconds);

        int healthTimeoutSeconds = ArcanumSettingClamps.LlamaHealthProbeTimeoutSeconds(settings.HealthProbeTimeoutSeconds);

        int maxConcurrent = ArcanumSettingClamps.LlamaMaxConcurrentRequests(settings.MaxConcurrentRequests);

        ManagedLlamaServer managed = new(
            cacheKey,
            maxConcurrent,
            ArcanumSettingClamps.LlamaShutdownTimeoutSeconds(settings.ShutdownTimeoutSeconds),
            _eventBus,
            _logger,
            onUnexpectedExit: key => _servers.TryRemove(key, out _));

        managed.SetState(LlamaServerState.Starting);

        PublishEvent(managed, "Starting llama-server.");

        Exception? lastError = null;

        for (int attempt = 0; attempt < MaxPortAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int port = portOverride.HasValue ? ClampPort(portOverride.Value) : TryPickFreePort(settings);

            if (port < 0)
            {
                throw new InvalidOperationException("No free port available in the configured port range.");
            }

            try
            {
                Process process = StartProcess(executablePath, modelPath, contextSize, gpuLayers, port, settings);

                managed.AttachProcess(process, port);

                bool healthy = await WaitForHealthyAsync(port, startTimeoutSeconds, healthTimeoutSeconds, managed, cancellationToken).ConfigureAwait(false);

                if (healthy && !process.HasExited)
                {
                    managed.SetState(LlamaServerState.Running);

                    PublishEvent(managed, "llama-server is healthy.");

                    return managed;
                }

                lastError = new InvalidOperationException("llama-server failed health check or exited during startup.");

                await managed.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lastError = ex;

                _logger.LogWarning(ex, "llama-server spawn attempt {Attempt} failed for {CacheKey} on port {Port}.", attempt + 1, cacheKey, port);

                await managed.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (portOverride.HasValue)
            {
                break;
            }
        }

        managed.SetState(LlamaServerState.Error, lastError?.Message);

        PublishEvent(managed, lastError?.Message ?? "Failed to start llama-server.");

        throw lastError ?? new InvalidOperationException("Failed to start llama-server.");

    }

    private Process StartProcess(
        string executablePath,
        string modelPath,
        int contextSize,
        int gpuLayers,
        int port,
        LlamaCppSettings settings)
    {

        ProcessStartInfo psi = new()
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("--model");

        psi.ArgumentList.Add(modelPath);

        psi.ArgumentList.Add("--ctx-size");

        psi.ArgumentList.Add(contextSize.ToString());

        psi.ArgumentList.Add("--port");

        psi.ArgumentList.Add(port.ToString());

        psi.ArgumentList.Add("--host");

        psi.ArgumentList.Add("127.0.0.1");

        AppendGpuLayersArgument(psi, gpuLayers);

        if (settings.AdditionalArguments is { Length: > 0 })
        {
            foreach (string arg in settings.AdditionalArguments)
            {
                if (!string.IsNullOrWhiteSpace(arg))
                {
                    psi.ArgumentList.Add(arg);
                }
            }
        }

        Process process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");

        return process;

    }

    internal static void AppendGpuLayersArgument(ProcessStartInfo psi, int gpuLayers)
    {

        int layers = ArcanumSettingClamps.LlamaGpuLayers(gpuLayers);

        psi.ArgumentList.Add("--n-gpu-layers");

        if (layers < 0)
        {
            psi.ArgumentList.Add("999");
        }
        else
        {
            psi.ArgumentList.Add(layers.ToString());
        }

    }

    private async Task<bool> WaitForHealthyAsync(
        int port,
        int startTimeoutSeconds,
        int healthTimeoutSeconds,
        ManagedLlamaServer managed,
        CancellationToken cancellationToken)
    {

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeoutCts.CancelAfter(TimeSpan.FromSeconds(startTimeoutSeconds));

        HttpClient client = _httpClientFactory.CreateClient();

        Uri healthUri = new($"http://127.0.0.1:{port}/health");

        while (!timeoutCts.IsCancellationRequested)
        {
            Process? process = managed.Process;

            if (process is null || process.HasExited)
            {
                return false;
            }

            try
            {
                using CancellationTokenSource probeCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);

                probeCts.CancelAfter(TimeSpan.FromSeconds(healthTimeoutSeconds));

                using HttpResponseMessage response = await client.GetAsync(healthUri, probeCts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // probe timeout; retry until start timeout
            }
            catch (HttpRequestException)
            {
                // server not ready yet
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), timeoutCts.Token).ConfigureAwait(false);
        }

        return false;

    }

    internal static int ClampPort(int port) => Math.Clamp(port, 1, 65_535);

    internal static int ComputeCandidatePort(int portStart, int portRange, int startOffset, int i) =>
        ClampPort(portStart + ((startOffset + i) % portRange));

    private int TryPickFreePort(LlamaCppSettings settings)
    {

        int portStart = ArcanumSettingClamps.LlamaPortStart(settings.PortStart);

        int portRange = ArcanumSettingClamps.LlamaPortRange(settings.PortRange);

        Random random = Random.Shared;

        int startOffset = random.Next(0, Math.Max(1, portRange));

        for (int i = 0; i < portRange; i++)
        {
            int port = ComputeCandidatePort(portStart, portRange, startOffset, i);

            if (IsPortFree(port))
            {
                return port;
            }
        }

        return -1;

    }

    private static bool IsPortFree(int port)
    {

        try
        {
            using TcpListener listener = new(IPAddress.Loopback, port);

            listener.Start();

            listener.Stop();

            return true;
        }
        catch (SocketException)
        {
            return false;
        }

    }

    private string? TryResolveExecutablePath()
    {

        lock (_executableLock)
        {
            if (_resolvedExecutablePath is not null)
            {
                return _resolvedExecutablePath;
            }

            LlamaCppSettings settings = _optionsMonitor.CurrentValue.LlamaCpp ?? new LlamaCppSettings();

            if (!string.IsNullOrWhiteSpace(settings.ServerExecutablePath))
            {
                string path = Path.IsPathRooted(settings.ServerExecutablePath)
                    ? settings.ServerExecutablePath
                    : Path.GetFullPath(settings.ServerExecutablePath);

                if (File.Exists(path))
                {
                    _resolvedExecutablePath = path;

                    return _resolvedExecutablePath;
                }

                return null;
            }

            string? fromPath = FindOnPath("llama-server");

            if (fromPath is not null)
            {
                _resolvedExecutablePath = fromPath;

                return _resolvedExecutablePath;
            }

            if (OperatingSystem.IsWindows())
            {
                fromPath = FindOnPath("llama-server.exe");

                if (fromPath is not null)
                {
                    _resolvedExecutablePath = fromPath;

                    return _resolvedExecutablePath;
                }
            }

            return null;
        }

    }

    private static string? FindOnPath(string fileName)
    {

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        string[] segments = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string probe = OperatingSystem.IsWindows() ? fileName : fileName;

        foreach (string segment in segments)
        {
            try
            {
                string candidate = Path.Combine(segment, probe);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore invalid PATH segments
            }
        }

        return null;

    }

    private void PublishEvent(ManagedLlamaServer server, string? message)
    {

        _eventBus.Publish(new LlamaServerEvent(
            DateTimeOffset.UtcNow,
            server.CacheKey,
            server.State,
            server.Port,
            message));

    }

    internal sealed class ManagedLlamaServer
    {

        private readonly object _gate = new();

        private readonly Queue<object> _diagnosticLines = new();

        private readonly IEventBus _eventBus;

        private readonly ILogger _logger;

        private readonly Action<string> _onUnexpectedExit;

        private int _waitingForSlot;

        private bool _processDetached;

        public ManagedLlamaServer(
            string cacheKey,
            int maxConcurrent,
            int shutdownTimeoutSeconds,
            IEventBus eventBus,
            ILogger logger,
            Action<string> onUnexpectedExit)
        {

            CacheKey = cacheKey;

            Semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);

            ShutdownTimeoutSeconds = shutdownTimeoutSeconds;

            _eventBus = eventBus;

            _logger = logger;

            _onUnexpectedExit = onUnexpectedExit;

        }

        public int ShutdownTimeoutSeconds { get; }

        public string CacheKey { get; }

        public SemaphoreSlim Semaphore { get; }

        public LlamaServerState State { get; private set; } = LlamaServerState.Stopped;

        public int Port { get; private set; }

        public Process? Process { get; private set; }

        public DateTimeOffset? StartedAt { get; private set; }

        public string? LastError { get; private set; }

        public void AttachProcess(Process process, int port)
        {

            lock (_gate)
            {
                Process = process;

                Port = port;

                StartedAt = DateTimeOffset.UtcNow;

                process.EnableRaisingEvents = true;

                process.OutputDataReceived += OnOutputDataReceived;

                process.ErrorDataReceived += OnErrorDataReceived;

                process.Exited += OnExited;

                process.BeginOutputReadLine();

                process.BeginErrorReadLine();
            }

            // Recorded outside the process's own lifecycle so a future Arcanum run can sweep this
            // process if the host is killed before StopAsync/DetachAndDisposeProcess ever runs — see
            // LlamaProcessRegistry and LlamaServerLifecycleHostedService.StartAsync.
            LlamaProcessRegistry.Record(process, CacheKey, _logger);

        }

        public void SetState(LlamaServerState state, string? error = null)
        {

            lock (_gate)
            {
                State = state;

                LastError = error;
            }

        }

        public LlamaServerInfo ToInfo()
        {

            lock (_gate)
            {
                return new LlamaServerInfo
                {
                    CacheKey = CacheKey,
                    State = State,
                    Port = Port,
                    Endpoint = Port > 0 ? $"http://127.0.0.1:{Port}/v1" : string.Empty,
                    ProcessId = Process?.Id,
                    StartedAt = StartedAt,
                    LastError = LastError,
                };
            }

        }

        public async Task<bool> WaitQueueAdmissionAsync(int queueDepth, CancellationToken cancellationToken)
        {

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int waiting;

                lock (_gate)
                {
                    waiting = _waitingForSlot;
                }

                if (waiting < queueDepth)
                {
                    lock (_gate)
                    {
                        if (_waitingForSlot < queueDepth)
                        {
                            _waitingForSlot++;

                            return true;
                        }
                    }
                }

                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }

        }

        public void ReleaseQueueAdmission()
        {

            lock (_gate)
            {
                if (_waitingForSlot > 0)
                {
                    _waitingForSlot--;
                }
            }

        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {

            Process? process;

            lock (_gate)
            {
                if (State is LlamaServerState.Stopped or LlamaServerState.Stopping)
                {
                    return;
                }

                State = LlamaServerState.Stopping;

                process = Process;

                Process = null;
            }

            _eventBus.Publish(new LlamaServerEvent(
                DateTimeOffset.UtcNow,
                CacheKey,
                LlamaServerState.Stopping,
                Port,
                "Stopping llama-server."));

            if (process is null)
            {
                SetState(LlamaServerState.Stopped);

                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    int shutdownSeconds = ShutdownTimeoutSeconds;

                    if (OperatingSystem.IsWindows())
                    {
                        // llama-server is always spawned with CreateNoWindow=true (see StartProcess),
                        // so it never has a window handle — CloseMainWindow() is a guaranteed no-op
                        // here, not a graceful-shutdown request. There is nothing extra to signal on
                        // Windows beyond waiting for the process to exit within the full configured
                        // timeout before falling back to a forceful tree kill.
                        using CancellationTokenSource killCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                        killCts.CancelAfter(TimeSpan.FromSeconds(shutdownSeconds));

                        try
                        {
                            await process.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            ProcessTreeKiller.TryKillEntireTree(process, _logger, $"llama-server {CacheKey}");
                        }
                    }
                    else
                    {

                        try
                        {

                            if (!process.HasExited)
                            {

                                // entireProcessTree: true — a single-process kill only signals the
                                // root pid, leaving any llama-server worker/child processes running
                                // until the timeout below forces a full-tree kill anyway.
                                process.Kill(entireProcessTree: true);

                            }

                        }
                        catch (Exception ex)
                        {

                            _logger.LogDebug(ex, "Graceful process-tree kill failed for llama-server {CacheKey}.", CacheKey);

                        }

                        using CancellationTokenSource killCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                        // Uses the full configured shutdown timeout (not an arbitrary 5s cap) so a
                        // slower-to-exit llama-server is not force-killed sooner than the operator
                        // configured, matching the Windows branch above.
                        killCts.CancelAfter(TimeSpan.FromSeconds(shutdownSeconds));

                        try
                        {

                            await process.WaitForExitAsync(killCts.Token).ConfigureAwait(false);

                        }
                        catch (OperationCanceledException)
                        {

                            ProcessTreeKiller.TryKillEntireTree(process, _logger, $"llama-server {CacheKey}");

                        }

                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during llama-server shutdown for {CacheKey}.", CacheKey);

                ProcessTreeKiller.TryKillEntireTree(process, _logger, $"llama-server {CacheKey}");
            }

            SetState(LlamaServerState.Stopped);

            _eventBus.Publish(new LlamaServerEvent(
                DateTimeOffset.UtcNow,
                CacheKey,
                LlamaServerState.Stopped,
                Port,
                "llama-server stopped."));

            DetachAndDisposeProcess(process);

        }

        private void DetachAndDisposeProcess(Process? process)
        {

            lock (_gate)
            {

                if (_processDetached || process is null)
                {

                    return;

                }

                _processDetached = true;

            }

            LlamaProcessRegistry.Remove(process.Id, _logger);

            try
            {

                process.CancelOutputRead();

            }
            catch (Exception ex)
            {

                _logger.LogDebug(ex, "CancelOutputRead failed for llama-server {CacheKey}.", CacheKey);

            }

            try
            {

                process.CancelErrorRead();

            }
            catch (Exception ex)
            {

                _logger.LogDebug(ex, "CancelErrorRead failed for llama-server {CacheKey}.", CacheKey);

            }

            process.OutputDataReceived -= OnOutputDataReceived;

            process.ErrorDataReceived -= OnErrorDataReceived;

            process.Exited -= OnExited;

            try
            {

                process.Dispose();

            }
            catch (Exception ex)
            {

                _logger.LogDebug(ex, "Process dispose failed for llama-server {CacheKey}.", CacheKey);

            }

        }

        private void OnExited(object? sender, EventArgs e)
        {

            Process? process;

            LlamaServerState previous;

            lock (_gate)
            {
                previous = State;

                if (previous is LlamaServerState.Stopping or LlamaServerState.Stopped)
                {
                    return;
                }

                if (previous is not (LlamaServerState.Running or LlamaServerState.Starting))
                {
                    return;
                }

                State = LlamaServerState.Error;

                LastError = "llama-server process exited unexpectedly.";

                process = Process;

                Process = null;
            }

            _eventBus.Publish(new LlamaServerEvent(
                DateTimeOffset.UtcNow,
                CacheKey,
                LlamaServerState.Error,
                Port,
                LastError));

            _logger.LogError("llama-server for {CacheKey} exited unexpectedly.", CacheKey);

            _onUnexpectedExit(CacheKey);

            DetachAndDisposeProcess(process);

        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs e) => AppendDiagnostic(e.Data);

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs e) => AppendDiagnostic(e.Data);

        private void AppendDiagnostic(string? line)
        {

            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (_gate)
            {
                _diagnosticLines.Enqueue(line);

                while (_diagnosticLines.Count > MaxDiagnosticLines)
                {
                    _diagnosticLines.Dequeue();
                }
            }

        }

    }

    private sealed class LlamaConcurrencySlot(SemaphoreSlim semaphore) : IDisposable
    {

        private int _disposed;

        public void Dispose()
        {

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {

                semaphore.Release();

            }
            catch (ObjectDisposedException)
            {

                // server stopped while inference was in flight

            }

        }

    }

}
