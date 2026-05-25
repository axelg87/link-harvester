using Microsoft.Extensions.Caching.Memory;

namespace LinkHarvester.Api.Caching;

/// <summary>
/// In-memory TTL cache for catalog aggregate endpoints (<c>/facets</c>,
/// <c>/genres</c>). Both endpoints execute full-table <c>GROUP BY</c> scans
/// over millions of <c>CatalogLinks</c> rows or every <c>GenresJson</c>
/// blob, so the result is expensive but rarely stale enough to matter
/// between catalog page loads.
///
/// Trade-offs:
///   - Cache lifetime is short (5 minutes by default) so a fresh ingest
///     or enrichment batch is visible within a coffee break, no manual
///     invalidation needed.
///   - Single-flight semantics via <see cref="GetOrAddAsync"/>: concurrent
///     callers for the same key share the in-flight factory. Stops the
///     two-pane catalog page from triple-scanning the database the very
///     first time it loads.
///   - Singleton lifetime is intentional — the cache must survive across
///     requests; if we accidentally made it scoped, every request would
///     start with a cold cache and the optimisation disappears.
/// </summary>
public sealed class CatalogAggregatesCache
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly IMemoryCache _cache;
    private readonly Dictionary<string, SemaphoreSlim> _keyGates = new(StringComparer.Ordinal);
    private readonly object _gateLock = new();

    public CatalogAggregatesCache(IMemoryCache cache)
    {
        _cache = cache;
    }

    /// <summary>
    /// Returns the cached value for <paramref name="key"/> if present and
    /// fresh, otherwise calls <paramref name="factory"/> under a per-key
    /// lock and stores the result with the requested TTL.
    /// </summary>
    public async Task<T> GetOrAddAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan? ttl = null,
        CancellationToken ct = default) where T : class
    {
        if (_cache.TryGetValue(key, out T? cached) && cached is not null)
            return cached;

        // Per-key gate so two concurrent callers don't both run the
        // expensive factory.
        var gate = AcquireGate(key);
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out cached) && cached is not null)
                return cached;

            var fresh = await factory(ct);
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ttl ?? DefaultTtl,
                // Each entry is tiny (a few hundred bytes); set Size = 1 so
                // we cooperate with any SizeLimit the host configures later.
                Size = 1,
            };
            _cache.Set(key, fresh, options);
            return fresh;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Drops everything in the cache. Wire up to ingest-completion or
    /// large-enrichment-batch events if you want fresher aggregates than
    /// the TTL provides.
    /// </summary>
    public void Invalidate(string key) => _cache.Remove(key);

    private SemaphoreSlim AcquireGate(string key)
    {
        lock (_gateLock)
        {
            if (!_keyGates.TryGetValue(key, out var g))
            {
                g = new SemaphoreSlim(1, 1);
                _keyGates[key] = g;
            }
            return g;
        }
    }
}
