namespace Acrux;

/// <summary>
/// The memory the column caches share: the file's and a script result's
/// count against one limit, half of what they could use (the memory free now
/// plus what they hold). Counting what they hold keeps the limit from
/// shrinking as they grow. It is read again before a load and by
/// <see cref="Trim"/>, so the caches give memory back when other programs
/// take it, and there is no fixed ceiling.
/// One thread, like the caches: call it from the UI thread.
/// </summary>
public sealed class MemoryBudget
{
    private readonly Func<long, long> _formula;
    private readonly List<ColumnCache> _caches = [];

    /// <param name="formula">Maps the bytes held to the limit (default: <see cref="HalfOfFree"/>).</param>
    public MemoryBudget(Func<long, long>? formula = null)
    {
        _formula = formula ?? HalfOfFree;
        Limit = _formula(0);
    }

    /// <summary>Half of the memory free now plus what the caches hold.</summary>
    public static long HalfOfFree(long held) => (SystemMemory.Available() + held) / 2;

    /// <summary>The limit as last computed (before a load or on <see cref="Trim"/>).</summary>
    public long Limit { get; private set; }

    /// <summary>Bytes of the Arrow buffers every cache holds.</summary>
    public long HeldBytes => _caches.Sum(c => c.HeldBytes);

    internal long InflightBytes => _caches.Sum(c => c.InflightBytes);

    internal void Add(ColumnCache cache) => _caches.Add(cache);

    internal void Remove(ColumnCache cache) => _caches.Remove(cache);

    /// <summary>
    /// Reads the free memory again and drops the columns past the new limit,
    /// never the pinned ones. Call it now and then.
    /// </summary>
    public void Trim()
    {
        if (_caches.Any(c => c.LoadingCount > 0))   // a load in flight lowers the free memory for a moment
            return;
        Refresh();
        MakeRoom(null, 0);
    }

    internal void Refresh()
    {
        if (_caches.All(c => c.LoadingCount == 0))   // otherwise the loads' working memory reads as taken
            Limit = _formula(HeldBytes);
    }

    // Drops least recently used columns until what is held plus what is
    // coming fits. A cache with nothing pinned (off screen: the file under a
    // script result) gives first, the one asking gives last.
    internal void MakeRoom(ColumnCache? requester, long incoming)
    {
        long excess = HeldBytes + incoming - Limit;
        foreach (var cache in _caches.OrderBy(c => c == requester ? 2 : c.HasPins ? 1 : 0).ToList())
        {
            if (excess <= 0) return;
            excess -= cache.Release(excess);
        }
    }
}
