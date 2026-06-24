using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

public sealed class SseConnectionGate
{

    private int _activeConnections;

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    public SseConnectionGate(IOptionsMonitor<ArcanumSettings> settings)
    {

        _settings = settings;

    }

    public bool TryAcquire(out SseConnectionLease? lease)
    {

        int maxConnections = ArcanumSettingClamps.MaxSseConnections(
            _settings.CurrentValue.EventBus?.MaxSseConnections ?? new EventBusSettings().MaxSseConnections);

        int active = Interlocked.Increment(ref _activeConnections);

        if (active > maxConnections)
        {

            Interlocked.Decrement(ref _activeConnections);

            lease = null;

            return false;

        }

        lease = new SseConnectionLease(this);

        return true;

    }

    internal void Release()
    {

        Interlocked.Decrement(ref _activeConnections);

    }

}

public sealed class SseConnectionLease : IDisposable
{

    private readonly SseConnectionGate _gate;

    private int _disposed;

    internal SseConnectionLease(SseConnectionGate gate)
    {

        _gate = gate;

    }

    public void Dispose()
    {

        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {

            _gate.Release();

        }

    }

}
