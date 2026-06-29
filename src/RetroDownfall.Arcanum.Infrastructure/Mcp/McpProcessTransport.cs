using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Classifies an inbound JSON-RPC line read from an MCP server over stdio.
/// </summary>
public enum McpInboundKind
{
    Response,

    Notification,

    Request,
}

/// <summary>
/// One parsed inbound JSON-RPC message from the child process stdout (newline-delimited).
/// </summary>
public readonly record struct McpInboundEnvelope(
    McpInboundKind Kind,
    JsonRpcResponse? Response,
    JsonRpcNotification? Notification,
    JsonRpcRequest? Request);

/// <summary>
/// Spawns an MCP server subprocess and exchanges newline-delimited JSON-RPC 2.0 over redirected stdio.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: spawns real MCP subprocess over stdio
internal sealed class McpProcessTransport : IMcpTransport
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _fileName;

    private readonly string _arguments;

    private readonly IReadOnlyList<string>? _argumentList;

    private readonly IReadOnlyDictionary<string, string>? _environment;

    private readonly bool _stripUserEnvironment;

    private readonly IReadOnlySet<string>? _inheritEnvironmentAllowlist;

    private readonly string? _workingDirectory;

    private readonly McpJsonSerializerContext _json;

    private readonly int _maxJsonRpcLineBytes;

    private readonly Channel<McpInboundEnvelope> _inbound;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private readonly CancellationTokenSource _lifetimeCts = new();

    private Process? _process;

    private Task? _stdoutTask;

    private Task? _stderrTask;

    private bool _started;

    private volatile bool _disposed;

    // W2.5 Fix 5: idempotency guard (0 = live, 1 = disposed) shared between the
    // Exited handler and DisposeAsync so the Process is disposed exactly once
    // regardless of which path fires first (or if they race).

    private int _processDisposed;

    // W2.5 Fix 5 (test seam): exposes the live Process reference so the
    // child-exit dispose contract is unit-testable (assert the handle is
    // released after the Exited handler, and a later DisposeAsync is safe).
    internal Process? ProcessForTesting => _process;

    /// <summary>
    /// Optional callback when a line cannot be parsed as JSON-RPC or does not match a supported shape.
    /// </summary>
    public Action<string, Exception>? OnParseError { get; init; }

    /// <summary>
    /// Optional callback for each stderr line from the child (diagnostics).
    /// </summary>
    public Action<string>? OnStderrLine { get; init; }

    /// <summary>
    /// Optional callback when the child process exits and the inbound channel is completed.
    /// </summary>
    public Action? OnTransportEnded { get; init; }

    public McpProcessTransport(
        string fileName,
        string arguments,
        int maxJsonRpcLineBytes,
        McpJsonSerializerContext? jsonContext = null,
        int inboundChannelCapacity = 256,
        IReadOnlyList<string>? argumentList = null,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        bool stripUserEnvironment = false,
        IReadOnlySet<string>? inheritEnvironmentAllowlist = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (maxJsonRpcLineBytes < 1)
        {

            throw new ArgumentOutOfRangeException(nameof(maxJsonRpcLineBytes));

        }

        _fileName = fileName;

        _arguments = arguments;

        _maxJsonRpcLineBytes = maxJsonRpcLineBytes;

        _argumentList = argumentList;

        _environment = environment;

        _stripUserEnvironment = stripUserEnvironment;

        _inheritEnvironmentAllowlist = inheritEnvironmentAllowlist;

        _workingDirectory = workingDirectory;

        _json = jsonContext ?? McpJsonSerializerContext.Default;

        BoundedChannelOptions channelOptions = new(inboundChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        };

        _inbound = Channel.CreateBounded<McpInboundEnvelope>(channelOptions);
    }

    /// <summary>
    /// Inbound messages from stdout (responses, notifications, and server-originated requests).
    /// </summary>
    public ChannelReader<McpInboundEnvelope> InboundReader => _inbound.Reader;

    /// <summary>
    /// Starts the subprocess and background read loops. Call once.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started)
        {
            throw new InvalidOperationException("McpProcessTransport has already been started.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        _started = true;

        ProcessStartInfo psi = new()
        {
            FileName = _fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };

        if (_argumentList is not null)
        {

            foreach (string token in _argumentList)
            {

                psi.ArgumentList.Add(token);

            }

        }
        else
        {

            psi.Arguments = _arguments;

        }

        IReadOnlyDictionary<string, string>? scrubbedEnvironment = McpSecurityLimits.ScrubProcessEnvironment(
            _environment,
            _stripUserEnvironment,
            _inheritEnvironmentAllowlist);

        if (_stripUserEnvironment)
        {

            psi.Environment.Clear();

        }

        if (scrubbedEnvironment is not null)
        {

            foreach (KeyValuePair<string, string> kv in scrubbedEnvironment)
            {

                psi.Environment[kv.Key] = kv.Value;

            }

        }

        if (!string.IsNullOrWhiteSpace(_workingDirectory))
        {
            psi.WorkingDirectory = _workingDirectory;
        }

        Process process = new() { StartInfo = psi, EnableRaisingEvents = true };

        process.Exited += (_, _) =>
        {

            _inbound.Writer.TryComplete();

            OnTransportEnded?.Invoke();

            // W2.5 Fix 5: dispose the Process handle exactly once on child exit
            // so an undisposed Process + Exited handler do not accumulate under a
            // crash/restart loop. The guard is shared with DisposeAsync so the two
            // paths never double-dispose. Synchronous process.Dispose() only — do
            // NOT block a thread-pool thread on DisposeAsync's async work. The
            // process local is captured (rather than reading _process) so the rare
            // exit-before-_process-assignment race still releases the handle.

            if (Interlocked.CompareExchange(ref _processDisposed, 1, 0) == 0)
            {

                try
                {

                    process.Dispose();

                }

                catch (InvalidOperationException)
                {

                    // Already torn down.

                }

                catch (NotSupportedException)
                {

                }

                _process = null;

            }

        };

        if (!process.Start())
        {
            process.Dispose();

            throw new InvalidOperationException($"Failed to start process: {_fileName}");
        }

        _process = process;

        CancellationToken lifetimeToken = _lifetimeCts.Token;

        _stdoutTask = Task.Run(() => ReadStdoutLoopAsync(lifetimeToken), CancellationToken.None);

        _stderrTask = Task.Run(() => ReadStderrLoopAsync(lifetimeToken), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Serializes a JSON-RPC request and writes it as one UTF-8 line (LF-terminated) to stdin, then flushes.
    /// </summary>
    public async Task WriteRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Process? process = _process;

        if (process is null)
        {
            throw new InvalidOperationException("StartAsync must be called before writing.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string json = JsonSerializer.Serialize(request, _json.JsonRpcRequest);

            McpOutboundLineGuard.Enforce(json, _maxJsonRpcLineBytes);

            StreamWriter stdin = process.StandardInput;
            await stdin.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.WriteAsync('\n').ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Serializes a JSON-RPC notification (no <c>id</c>) and writes it as one UTF-8 line (LF-terminated) to stdin, then flushes.
    /// </summary>
    public async Task WriteNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Process? process = _process;

        if (process is null)
        {
            throw new InvalidOperationException("StartAsync must be called before writing.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            string json = JsonSerializer.Serialize(notification, _json.JsonRpcNotification);

            McpOutboundLineGuard.Enforce(json, _maxJsonRpcLineBytes);

            StreamWriter stdin = process.StandardInput;
            await stdin.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stdin.WriteAsync('\n').ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _lifetimeCts.CancelAsync().ConfigureAwait(false);

        Process? process = _process;

        // W2.5 Fix 5: share the _processDisposed guard with the Exited handler so
        // the two paths never double-dispose the Process. If the child already
        // exited and the Exited handler disposed it, this CAS fails and we skip
        // the kill+dispose (the handle is already released).

        if (process is not null && Interlocked.CompareExchange(ref _processDisposed, 1, 0) == 0)
        {

            try
            {

                if (!process.HasExited)
                {

                    process.Kill(entireProcessTree: true);

                }

            }

            catch (InvalidOperationException)
            {

                // Process may already be torn down.

            }

            catch (NotSupportedException)
            {

                // entireProcessTree not supported on some targets; best-effort single process.

                try
                {

                    if (!process.HasExited)
                    {

                        process.Kill();

                    }

                }

                catch (InvalidOperationException)
                {

                }

            }

            process.Dispose();

        }

        _process = null;

        _inbound.Writer.TryComplete();

        Task stdout = _stdoutTask ?? Task.CompletedTask;

        Task stderr = _stderrTask ?? Task.CompletedTask;

        await Task.WhenAll(
                AwaitTaskGracefullyAsync(stdout),
                AwaitTaskGracefullyAsync(stderr))
            .ConfigureAwait(false);

        _lifetimeCts.Dispose();

        _writeLock.Dispose();
    }

    private static async Task AwaitTaskGracefullyAsync(Task task)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);

            return;
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        try
        {
            await task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Swallow — disposal best-effort.
        }
    }

    private async Task ReadStdoutLoopAsync(CancellationToken cancellationToken)
    {
        Process? process = _process;

        if (process is null)
        {
            return;
        }

        StreamReader stdout = process.StandardOutput;

        McpStdioLineReader lineReader = new(stdout);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;

                try
                {

                    line = await lineReader.ReadLineUtf8CappedAsync(_maxJsonRpcLineBytes, cancellationToken).ConfigureAwait(false);

                }
                catch (JsonException ex)
                {

                    OnParseError?.Invoke("(line exceeds size cap)", ex);

                    continue;

                }

                catch (ObjectDisposedException)
                {

                    // W2.5 Fix 5: the Process/streams were disposed (child exited
                    // and the Exited handler released the handle, or DisposeAsync
                    // tore it down). Stop draining cleanly instead of faulting the
                    // background read task.

                    break;

                }

                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    McpInboundEnvelope envelope = McpInboundJsonRpc.ParseInbound(line, _json, _maxJsonRpcLineBytes);

                    await _inbound.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    OnParseError?.Invoke(line, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (ObjectDisposedException)
        {
            // W2.5 Fix 5: Process/streams disposed mid-read; stop cleanly.
        }
        finally
        {
            _inbound.Writer.TryComplete();
        }
    }

    private async Task ReadStderrLoopAsync(CancellationToken cancellationToken)
    {
        Process? process = _process;

        if (process is null)
        {
            return;
        }

        StreamReader stderr = process.StandardError;

        McpStdioLineReader lineReader = new(stderr);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;

                try
                {

                    line = await lineReader.ReadLineUtf8CappedAsync(_maxJsonRpcLineBytes, cancellationToken).ConfigureAwait(false);

                }
                catch (JsonException)
                {

                    OnStderrLine?.Invoke(
                        $"[stderr line truncated: exceeded {_maxJsonRpcLineBytes} UTF-8 bytes]");

                    continue;

                }

                catch (ObjectDisposedException)
                {

                    // W2.5 Fix 5: the Process/streams were disposed (child exited
                    // and the Exited handler released the handle, or DisposeAsync
                    // tore it down). Stop draining cleanly instead of faulting the
                    // background read task.

                    break;

                }

                if (line is null)
                {
                    break;
                }

                OnStderrLine?.Invoke(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (ObjectDisposedException)
        {
            // W2.5 Fix 5: Process/streams disposed mid-read; stop cleanly.
        }
    }

}
