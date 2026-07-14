namespace RetroDownfall.TheForge.Ux.Markdown;

/// <summary>
/// Holds markdown content briefly between Workspace Explorer "Open Preview" and Workbench factory
/// create. Bounded to the last <see cref="Capacity"/> entries; callers may also <see cref="Remove"/>
/// on document close.
/// </summary>
public interface IMarkdownDocumentContentStore
{

    void Put(string id, string title, string content);

    void Put(MarkdownDocumentPayload payload);

    bool TryGet(string id, out MarkdownDocumentPayload payload);

    void Remove(string id);

}

public sealed class MarkdownDocumentContentStore : IMarkdownDocumentContentStore
{

    public const int Capacity = 16;

    private readonly object _gate = new();

    private readonly Dictionary<string, MarkdownDocumentPayload> _byId = new(StringComparer.Ordinal);

    private readonly LinkedList<string> _order = new();

    public void Put(string id, string title, string content) =>
        Put(new MarkdownDocumentPayload(id, title, content ?? string.Empty));

    public void Put(MarkdownDocumentPayload payload)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(payload.Id);

        lock (_gate)
        {

            if (_byId.ContainsKey(payload.Id))
            {

                _order.Remove(payload.Id);

            }

            _byId[payload.Id] = payload with { Content = payload.Content ?? string.Empty };

            _order.AddLast(payload.Id);

            while (_order.Count > Capacity)
            {

                string? oldest = _order.First?.Value;

                if (oldest is null)
                {

                    break;

                }

                _order.RemoveFirst();

                _byId.Remove(oldest);

            }

        }

    }

    public bool TryGet(string id, out MarkdownDocumentPayload payload)
    {

        lock (_gate)
        {

            if (_byId.TryGetValue(id, out MarkdownDocumentPayload? found))
            {

                payload = found;

                return true;

            }

        }

        payload = null!;

        return false;

    }

    public void Remove(string id)
    {

        lock (_gate)
        {

            if (_byId.Remove(id))
            {

                _order.Remove(id);

            }

        }

    }

}
