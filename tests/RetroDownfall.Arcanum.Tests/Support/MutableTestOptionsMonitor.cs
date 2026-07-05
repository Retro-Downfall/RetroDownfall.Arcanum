using Microsoft.Extensions.Options;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Simulates <c>IOptionsMonitor&lt;T&gt;.CurrentValue</c> changing across calls (hot-reload), unlike
/// the fixed-snapshot <see cref="TestOptionsMonitor{T}"/>.
/// </summary>
internal sealed class MutableTestOptionsMonitor<T>(T current) : IOptionsMonitor<T>
{

    public T CurrentValue { get; set; } = current;

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

    private sealed class NoopDisposable : IDisposable
    {

        public void Dispose()
        {
        }

    }

}
