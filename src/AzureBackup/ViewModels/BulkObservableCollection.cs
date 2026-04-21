using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AzureBackup.ViewModels;

/// <summary>
/// <see cref="ObservableCollection{T}"/> with a bulk-replace primitive that
/// raises a single <see cref="NotifyCollectionChangedAction.Reset"/> event for
/// the whole batch, instead of one event per item.
///
/// <para>
/// Use this when:
/// </para>
/// <list type="bullet">
///   <item>The bound view is a virtualised list/tree that re-evaluates its
///     viewport on every <see cref="NotifyCollectionChangedAction.Add"/>
///     (Avalonia's <c>ItemsControl</c> behaves this way).</item>
///   <item>You are doing a "Clear + foreach Add" in a hot path where the
///     N item-add events dominate cost (e.g. the Logs drain or
///     <c>RefreshFromAzureAsync</c> rebuilding 50K rows).</item>
/// </list>
///
/// <para>
/// Trade-off: callers lose per-item delta information. Bound controls that
/// animate Add/Remove will not animate the bulk change.
/// </para>
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replaces the collection contents with <paramref name="items"/> and
    /// raises a single Reset event. Use for batch rebuilds.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Appends every item in <paramref name="items"/> to the end of the
    /// collection and raises a single Reset event. Use when adding more
    /// than a handful of items at once to a non-empty collection.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        CheckReentrancy();
        var anyAdded = false;
        foreach (var item in items)
        {
            Items.Add(item);
            anyAdded = true;
        }
        if (!anyAdded) return;
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Removes the trailing <paramref name="count"/> items in one shot and
    /// raises a single Reset event. Used by the Logs cap-trim path so a
    /// large drain does not produce hundreds of Remove events.
    /// </summary>
    public void RemoveLast(int count)
    {
        if (count <= 0) return;
        CheckReentrancy();
        var actual = System.Math.Min(count, Items.Count);
        for (var i = 0; i < actual; i++)
        {
            Items.RemoveAt(Items.Count - 1);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
