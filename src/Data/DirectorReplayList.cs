using System.Collections;

namespace VSDirector.Data;

/// <summary>Mutable capture buffer or read-only, indexed playback view.</summary>
public sealed class DirectorReplayList<T> : IList<T>, IReadOnlyList<T>
{
    private readonly List<T>? items;
    private readonly Func<int, T>? read;
    private readonly int count;
    private readonly Func<double, bool, int>? searchTime;
    public DirectorReplayList() { items = new(); }
    public DirectorReplayList(IEnumerable<T> source) { items = source.ToList(); }
    internal DirectorReplayList(int count, Func<int, T> read, Func<double, bool, int>? searchTime = null) { this.count = count; this.read = read; this.searchTime = searchTime; }
    internal bool TrySearchTime(double time, bool inclusive, out int index)
    { index = searchTime?.Invoke(time, inclusive) ?? -1; return searchTime is not null; }
    public static implicit operator DirectorReplayList<T>(List<T> source) => new(source);
    public bool IsPaged => read is not null;
    public int Count => items?.Count ?? count;
    public bool IsReadOnly => IsPaged;
    private List<T> Mutable => items ?? throw new InvalidOperationException("Materialize the replay before editing recorded data.");
    public T this[int index]
    {
        get { if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index)); return items is null ? read!(index) : items[index]; }
        set => Mutable[index] = value;
    }
    public void Add(T item) => Mutable.Add(item);
    public void AddRange(IEnumerable<T> values) => Mutable.AddRange(values);
    public void Clear() => Mutable.Clear();
    public void Insert(int index, T item) => Mutable.Insert(index, item);
    public bool Remove(T item) => Mutable.Remove(item);
    public void RemoveAt(int index) => Mutable.RemoveAt(index);
    public void RemoveRange(int index, int count) => Mutable.RemoveRange(index, count);
    public int RemoveAll(Predicate<T> match) => Mutable.RemoveAll(match);
    public void Sort(Comparison<T> comparison) => Mutable.Sort(comparison);
    public int FindIndex(Predicate<T> match) { for (int i = 0; i < Count; i++) if (match(this[i])) return i; return -1; }
    public int FindLastIndex(Predicate<T> match) { for (int i = Count - 1; i >= 0; i--) if (match(this[i])) return i; return -1; }
    public int IndexOf(T item) => FindIndex(value => EqualityComparer<T>.Default.Equals(item, value));
    public bool Contains(T item) => IndexOf(item) >= 0;
    public void CopyTo(T[] array, int arrayIndex) { foreach (T item in this) array[arrayIndex++] = item; }
    public IEnumerator<T> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
