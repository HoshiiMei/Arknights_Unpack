using System.Windows.Media.Imaging;

namespace ArkBrowser.UI;

internal sealed class FullResCache
{
    private readonly int capacity;
    private readonly object gate = new();
    private readonly Dictionary<string, BitmapSource> cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> lru = new();

    public FullResCache(int capacity)
    {
        this.capacity = Math.Max(1, capacity);
    }

    public bool TryGet(string key, out BitmapSource? value)
    {
        lock (gate)
        {
            if (cache.TryGetValue(key, out value))
            {
                lru.Remove(key);
                lru.AddFirst(key);
                return true;
            }

            value = null;
            return false;
        }
    }

    public void Add(string key, BitmapSource value)
    {
        lock (gate)
        {
            if (cache.TryGetValue(key, out _))
            {
                lru.Remove(key);
            }
            else if (cache.Count >= capacity && lru.Last is not null)
            {
                cache.Remove(lru.Last.Value);
                lru.RemoveLast();
            }

            cache[key] = value;
            lru.AddFirst(key);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            cache.Clear();
            lru.Clear();
        }
    }
}
