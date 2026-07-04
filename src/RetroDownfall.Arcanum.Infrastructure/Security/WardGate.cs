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
    /// exists so future callers/clients have a stable string to compare against if that changes.
    /// See docs/DESIGN.md §11.14 and docs/persistence.md §7.
    /// </summary>
    private const string HostRestartedReason = "Host restarted — ward timed out";

    // W3.3 Fix 1: atomic active-ward counter via AdmissionGate. The counter is
    // incremented BEFORE the TryAdd; if it exceeds MaxActiveWards it is rolled back
    // and the acquire is denied. The TryAdd-failure path (duplicate ward id) also
    // rolls back. Every terminal removal from _pending disposes the ward lease
    // exactly once — ConcurrentDictionary.TryRemove returns true only for the first
    // remover, so a double-resolve/late-timeout cannot double-release. _pending.Count
    // is no longer used for cap enforcement (it was a non-atomic check-then-add).
    private readonly AdmissionGate _activeWards = new();

    private readonly ConcurrentDictionary<string, WardEntry> _pending = new();

    private readonly ConcurrentDictionary<string, WardResolution> _resolved = new();

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    public WardGate(IOptionsMonitor<ArcanumSettings> settings)
    {
        _settings = settings;
    }

    public async Task<WardResolution> WardAsync(
        string wardId,
        string toolName,
        JsonDocument? arguments,
        string? sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset placedAt = DateTimeOffset.UtcNow;

        DateTimeOffset expiresAt = placedAt.Add(timeout);

        var entryCts = new CancellationTokenSource();

        int maxActiveWards = ArcanumSettingClamps.MaxActiveWards(
            _settings.CurrentValue.Ward?.MaxActiveWards ?? new WardSettings().MaxActiveWards);

        if (!_activeWards.TryEnter(maxActiveWards, out IDisposable? wardLease))
        {

            return new WardResolution(false, CapacityReason, DateTimeOffset.UtcNow);

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

        if (_pending.TryRemove(wardId, out WardEntry? entry))
        {
            entry.CapacityLease.Dispose();

            TryCancelEntry(entry.Cts);

            DisposeEntry(entry);

            DateTimeOffset resolvedAt = DateTimeOffset.UtcNow;

            var resolution = new WardResolution(allow, reason, resolvedAt);

            if (entry.Tcs.TrySetResult(resolution))
            {
                _resolved[wardId] = resolution;

                return ResolveStatus.Success;
            }

            _resolved[wardId] = resolution;

            return ResolveStatus.AlreadyResolved;
        }

        if (_resolved.ContainsKey(wardId))
        {
            return ResolveStatus.AlreadyResolved;
        }

        return ResolveStatus.NotFound;
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

        if (!_pending.TryRemove(wardId, out WardEntry? removed))
        {
            return;
        }

        removed.CapacityLease.Dispose();

        DisposeEntry(removed);

        DateTimeOffset resolvedAt = DateTimeOffset.UtcNow;

        var resolution = new WardResolution(false, TimeoutReason, resolvedAt);

        if (removed.Tcs.TrySetResult(resolution))
        {
            _resolved[wardId] = resolution;
        }

        PruneResolvedTombstones();
    }

    private void PruneResolvedTombstones()
    {
        int timeoutSeconds = ArcanumSettingClamps.WardTimeoutSeconds(
            _settings.CurrentValue.Ward?.TimeoutSeconds ?? new WardSettings().TimeoutSeconds);

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddSeconds(-(timeoutSeconds + 60));

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
