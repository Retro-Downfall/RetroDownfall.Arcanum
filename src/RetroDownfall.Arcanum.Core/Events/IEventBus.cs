namespace RetroDownfall.Arcanum.Core.Events;

/// <summary>
/// Process-local publish/subscribe bus for live push updates (SSE consumers).
/// </summary>
public interface IEventBus
{

    void Publish<T>(T @event) where T : notnull;

    IAsyncEnumerable<T> Subscribe<T>(CancellationToken cancellationToken) where T : notnull;

}
