using LinkHarvester.Api.Caching;
using Microsoft.Extensions.Caching.Memory;

namespace LinkHarvester.Api.Tests;

public class CatalogAggregatesCacheTests
{
    private static CatalogAggregatesCache NewCache() =>
        new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public async Task First_call_runs_factory_subsequent_calls_serve_from_cache()
    {
        var cache = NewCache();
        var calls = 0;
        Task<List<int>> Factory(CancellationToken _) { calls++; return Task.FromResult(new List<int> { 1, 2, 3 }); }

        var a = await cache.GetOrAddAsync("k", Factory);
        var b = await cache.GetOrAddAsync("k", Factory);
        var c = await cache.GetOrAddAsync("k", Factory);

        Assert.Equal(1, calls);
        Assert.Same(a, b);
        Assert.Same(b, c);
        Assert.Equal(new[] { 1, 2, 3 }, a);
    }

    [Fact]
    public async Task Different_keys_run_their_own_factory()
    {
        var cache = NewCache();
        var calls = 0;
        Task<string> Factory(CancellationToken _) { calls++; return Task.FromResult("v" + calls); }

        var a = await cache.GetOrAddAsync("k1", Factory);
        var b = await cache.GetOrAddAsync("k2", Factory);

        Assert.Equal(2, calls);
        Assert.Equal("v1", a);
        Assert.Equal("v2", b);
    }

    [Fact]
    public async Task Expired_entry_re_runs_factory()
    {
        var cache = NewCache();
        var calls = 0;
        Task<string> Factory(CancellationToken _) { calls++; return Task.FromResult("v" + calls); }

        var a = await cache.GetOrAddAsync("k", Factory, ttl: TimeSpan.FromMilliseconds(50));
        await Task.Delay(150);
        var b = await cache.GetOrAddAsync("k", Factory, ttl: TimeSpan.FromMilliseconds(50));

        Assert.Equal(2, calls);
        Assert.Equal("v1", a);
        Assert.Equal("v2", b);
    }

    [Fact]
    public async Task Concurrent_callers_share_a_single_in_flight_factory()
    {
        // This is the single-flight guarantee: if 100 catalog page loads
        // hit /facets at the same time on a cold cache, the underlying
        // 2.3M-row GROUP BY scan must run exactly once.
        var cache = NewCache();
        var calls = 0;
        var gate = new TaskCompletionSource<bool>();

        async Task<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return "result";
        }

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => cache.GetOrAddAsync("k", Factory))
            .ToArray();

        // Let the inflight factory complete.
        await Task.Delay(50);
        gate.SetResult(true);
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal("result", r));
    }

    [Fact]
    public async Task Invalidate_drops_entry_so_next_call_re_runs_factory()
    {
        var cache = NewCache();
        var calls = 0;
        Task<string> Factory(CancellationToken _) { calls++; return Task.FromResult("v" + calls); }

        await cache.GetOrAddAsync("k", Factory);
        cache.Invalidate("k");
        await cache.GetOrAddAsync("k", Factory);

        Assert.Equal(2, calls);
    }
}
