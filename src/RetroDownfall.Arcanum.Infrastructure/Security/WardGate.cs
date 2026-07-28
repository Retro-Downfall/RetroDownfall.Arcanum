using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class WardGate : IWard
{

    private const string TimeoutReason = "The ward held until timeout — action was not allowed";

    private const string CapacityReason = "Maximum active wards reached — action was not allowed";

    /// <summary>
    /// Documented contract value for restart-driven denial. Wards are ephemeral by design: a ward's
    /// <see cref="System.Threading.Tasks.TaskCompletionSource{TResult}"/> is correlated to one in-flight
    /// inference turn in one process. <see cref="WardGate"/> is a fresh, empty singleton on every process
    /// start (there are no active wards to iterate and deny), so no code path sets this value today — it
    /// remains a stable contract string for clients. See <c>docs/Arcanum.DESIGN.md</c> §5.4.4 and
    /// §11.14.
    /// </summary>
    private const string HostRestartedReason = "Host restarted — ward timed out";

    // AdmissionGate atomically enforces the active-ward cap. The counter is incremented before
    // TryAdd and rolled back when the cap or a duplicate ward id rejects admission. Every terminal
    // removal from _pending disposes the lease exactly once because ConcurrentDictionary.TryRemove
    // succeeds for only the first remover.
    private readonly AdmissionGate _activeWards = new();

    private readonly ConcurrentDictionary<string, WardEntry> _pending = new();

    private readonly ConcurrentDictionary<string, WardResolution> _resolved = new();

    private readonly object _resolutionGate = new();

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    private readonly TimeProvider _timeProvider;

    public WardGate(IOptionsMonitor<ArcanumSettings> settings, TimeProvider? timeProvider = null)
    {
        _settings = settings;

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WardResolution> WardAsync(
        string wardId,
        string toolName,
        JsonDocument? arguments,
        string? sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset placedAt = _timeProvider.GetUtcNow();

        DateTimeOffset expiresAt = placedAt.Add(timeout);

        var entryCts = new CancellationTokenSource();

        int maxActiveWards = ArcanumSettingClamps.MaxActiveWards(
            ResolveRuntimeSettings().MaxActiveWards);

        if (!_activeWards.TryEnter(maxActiveWards, out IDisposable? wardLease))
        {

            return new WardResolution(false, CapacityReason, _timeProvider.GetUtcNow());

        }

        var entry = new WardEntry(
            new TaskCompletionSource<WardResolution>(TaskCreationOptions.RunContinuationsAsynchronously),
            entryCts,
            wardLease!,
            toolName,
            arguments,
            sessionId,
            placedAt,
            expiresAt);

        if (!_pending.TryAdd(wardId, entry))
        {

            wardLease!.Dispose();

            throw new InvalidOperationException($"A ward with id '{wardId}' is already active.");

        }

        await using CancellationTokenRegistration callerRegistration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(wardId, out WardEntry? removed))
            {
                removed.CapacityLease.Dispose();

                TryCancelEntry(removed.Cts);

                DisposeEntry(removed);

                removed.Tcs.TrySetCanceled(cancellationToken);
            }
        });

        _ = RunTimeoutAsync(wardId, entry, timeout, entryCts.Token);

        try
        {
            return await entry.Tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PruneResolvedTombstones();

            entryCts.Dispose();
        }
    }

    public ResolveStatus Resolve(string wardId, bool allow, string? reason)
    {
        PruneResolvedTombstones();

        WardEntry entry;

        WardResolution resolution;

        lock (_resolutionGate)
        {

            if (!_pending.TryRemove(wardId, out entry!))
            {

                return _resolved.ContainsKey(wardId)
                    ? ResolveStatus.AlreadyResolved
                    : ResolveStatus.NotFound;

            }

            resolution = new WardResolution(allow, reason, _timeProvider.GetUtcNow());

            _resolved[wardId] = resolution;

        }

        entry.CapacityLease.Dispose();

        TryCancelEntry(entry.Cts);

        DisposeEntry(entry);

        // Every completion path must win _pending.TryRemove before completing the TCS, so
        // the remover exclusively owns an incomplete entry.
        _ = entry.Tcs.TrySetResult(resolution);

        return ResolveStatus.Success;
    }

    public IReadOnlyList<ActiveWard> GetActiveWards()
    {
        PruneResolvedTombstones();

        return _pending
            .Select(static pair => new ActiveWard(
                pair.Key,
                pair.Value.ToolName,
                pair.Value.Arguments,
                pair.Value.SessionId,
                pair.Value.PlacedAt,
                pair.Value.ExpiresAt))
            .ToList();
    }


    private static void TryCancelEntry(CancellationTokenSource cts)
    {

        try
        {

            cts.Cancel();

        }
        catch (ObjectDisposedException)
        {

        }

    }

    // W3.4 Group B: dispose the pooled native memory behind WardEntry.Arguments when the
    // entry leaves _pending on any terminal path (resolve / timeout / caller-cancel). The
    // arguments may be null (ward placed without a payload), so guard the disposal.
    private static void DisposeEntry(WardEntry entry)
    {

        entry.Arguments?.Dispose();

    }

    private async Task RunTimeoutAsync(string wardId, WardEntry entry, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        WardEntry removed;

        WardResolution resolution;

        lock (_resolutionGate)
        {

            if (!_pending.TryRemove(wardId, out removed!))
            {
                return;
            }

            resolution = new WardResolution(false, TimeoutReason, _timeProvider.GetUtcNow());

            _resolved[wardId] = resolution;

        }

        removed.CapacityLease.Dispose();

        DisposeEntry(removed);

        _ = removed.Tcs.TrySetResult(resolution);

        PruneResolvedTombstones();
    }

    private WardSettings ResolveRuntimeSettings() =>
        _settings.CurrentValue.ResolveWard();

    private void PruneResolvedTombstones()
    {
        int timeoutSeconds = ArcanumSettingClamps.WardTimeoutSeconds(
            ResolveRuntimeSettings().TimeoutSeconds);

        DateTimeOffset cutoff = _timeProvider.GetUtcNow().AddSeconds(-(timeoutSeconds + 60));

        foreach (KeyValuePair<string, WardResolution> pair in _resolved)
        {
            if (pair.Value.ResolvedAt < cutoff)
            {
                _resolved.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record WardEntry(
        TaskCompletionSource<WardResolution> Tcs,
        CancellationTokenSource Cts,
        IDisposable CapacityLease,
        string ToolName,
        JsonDocument? Arguments,
        string? SessionId,
        DateTimeOffset PlacedAt,
        DateTimeOffset ExpiresAt);

}
