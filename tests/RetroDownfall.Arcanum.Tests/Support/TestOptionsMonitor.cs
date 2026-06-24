using Microsoft.Extensions.Options;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class TestOptionsMonitor<T>(T current) : IOptionsMonitor<T>
{

    public T CurrentValue => current;

    public T Get(string? name) => current;

    public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable
    {

        public void Dispose()
        {
        }

    }

}
