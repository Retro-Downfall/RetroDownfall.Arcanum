using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace RetroDownfall.TheForge.Ux.Models;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can be replaced wholesale in one notification.
/// Filling a bound Avalonia <c>ListBox</c> item-by-item raises one <c>CollectionChanged</c> per row,
/// and a large diff is tens of thousands of rows — the notification storm alone freezes the window
/// for longer than computing the diff did. <see cref="ResetTo"/> swaps the contents behind a single
/// <see cref="NotifyCollectionChangedAction.Reset"/>.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{

    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));

    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");

    private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);

    public void ResetTo(IEnumerable<T> items)
    {

        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();

        foreach (T item in items)
        {

            Items.Add(item);

        }

        OnPropertyChanged(CountChanged);

        OnPropertyChanged(IndexerChanged);

        OnCollectionChanged(ResetArgs);

    }

}
