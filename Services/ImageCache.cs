using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace FloatingImageViewer.Services;

/// <summary>
/// 解码位图缓存（LRU）：按数量或按大小（MB）限制，超限时淘汰最久未使用的项。
/// 缓存的是已归一化/降采样后的显示位图，缩放时直接复用，避免重复解码。
/// </summary>
public static class ImageCache
{
    public readonly record struct CacheItem(BitmapSource Bitmap, int OriginalWidth, int OriginalHeight);

    private sealed class Entry
    {
        public required BitmapSource Bitmap { get; init; }

        public int OriginalWidth { get; init; }

        public int OriginalHeight { get; init; }

        public long Bytes { get; init; }

        public long LastAccess { get; set; }
    }

    private static readonly Dictionary<string, Entry> Entries = new();
    private static readonly object Sync = new();
    private static long _tick;
    private static string _strategy = "Count";
    private static int _limit = 20;

    public static void Configure(string strategy, int limit)
    {
        lock (Sync)
        {
            _strategy = strategy == "Size" ? "Size" : "Count";
            _limit = Math.Max(1, limit);
            Evict();
        }
    }

    public static CacheItem? Get(string key)
    {
        lock (Sync)
        {
            if (Entries.TryGetValue(key, out var entry))
            {
                entry.LastAccess = ++_tick;
                return new CacheItem(entry.Bitmap, entry.OriginalWidth, entry.OriginalHeight);
            }

            return null;
        }
    }

    public static void Add(string key, BitmapSource bitmap, int originalWidth, int originalHeight)
    {
        if (string.IsNullOrEmpty(key) || bitmap is null || originalWidth <= 0 || originalHeight <= 0)
        {
            return;
        }

        lock (Sync)
        {
            if (Entries.ContainsKey(key))
            {
                return;
            }

            Entries[key] = new Entry
            {
                Bitmap = bitmap,
                OriginalWidth = originalWidth,
                OriginalHeight = originalHeight,
                Bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4,
                LastAccess = ++_tick,
            };
            Evict();
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Entries.Clear();
        }
    }

    public static int Count
    {
        get
        {
            lock (Sync)
            {
                return Entries.Count;
            }
        }
    }

    private static void Evict()
    {
        if (_strategy == "Size")
        {
            long budgetBytes = (long)_limit * 1024 * 1024;
            long total = 0;
            foreach (var entry in Entries.Values)
            {
                total += entry.Bytes;
            }

            while (total > budgetBytes && Entries.Count > 0)
            {
                var oldest = Entries.OrderBy(e => e.Value.LastAccess).First();
                total -= oldest.Value.Bytes;
                Entries.Remove(oldest.Key);
            }
        }
        else
        {
            while (Entries.Count > _limit)
            {
                var oldest = Entries.OrderBy(e => e.Value.LastAccess).First();
                Entries.Remove(oldest.Key);
            }
        }
    }
}
