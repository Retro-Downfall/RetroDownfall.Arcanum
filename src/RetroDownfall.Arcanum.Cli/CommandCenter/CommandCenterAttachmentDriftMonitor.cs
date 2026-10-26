using System.ComponentModel;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>Filesystem hints invalidate only; attachment state and badges remain server-owned.</summary>
internal sealed class CommandCenterAttachmentDriftMonitor(
    ArcanumApiClient apiClient,
    ILogger<CommandCenterAttachmentDriftMonitor> logger) : IAsyncDisposable, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    private readonly Func<string, FileSystemWatcher> _createWatcher = static path => new FileSystemWatcher(path);

    private readonly Action<FileSystemWatcher> _enableWatcher = static watcher => watcher.EnableRaisingEvents = true;

    private readonly TimeProvider _timeProvider = TimeProvider.System;

    private readonly object _lifecycle = new();

    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });

    private FileSystemWatcher? _watcher;

    private CancellationTokenSource? _lifetime;

    private Task? _pump;

    private Task? _stopTask;

    private CommandCenterState? _state;

    private int _pendingReasons;

    private CancellationToken _runToken;

    private bool _disposed;

    [Flags]
    private enum RefreshReason
    {
        None = 0,
        Explicit = 1,
        FileSystem = 2,
        PendingIndex = 4,
        WatcherError = 8,
    }

    internal CommandCenterAttachmentDriftMonitor(
        ArcanumApiClient apiClient,
        ILogger<CommandCenterAttachmentDriftMonitor> logger,
        Func<string, FileSystemWatcher> createWatcher,
        Action<FileSystemWatcher> enableWatcher,
        TimeProvider timeProvider)
        : this(apiClient, logger)
    {
        _createWatcher = createWatcher;

        _enableWatcher = enableWatcher;

        _timeProvider = timeProvider;
    }

    public void Start(CommandCenterState state, ChannelWriter<CommandCenterUiUpdate> uiUpdates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        ArgumentNullException.ThrowIfNull(uiUpdates);

        lock (_lifecycle)
        {
            if (_disposed || _lifetime is not null || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            CancellationToken lifetime = _lifetime.Token;

            _runToken = lifetime;

            _state = state;

            // Explicit refresh and indexing polling do not depend on an optional filesystem watcher.
            // Capture the token before scheduling so immediate disposal cannot invalidate its source.
            _pump = Task.Run(() => PumpAsync(state, uiUpdates, lifetime));

            string? unavailable = null;

            try
            {
                if (!Directory.Exists(state.WorkingDirectory))
                {
                    unavailable = "workspace_unavailable";
                }
                else
                {
                    _watcher = _createWatcher(state.WorkingDirectory);

                    _watcher.IncludeSubdirectories = true;

                    _watcher.InternalBufferSize = 16 * 1024;

                    _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;

                    _watcher.Created += OnChanged;

                    _watcher.Changed += OnChanged;

                    _watcher.Deleted += OnChanged;

                    _watcher.Renamed += OnRenamed;

                    _watcher.Error += OnError;

                    _enableWatcher(_watcher);
                }
            }
            catch (Exception exception) when (IsExpectedWatcherFailure(exception)
                || (exception is ArgumentException && !Directory.Exists(state.WorkingDirectory)))
            {
                unavailable = WatcherFailureReason(exception);
            }

            if (unavailable is not null)
            {
                FileSystemWatcher? partial = _watcher;

                _watcher = null;

                DisposeWatcher(partial);

                logger.LogWarning("Automatic attachment drift notifications unavailable ({Reason}). Use /attachments to recheck; restore workspace access and restart Command Center to retry notifications. Explicit attachment refresh and pending indexing checks continue.", unavailable);

                state.Log.Append(SessionLogEntryKind.Status, "Automatic attachment drift notifications unavailable. Use /attachments to recheck; restore workspace access and restart Command Center to retry notifications. Explicit attachment refresh and pending indexing checks continue.");

                _ = uiUpdates.TryWrite(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog));
            }

            Signal(RefreshReason.Explicit);
        }
    }

    public void RequestRefresh() => Signal(RefreshReason.Explicit);

    internal static IReadOnlyList<string> ApplyBackendSnapshot(
        CommandCenterState state,
        IReadOnlyList<SessionAttachmentDto> current)
    {
        Dictionary<Guid, SessionAttachmentDto> previous = state.SessionAttachments
            .ToDictionary(static row => row.Id);

        List<string> notices = [];

        foreach (SessionAttachmentDto row in current)
        {
            if (!previous.TryGetValue(row.Id, out SessionAttachmentDto? prior))
            {
                continue;
            }

            string before = Badge(prior);

            string after = Badge(row);

            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                continue;
            }

            notices.Add(
                $"[{after}] {row.LogicalKey} v{row.Version} "
                + $"loaded={ShortHash(row.ContentSha256)} "
                + $"disk={ShortHash(row.LastObservedSourceContentSha256)}");
        }

        state.SessionAttachments = current.ToArray();

        return notices;
    }

    private async Task PumpAsync(CommandCenterState state, ChannelWriter<CommandCenterUiUpdate> uiUpdates, CancellationToken cancellationToken)
    {
        long? lastCompletedRequest = null;

        try
        {
            while (await _signals.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                DrainWakeups();

                await Task.Delay(Debounce, _timeProvider, cancellationToken).ConfigureAwait(false);

                if (lastCompletedRequest is { } prior)
                {
                    TimeSpan remaining = MinimumInterval - _timeProvider.GetElapsedTime(prior);

                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, _timeProvider, cancellationToken).ConfigureAwait(false);
                    }
                }

                // Drain before exchanging the reasons: a concurrent signal is either included now
                // or leaves a wakeup for the next cycle. Explicit/poll reasons cannot be dropped.
                DrainWakeups();

                RefreshReason reasons = (RefreshReason)Interlocked.Exchange(ref _pendingReasons, 0);

                (Guid? SessionId, long Revision) binding = state.AttachmentBinding;

                if (reasons == RefreshReason.None
                    || (reasons == RefreshReason.FileSystem && !HasWorkspaceSources(state))
                    || binding.SessionId is not { } sessionId)
                {
                    continue;
                }

                Result<SessionAttachmentDto[]> result = await apiClient.GetSessionAttachmentsAsync(sessionId, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                // Pace whole monitor operations from completion. Authentication preparation may
                // delay the API's first send; internal API authentication/retries stay its contract.
                lastCompletedRequest = _timeProvider.GetTimestamp();

                bool refreshLog = false;

                bool refreshFooter = false;

                bool pending;

                // A Session switch and snapshot admission share one short synchronous region.
                // The revision also rejects a response from an earlier visit to the same Session.
                lock (state.AttachmentProjectionGate)
                {
                    if (state.AttachmentBinding != binding)
                    {
                        continue;
                    }

                    if (result.IsSuccess)
                    {
                        string? indexingBefore = state.AttachmentIndexingStatusText;

                        IReadOnlyList<string> notices = ApplyBackendSnapshot(state, result.Value ?? []);

                        foreach (string notice in notices)
                        {
                            state.Log.Append(SessionLogEntryKind.Status, notice);
                        }

                        refreshLog = notices.Count > 0;

                        refreshFooter = !string.Equals(indexingBefore, state.AttachmentIndexingStatusText, StringComparison.Ordinal);
                    }

                    pending = state.SessionAttachments.Any(static row => row.IndexingStatus == SessionAttachmentIndexStatus.Pending);
                }

                if (refreshLog)
                {
                    await uiUpdates.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog), cancellationToken).ConfigureAwait(false);
                }

                if (refreshFooter)
                {
                    await uiUpdates.WriteAsync(new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshFooter), cancellationToken).ConfigureAwait(false);
                }

                // A transient GET failure must not strand an already pending indexing projection.
                if (pending)
                {
                    await Task.Delay(MinimumInterval, _timeProvider, cancellationToken).ConfigureAwait(false);

                    Signal(RefreshReason.PendingIndex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
            // The UI consumer may close during ordinary Command Center shutdown.
        }
    }

    private void DrainWakeups()
    {
        while (_signals.Reader.TryRead(out _))
        {
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (IsRelevantHint(args.FullPath, uncertainDirectory: args.ChangeType == WatcherChangeTypes.Deleted))
        {
            Signal(RefreshReason.FileSystem);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        if (IsRelevantHint(args.OldFullPath, uncertainDirectory: true)
            || IsRelevantHint(args.FullPath, uncertainDirectory: true))
        {
            Signal(RefreshReason.FileSystem);
        }
    }

    private bool IsRelevantHint(string fullPath, bool uncertainDirectory)
    {
        CommandCenterState? state = Volatile.Read(ref _state);

        if (state is null || !HasWorkspaceSources(state))
        {
            return false;
        }

        string path;

        string root;

        try
        {
            path = NormalizeObservedPath(fullPath);

            root = NormalizeObservedPath(state.WorkingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        // A root/parent directory notification cannot be safely dismissed from opaque provenance.
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || root.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (SessionAttachmentDto row in state.SessionAttachments)
        {
            if (row.SourceKind != AttachmentSourceKind.WorkspaceFile)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.SourceRelativePath))
            {
                return true;
            }

            string source;

            try
            {
                source = row.SourceRelativePath.Replace('\\', '/').Trim('/').Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException)
            {
                return true;
            }

            string[] parts = source.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0 || source.Contains(':') || Path.IsPathRooted(row.SourceRelativePath!)
                || parts.Any(static part => part is "." or ".."))
            {
                return true;
            }

            // Workspace identity is opaque. Compare complete suffix segments, never guess a root
            // by joining provenance to WorkingDirectory. Conservative casing may overmatch hints.
            string prefix = string.Empty;

            foreach (string part in parts)
            {
                prefix = prefix.Length == 0 ? part : prefix + "/" + part;

                if (path.EndsWith("/" + prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // A deleted/renamed node has no reliable file-versus-directory type in FSW's event DTO.
        // It might be an unknown registered root containing a tracked suffix; retain that hint.
        return uncertainDirectory;
    }

    private static string NormalizeObservedPath(string path) => Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/').Normalize(NormalizationForm.FormC);

    private static bool HasWorkspaceSources(CommandCenterState state) =>
        state.SessionAttachments.Any(static row => row.SourceKind == AttachmentSourceKind.WorkspaceFile);

    private void OnError(object sender, ErrorEventArgs args)
    {
        string reason = args.GetException() is InternalBufferOverflowException ? "overflow" : "watcher_error";

        logger.LogDebug("Attachment watcher reported {Reason}; revalidating backend state.", reason);

        Signal(RefreshReason.WatcherError);
    }

    private void Signal(RefreshReason reason)
    {
        if (Volatile.Read(ref _disposed) || _runToken.IsCancellationRequested)
        {
            return;
        }

        _ = Interlocked.Or(ref _pendingReasons, (int)reason);

        _ = _signals.Writer.TryWrite(0);
    }

    private static bool IsExpectedWatcherFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or Win32Exception or PlatformNotSupportedException;

    private static string WatcherFailureReason(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "access_denied",
        PlatformNotSupportedException => "unsupported",
        Win32Exception => "resource_unavailable",
        ArgumentException => "workspace_unavailable",
        _ => "io_failure",
    };

    private void DisposeWatcher(FileSystemWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        watcher.Created -= OnChanged;

        watcher.Changed -= OnChanged;

        watcher.Deleted -= OnChanged;

        watcher.Renamed -= OnRenamed;

        watcher.Error -= OnError;

        try
        {
            watcher.Dispose();
        }
        catch (Exception exception) when (IsExpectedWatcherFailure(exception))
        {
            logger.LogWarning("Attachment watcher disposal failed ({Reason}); refresh cancellation continues.", WatcherFailureReason(exception));
        }
    }

    private static string Badge(SessionAttachmentDto row) =>
        row.SourceKind == AttachmentSourceKind.SnapshotOnly ? "Snapshot"
            : row.SourceStatus == AttachmentSourceStatus.Refreshable && row.IsRefreshable ? "Live" : "Stale";

    private static string ShortHash(string? value)
    {
        string hash = value?.Trim() ?? string.Empty;

        return hash.Length <= 8 ? hash : hash[..8];
    }

    private Task StopAsync()
    {
        lock (_lifecycle)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            _disposed = true;

            _state = null;

            FileSystemWatcher? watcher = _watcher;

            CancellationTokenSource? lifetime = _lifetime;

            Task? pump = _pump;

            _watcher = null;

            _lifetime = null;

            _pump = null;

            _signals.Writer.TryComplete();

            return _stopTask = StopCoreAsync(watcher, lifetime, pump);
        }
    }

    private async Task StopCoreAsync(FileSystemWatcher? watcher, CancellationTokenSource? lifetime, Task? pump)
    {
        try
        {
            DisposeWatcher(watcher);
        }
        finally
        {
            if (lifetime is not null)
            {
                try
                {
                    await lifetime.CancelAsync().ConfigureAwait(false);

                    if (pump is not null)
                    {
                        await pump.ConfigureAwait(false);
                    }
                }
                finally
                {
                    lifetime.Dispose();
                }
            }
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    public void Dispose() => _ = StopAsync();
}
