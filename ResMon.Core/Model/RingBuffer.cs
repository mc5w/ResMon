namespace ResMon.Core.Model;

/// <summary>
/// Fixed-size FIFO-Puffer. Beim Überlauf wird der älteste Eintrag überschrieben.
/// Trägt die 5-Minuten-Historie der Aggregatwerte (DESIGN.md §10).
/// </summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private readonly Lock _gate = new();
    private int _start;
    private int _count;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    public void Add(T item)
    {
        lock (_gate)
        {
            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = item;
                _count++;
            }
            else
            {
                _items[_start] = item;
                _start = (_start + 1) % _items.Length;
            }
        }
    }

    /// <summary>Kopiert den Inhalt in chronologischer Reihenfolge (ältester zuerst).</summary>
    public T[] ToArray()
    {
        lock (_gate)
        {
            var result = new T[_count];
            for (int i = 0; i < _count; i++)
                result[i] = _items[(_start + i) % _items.Length];
            return result;
        }
    }

    /// <summary>Kopiert höchstens die letzten <paramref name="count"/> Einträge.</summary>
    public T[] TakeLast(int count)
    {
        lock (_gate)
        {
            int take = Math.Min(count, _count);
            var result = new T[take];
            int offset = _count - take;
            for (int i = 0; i < take; i++)
                result[i] = _items[(_start + offset + i) % _items.Length];
            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _start = 0;
            _count = 0;
        }
    }
}
