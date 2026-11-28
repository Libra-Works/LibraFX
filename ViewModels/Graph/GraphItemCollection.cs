using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LiberaFx.ViewModels.Graph;

public sealed class GraphItemCollection<T> : ObservableCollection<T>
{
    internal void ReplaceAll(IEnumerable<T> values)
    {
        var replacement = values.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (var value in replacement) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
