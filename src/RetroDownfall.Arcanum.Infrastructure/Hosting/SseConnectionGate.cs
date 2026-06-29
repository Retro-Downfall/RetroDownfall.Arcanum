using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

public sealed class SseConnectionGate
{

    private readonly AdmissionGate _gate = new();

    private readonly IOptionsMonitor<ArcanumSettings> _settings;

    public SseConnectionGate(IOptionsMonitor<ArcanumSettings> settings)
    {

        _settings = settings;

    }

    public bool TryAcquire(out SseConnectionLease? lease)
    {

        int maxConnections = ArcanumSettingClamps.MaxSseConnections(
            _settings.CurrentValue.EventBus?.MaxSseConnections ?? new EventBusSettings().MaxSseConnections);

        if (!_gate.TryEnter(maxConnections, out IDisposable? innerLease))
        {

            lease = null;

            return false;

        }

        lease = new SseConnectionLease(innerLease!);

        return true;

    }

}

public sealed class SseConnectionLease : IDisposable
{

    private readonly IDisposable _innerLease;

    private int _disposed;

    internal SseConnectionLease(IDisposable innerLease)
    {

        _innerLease = innerLease;

    }

    public void Dispose()
    {

        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {

            _innerLease.Dispose();

        }

    }

}
