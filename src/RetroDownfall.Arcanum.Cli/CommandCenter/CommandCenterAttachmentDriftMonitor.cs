using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.TheForge;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>

/// Uses local filesystem notifications only as an invalidation hint. Attachment state remains

/// server-owned: every signal fetches revalidated DTOs before Command Center changes a badge.

/// </summary>

internal sealed class CommandCenterAttachmentDriftMonitor(

    ArcanumApiClient apiClient,

    ILogger<CommandCenterAttachmentDriftMonitor> logger) : IAsyncDisposable, IDisposable

{

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);

    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(

        new BoundedChannelOptions(1)

        {

            FullMode = BoundedChannelFullMode.DropWrite,

            SingleReader = true,

            SingleWriter = false,

        });

    private FileSystemWatcher? _watcher;

    private CancellationTokenSource? _lifetime;

    private Task? _pump;

    public void Start(

        CommandCenterState state,

        ChannelWriter<CommandCenterUiUpdate> uiUpdates,

        CancellationToken cancellationToken)

    {

        ArgumentNullException.ThrowIfNull(state);

        ArgumentNullException.ThrowIfNull(uiUpdates);

        if (_lifetime is not null || !Directory.Exists(state.WorkingDirectory))

        {

            return;

        }

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _watcher = new FileSystemWatcher(state.WorkingDirectory)

        {

            IncludeSubdirectories = true,

            InternalBufferSize = 16 * 1024,

            NotifyFilter = NotifyFilters.FileName

                | NotifyFilters.DirectoryName

                | NotifyFilters.LastWrite

                | NotifyFilters.Size

                | NotifyFilters.CreationTime,

        };

        _watcher.Created += OnChanged;

        _watcher.Changed += OnChanged;

        _watcher.Deleted += OnChanged;

        _watcher.Renamed += OnRenamed;

        _watcher.Error += OnError;

        _watcher.EnableRaisingEvents = true;

        _pump = Task.Run(

            () => PumpAsync(state, uiUpdates, _lifetime.Token),

            _lifetime.Token);

        Signal();

    }

    public void RequestRefresh() => Signal();

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

    private async Task PumpAsync(

        CommandCenterState state,

        ChannelWriter<CommandCenterUiUpdate> uiUpdates,

        CancellationToken cancellationToken)

    {

        try

        {

            while (await _signals.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))

            {

                while (_signals.Reader.TryRead(out _))

                {

                }

                await Task.Delay(Debounce, cancellationToken).ConfigureAwait(false);

                while (_signals.Reader.TryRead(out _))

                {

                }

                if (state.SessionId is not { } sessionId)

                {

                    continue;

                }

                Result<SessionAttachmentDto[]> result = await apiClient

                    .GetSessionAttachmentsAsync(sessionId, cancellationToken)

                    .ConfigureAwait(false);

                if (result.IsFailure || state.SessionId != sessionId)

                {

                    continue;

                }

                string? indexingBefore = state.AttachmentIndexingStatusText;

                IReadOnlyList<string> notices = ApplyBackendSnapshot(

                    state,

                    result.Value ?? []);

                string? indexingAfter = state.AttachmentIndexingStatusText;

                foreach (string notice in notices)

                {

                    state.Log.Append(SessionLogEntryKind.Status, notice);

                }

                if (notices.Count > 0)

                {

                    await uiUpdates.WriteAsync(

                            new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshLog),

                            cancellationToken)

                        .ConfigureAwait(false);

                }

                if (!string.Equals(indexingBefore, indexingAfter, StringComparison.Ordinal))
                {

                    await uiUpdates.WriteAsync(

                            new CommandCenterUiUpdate(CommandCenterUiUpdateKind.RefreshFooter),

                            cancellationToken)

                        .ConfigureAwait(false);

                }

                if (state.SessionAttachments.Any(
                        static attachment => attachment.IndexingStatus == SessionAttachmentIndexStatus.Pending))
                {

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

                    Signal();

                }

            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)

        {

        }
        catch (Exception ex)

        {

            logger.LogDebug(ex, "Command Center attachment drift monitor stopped unexpectedly.");

        }

    }

    private void OnChanged(object sender, FileSystemEventArgs args) => Signal();

    private void OnRenamed(object sender, RenamedEventArgs args) => Signal();

    private void OnError(object sender, ErrorEventArgs args)

    {

        logger.LogDebug(

            args.GetException(),

            "Command Center attachment watcher overflowed or failed; revalidating backend state.");

        Signal();

    }

    private void Signal()

    {

        _ = _signals.Writer.TryWrite(0);

    }

    private static string Badge(SessionAttachmentDto row) =>

        row.SourceKind == AttachmentSourceKind.SnapshotOnly

            ? "Snapshot"

            : row.SourceStatus == AttachmentSourceStatus.Refreshable && row.IsRefreshable

                ? "Live"

                : "Stale";

    private static string ShortHash(string? value)

    {

        string hash = value?.Trim() ?? string.Empty;

        return hash.Length <= 8 ? hash : hash[..8];

    }

    public async ValueTask DisposeAsync()

    {

        _watcher?.Dispose();

        _watcher = null;

        if (_lifetime is null)

        {

            return;

        }

        await _lifetime.CancelAsync().ConfigureAwait(false);

        _signals.Writer.TryComplete();

        if (_pump is not null)

        {

            try

            {

                await _pump.ConfigureAwait(false);

            }
            catch (OperationCanceledException)

            {

            }

        }

        _lifetime.Dispose();

        _lifetime = null;

        _pump = null;

    }

    public void Dispose()

    {

        _watcher?.Dispose();

        _watcher = null;

        if (_lifetime is null)

        {

            return;

        }

        _lifetime.Cancel();

        _signals.Writer.TryComplete();

        _lifetime.Dispose();

        _lifetime = null;

        _pump = null;

    }

}
