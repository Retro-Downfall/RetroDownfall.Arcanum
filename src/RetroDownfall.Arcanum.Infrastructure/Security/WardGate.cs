using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

public sealed class WardGate : IWard
{

    private const string TimeoutReason = "The ward held until timeout — action was not allowed";

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

        var entry = new WardEntry(
            new TaskCompletionSource<WardResolution>(TaskCreationOptions.RunContinuationsAsynchronously),
            entryCts,
            toolName,
            arguments,
            sessionId,
            placedAt,
            expiresAt);

        if (!_pending.TryAdd(wardId, entry))
        {
            throw new InvalidOperationException($"A ward with id '{wardId}' is already active.");
        }

        await using CancellationTokenRegistration callerRegistration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(wardId, out WardEntry? removed))
            {
                removed.Cts.Cancel();

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
            entryCts.Dispose();
        }
    }

    public ResolveStatus Resolve(string wardId, bool allow, string? reason)
    {
        PruneResolvedTombstones();

        if (_pending.TryRemove(wardId, out WardEntry? entry))
        {
            entry.Cts.Cancel();

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

        DateTimeOffset resolvedAt = DateTimeOffset.UtcNow;

        var resolution = new WardResolution(false, TimeoutReason, resolvedAt);

        if (removed.Tcs.TrySetResult(resolution))
        {
            _resolved[wardId] = resolution;
        }
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
        string ToolName,
        JsonDocument? Arguments,
        string? SessionId,
        DateTimeOffset PlacedAt,
        DateTimeOffset ExpiresAt);

}
