using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Reason a call to <see cref="SseConnectionGate.TryAcquire"/> was denied.
/// </summary>
public enum SseDenialReason
{

    None,

    /// <summary>The global <c>Arcanum:Execution:MaxSseConnections</c> cap was reached.</summary>
    Global,

    /// <summary>The per-event-type <c>Arcanum:Execution:MaxSseConnectionsPerType</c> cap was reached.</summary>
    PerType,

}

/// <summary>
/// Describes why an SSE connection attempt was denied, including enough detail to build a
/// type-specific error message.
/// </summary>
public readonly record struct SseConnectionDenial(SseDenialReason Reason, string EventType, int Limit)
{

    public static readonly SseConnectionDenial None = new(SseDenialReason.None, string.Empty, 0);

}

/// <summary>
/// Enforces the global and per-event-type SSE connection caps. A single shared instance backs
/// every SSE route so the global <see cref="AdmissionGate"/> and the per-type
/// <see cref="SseConnectionCounter"/> are consistent across all stream families.
/// </summary>
public sealed class SseConnectionGate
{

    private readonly AdmissionGate _gate = new();

    private readonly SseConnectionCounter _counter;

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    public SseConnectionGate(SseConnectionCounter counter, IOptionsMonitor<ArcanumSettings> settings)
    {

        _counter = counter;

        _settings = settings;

    }

    /// <summary>
    /// Attempts to admit a new SSE connection for <paramref name="eventType"/>. Checks the global
    /// cap first (existing behavior), then the per-type cap. On success, disposing the returned
    /// lease releases both the per-type counter and the global slot.
    /// </summary>
    public bool TryAcquire(string eventType, out SseConnectionLease? lease, out SseConnectionDenial denial)
    {

        EventBusSettings eventBus = _settings.CurrentValue.ResolveEventBus();

        int maxConnections = ArcanumSettingClamps.MaxSseConnections(eventBus.MaxSseConnections);

        int perTypeLimit = ArcanumSettingClamps.SseConnectionsPerType(eventBus.MaxSseConnectionsPerType);

        if (!_gate.TryEnter(maxConnections, out IDisposable? innerLease))
        {

            lease = null;

            denial = new SseConnectionDenial(SseDenialReason.Global, eventType, maxConnections);

            return false;

        }

        int typeCount = _counter.Increment(eventType);

        if (typeCount > perTypeLimit)
        {

            _counter.Decrement(eventType);

            innerLease!.Dispose();

            lease = null;

            denial = new SseConnectionDenial(SseDenialReason.PerType, eventType, perTypeLimit);

            return false;

        }

        lease = new SseConnectionLease(innerLease!, _counter, eventType);

        denial = SseConnectionDenial.None;

        ArcanumMetrics.SseConnectionsCurrent.Add(1, new KeyValuePair<string, object?>("event_type", eventType));

        return true;

    }

}

public sealed class SseConnectionLease : IDisposable
{

    private readonly IDisposable _innerLease;

    private readonly SseConnectionCounter _counter;

    private readonly string _eventType;

    private int _disposed;

    internal SseConnectionLease(IDisposable innerLease, SseConnectionCounter counter, string eventType)
    {

        _innerLease = innerLease;

        _counter = counter;

        _eventType = eventType;

    }

    public void Dispose()
    {

        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {

            // Decrement the per-type counter before releasing the global slot so another thread
            // cannot acquire the freed global slot and observe an inflated per-type count from
            // this connection before it has finished releasing.
            _counter.Decrement(_eventType);

            ArcanumMetrics.SseConnectionsCurrent.Add(-1, new KeyValuePair<string, object?>("event_type", _eventType));

            _innerLease.Dispose();

        }

    }

}
