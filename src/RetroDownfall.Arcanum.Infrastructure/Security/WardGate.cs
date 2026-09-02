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

    /// <summary>
    /// Fail-closed answer when an automatic decision names a ward id that is already live. Ward ids
    /// are freshly minted GUIDs, so this is unreachable in practice; it exists so the automatic path
    /// can never strand or overwrite an interactive waiter.
    /// </summary>
    private const string ContendedAutomaticResolutionReason =
        "A ward with this id is already awaiting an operator — action was not allowed";

    // AdmissionGate atomically enforces the active-ward cap. The counter is incremented before
    // TryAdd and rolled back when the cap or a duplicate ward id rejects admission. Every terminal
    // removal from _pending disposes the lease exactly once because ConcurrentDictionary.TryRemove
    // succeeds for only the first remover.
    private readonly AdmissionGate _activeWards = new();

    private readonly ConcurrentDictionary<string, WardEntry> _pending = new();

    private readonly ConcurrentDictionary<string, WardResolution> _resolved = new();

    private readonly object _resolutionGate = new();

    private readonly WardSettings _runtimeSettings;

    private readonly TimeProvider _timeProvider;

    public WardGate(IOptionsMonitor<ArcanumSettings> settings, TimeProvider? timeProvider = null)
    {
        _runtimeSettings = settings.CurrentValue.ResolveWard();

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

        int maxActiveWards = ArcanumSettingClamps.MaxActiveWards(
            _runtimeSettings.MaxActiveWards);

        if (!_activeWards.TryEnter(maxActiveWards, out IDisposable? wardLease))
        {

            // Denied before any resource beyond the capacity probe itself was allocated for
            // this call: no CancellationTokenSource has been minted yet (it is allocated below,
            // only once admission succeeds), and the caller's JsonDocument is released here
            // rather than being silently dropped.
            arguments?.Dispose();

            return new WardResolution(
                false,
                CapacityReason,
                _timeProvider.GetUtcNow(),
                WardResolutionOrigin.AutoDenied);

        }

        var entryCts = new CancellationTokenSource();

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

            DisposeEntry(entry);

            entryCts.Dispose();

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

        _ = RunTimeoutAsync(wardId, timeout, entryCts.Token);

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

            resolution = new WardResolution(
                allow,
                reason,
                _timeProvider.GetUtcNow(),
                WardResolutionOrigin.Human);

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

    /// <summary>
    /// Records a host-made decision as an atomic create → resolved transition: the tombstone is
    /// written under the same <see cref="_resolutionGate"/> that <see cref="Resolve"/> and the timeout
    /// take, and no entry is ever added to <see cref="_pending"/>. There is therefore no window in
    /// which <see cref="GetActiveWards"/> lists the ward or a manual <c>POST</c> can resolve it — a
    /// competitor arriving afterwards sees <see cref="ResolveStatus.AlreadyResolved"/>.
    /// </summary>
    public WardResolution RecordAutomaticResolution(
        string wardId,
        bool allowed,
        string? reason,
        WardResolutionOrigin origin)
    {
        PruneResolvedTombstones();

        lock (_resolutionGate)
        {

            // A live ward already has a waiter that owns the outcome; overwriting its tombstone here
            // would strand that waiter. Fail closed instead of racing the interactive path.
            if (_pending.ContainsKey(wardId))
            {

                return new WardResolution(
                    false,
                    ContendedAutomaticResolutionReason,
                    _timeProvider.GetUtcNow(),
                    WardResolutionOrigin.AutoDenied);

            }

            if (_resolved.TryGetValue(wardId, out WardResolution? existing))
            {

                return existing;

            }

            WardResolution resolution = new(allowed, reason, _timeProvider.GetUtcNow(), origin);

            _resolved[wardId] = resolution;

            return resolution;

        }
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
    // entry leaves _pending on any terminal path (resolve / timeout / caller-cancel), and also
    // when a duplicate ward id rejects admission before the entry ever enters _pending (W7-6) —
    // that rejection is terminal for this entry too, just never pending. The arguments may be
    // null (ward placed without a payload), so guard the disposal.
    private static void DisposeEntry(WardEntry entry)
    {

        entry.Arguments?.Dispose();

    }

    private async Task RunTimeoutAsync(string wardId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _ = TryResolveTimedOutWard(wardId);
    }

    // Keep the post-delay transition separate so both outcomes remain explicit: the timeout can
    // remove the live ward, or it can observe that another resolver already won.
    internal bool TryResolveTimedOutWard(string wardId)
    {
        WardEntry removed;

        WardResolution resolution;

        lock (_resolutionGate)
        {

            if (!_pending.TryRemove(wardId, out removed!))
            {
                return false;
            }

            resolution = new WardResolution(
                false,
                TimeoutReason,
                _timeProvider.GetUtcNow(),
                WardResolutionOrigin.TimedOut);

            _resolved[wardId] = resolution;

        }

        removed.CapacityLease.Dispose();

        DisposeEntry(removed);

        _ = removed.Tcs.TrySetResult(resolution);

        PruneResolvedTombstones();

        return true;
    }

    private void PruneResolvedTombstones()
    {
        int timeoutSeconds = ArcanumSettingClamps.WardTimeoutSeconds(
            _runtimeSettings.TimeoutSeconds);

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
