using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MasterSplinter.Entrypoint.Infrastructure
{
    /// <summary>
    /// An <see cref="ObservableCollection{T}"/> that can be refilled in one shot.
    /// <para>
    /// The usual <c>Clear()</c> + <c>Add()</c>-in-a-loop raises one CollectionChanged event per
    /// item — 2001 of them for a 2000-commit log, each one making the bound ListView do work, on
    /// the UI thread. <see cref="Reset"/> mutates the backing list directly and raises a single
    /// Reset event instead, which the ListView handles by rebuilding once.
    /// </para>
    /// </summary>
    public sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        /// <summary>Replace the entire contents, raising exactly one Reset notification.</summary>
        public void Reset(IEnumerable<T> items)
        {
            Items.Clear();
            foreach (T item in items)
                Items.Add(item);

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
