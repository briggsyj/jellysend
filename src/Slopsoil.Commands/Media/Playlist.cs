using System.Globalization;

namespace Slopsoil.Commands.Media;

/// <summary>
/// The per-guild "up next" list (<c>_GuildState.queue</c>): ordered, at most <see cref="MediaText.PlaylistMax"/>
/// items, items addressed by 1-based number or a unique case-insensitive label substring
/// (<c>_find_queued</c>). Thread-safe; replies match <c>cogs/jellyfin.py</c>.
/// </summary>
public sealed class Playlist<T>(Func<T, string> label)
    where T : class
{
    private readonly List<T> _items = [];
    private readonly object _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public bool IsFull => Count >= MediaText.PlaylistMax;

    public IReadOnlyList<T> Snapshot()
    {
        lock (_lock)
        {
            return [.. _items];
        }
    }

    /// <summary>Append; returns the new 1-based position, or null when full.</summary>
    public int? Add(T item)
    {
        lock (_lock)
        {
            if (_items.Count >= MediaText.PlaylistMax)
            {
                return null;
            }

            _items.Add(item);
            return _items.Count;
        }
    }

    /// <summary>
    /// Insert <paramref name="items"/> at the head in their order (they play next), as many as fit under the cap;
    /// returns how many were inserted (spec 014: episodes picked together with the one starting now).
    /// </summary>
    public int InsertNext(IReadOnlyList<T> items)
    {
        lock (_lock)
        {
            var fit = Math.Max(0, Math.Min(items.Count, MediaText.PlaylistMax - _items.Count));
            _items.InsertRange(0, items.Take(fit));
            return fit;
        }
    }

    /// <summary>Remove and return the first item, or null when empty (auto-advance / skip).</summary>
    public T? TakeNext()
    {
        lock (_lock)
        {
            if (_items.Count == 0)
            {
                return null;
            }

            var item = _items[0];
            _items.RemoveAt(0);
            return item;
        }
    }

    /// <summary>Put an item back at the head (its start failed); may exceed the cap by one, as Python's insert(0).</summary>
    public void PushFront(T item)
    {
        lock (_lock)
        {
            _items.Insert(0, item);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }

    /// <summary><c>!playlist order &lt;# or title&gt; &lt;pos&gt;</c>: the reply to send.</summary>
    public string Move(string args)
    {
        var trimmed = args.Trim();
        var split = trimmed.LastIndexOfAny([' ', '\t', '\n']);
        var target = split < 0 ? "" : trimmed[..split].Trim();
        var posText = split < 0 ? "" : trimmed[(split + 1)..];
        if (target.Length == 0 || !IsDecimal(posText))
        {
            return "🙅 Say which item and where to — e.g. `!playlist order 3 1` (item 3 to the top).";
        }

        lock (_lock)
        {
            if (_items.Count == 0)
            {
                return "📭 The playlist is empty.";
            }

            var (index, error) = Find(target);
            if (error is not null)
            {
                return error;
            }

            if (!int.TryParse(posText, NumberStyles.None, CultureInfo.InvariantCulture, out var position) || position < 1 || position > _items.Count)
            {
                return $"🙅 The new position must be between 1 and {_items.Count}.";
            }

            var item = _items[index];
            _items.RemoveAt(index);
            _items.Insert(position - 1, item);
            return $"↕️ Moved **{label(item)}** to #{position}.";
        }
    }

    /// <summary><c>!playlist remove &lt;# or title&gt;</c>: the reply to send.</summary>
    public string Remove(string target)
    {
        if (target.Trim().Length == 0)
        {
            return "🙅 Say which item — e.g. `!playlist remove 2`.";
        }

        lock (_lock)
        {
            if (_items.Count == 0)
            {
                return "📭 The playlist is empty.";
            }

            var (index, error) = Find(target);
            if (error is not null)
            {
                return error;
            }

            var item = _items[index];
            _items.RemoveAt(index);
            return $"➖ Took **{label(item)}** off the playlist.";
        }
    }

    /// <summary>0-based index of <paramref name="target"/> (number or title), or an error reply. Caller holds the lock.</summary>
    private (int Index, string? Error) Find(string target)
    {
        target = target.Trim();
        if (IsDecimal(target))
        {
            if (int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= _items.Count)
            {
                return (n - 1, null);
            }

            return (-1, $"🙅 There's no #{target.TrimStart('0').PadLeft(1, '0')} on the playlist (1–{_items.Count}).");
        }

        var matches = _items.Select((item, i) => (item, i))
            .Where(x => label(x.item).Contains(target, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.i)
            .ToList();
        return matches.Count switch
        {
            0 => (-1, $"🔎 Nothing on the playlist matches `{target}`."),
            1 => (matches[0], null),
            _ => (-1, $"🙅 `{target}` matches several items — use its number from `!playlist`."),
        };
    }

    private static bool IsDecimal(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
}
