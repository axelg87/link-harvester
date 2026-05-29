using System.Text.Json;
using Microsoft.JSInterop;

namespace LinkHarvester.Web.Services;

/// <summary>
/// Tiny localStorage-backed JSON cache. Used by Inbox / Catalog to render
/// the last-known payload instantly on navigation, then revalidate in the
/// background. Reduces perceived latency on every back-button / tab-switch.
///
/// Values are stamped with their cache time; reads expose <see cref="CachedEntry{T}.SavedAt"/>
/// so the caller can decide whether to skip the network fetch entirely.
/// Storage is bounded by the browser's per-origin quota (~5–10 MB); the
/// app's largest payload is /api/inbox at ~1 MB, well under.
/// </summary>
public sealed class BrowserCache
{
    private readonly IJSRuntime _js;
    public BrowserCache(IJSRuntime js) { _js = js; }

    public async Task<CachedEntry<T>?> TryGetAsync<T>(string key)
    {
        try
        {
            var raw = await _js.InvokeAsync<string?>("localStorage.getItem", key);
            if (string.IsNullOrEmpty(raw)) return null;
            return JsonSerializer.Deserialize<CachedEntry<T>>(raw);
        }
        catch { return null; }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        try
        {
            var entry = new CachedEntry<T>(value, DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(entry);
            await _js.InvokeVoidAsync("localStorage.setItem", key, json);
        }
        catch { /* quota / disabled localStorage — non-fatal */ }
    }
}

public sealed record CachedEntry<T>(T Value, DateTimeOffset SavedAt);
