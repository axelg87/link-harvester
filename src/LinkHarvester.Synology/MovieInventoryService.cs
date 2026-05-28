using System.Text.RegularExpressions;
using LinkHarvester.Core;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Synology;

/// <summary>
/// Answers "is this movie already on disk?" for catalog tiles. Movies live
/// flat in the movie root folder, so the per-folder approach we use for
/// series doesn't work — we need a one-shot index keyed by (normalized
/// title, year).
///
/// Two strategies:
///  - <b>Plex</b> — preferred. If <see cref="AppSettingsSnapshot.PlexBaseUrl"/>
///    and <c>PlexToken</c> are set we ask Plex directly for its movie list.
///    Plex has already parsed file names into Title + Year, so we trust it.
///  - <b>DSM filename heuristic</b> — fallback. We list the movie root and
///    parse "<i>Title</i>.<i>YYYY</i>"-shaped names. Less reliable for
///    titles with diacritics or unusual punctuation, but works without an
///    extra service.
///
/// The owned set is cached for 5 minutes (the catalog UI only polls on
/// click; a 5-minute staleness window is fine and keeps the load on Plex
/// and DSM minimal).
/// </summary>
public interface IMovieInventoryService
{
    Task<MovieOwnership> CheckAsync(string normalizedTitle, int? year, CancellationToken ct);
    /// <summary>Drop the cached owned set so the next call refetches.</summary>
    void Invalidate();
}

public sealed record MovieOwnership(bool Exists, string? MatchedName, string Source);

public sealed class MovieInventoryService : IMovieInventoryService
{
    private readonly IPlexClient _plex;
    private readonly IDownloadStationClient _dsm;
    private readonly ISettingsService _settings;
    private readonly ITitleNormalizer _normalizer;
    private readonly ILogger<MovieInventoryService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Cached owned-set. Keyed by (normalizedTitle, year) and (normalizedTitle, null)
    // — the null bucket lets us answer "I have *some* file with that title
    // even if the year differs from the catalog by ±1 or is missing."
    private CachedSet? _cache;

    public MovieInventoryService(
        IPlexClient plex,
        IDownloadStationClient dsm,
        ISettingsService settings,
        ITitleNormalizer normalizer,
        ILogger<MovieInventoryService> log)
    {
        _plex = plex;
        _dsm = dsm;
        _settings = settings;
        _normalizer = normalizer;
        _log = log;
        // Stale the cache whenever settings change (Plex URL/token toggle,
        // movie destination change…).
        _settings.Changed += Invalidate;
    }

    public async Task<MovieOwnership> CheckAsync(string normalizedTitle, int? year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(normalizedTitle))
            return new MovieOwnership(false, null, "none");

        var set = await GetCachedAsync(ct);
        if (set.Items.Count == 0)
            return new MovieOwnership(false, null, set.Source);

        // Exact match on (title, year); then leniency window of ±1 year;
        // then year-agnostic fallback.
        if (year is int y)
        {
            if (set.Items.TryGetValue((normalizedTitle, y), out var hit)) return Found(hit, set.Source);
            if (set.Items.TryGetValue((normalizedTitle, y - 1), out var hit1)) return Found(hit1, set.Source);
            if (set.Items.TryGetValue((normalizedTitle, y + 1), out var hit2)) return Found(hit2, set.Source);
        }
        if (set.Items.TryGetValue((normalizedTitle, (int?)null), out var anyYear))
            return Found(anyYear, set.Source);

        return new MovieOwnership(false, null, set.Source);
    }

    public void Invalidate()
    {
        _cache = null;
    }

    private static MovieOwnership Found(string matched, string source) => new(true, matched, source);

    private async Task<CachedSet> GetCachedAsync(CancellationToken ct)
    {
        var snap = _cache;
        if (snap is not null && snap.ExpiresAt > DateTimeOffset.UtcNow)
            return snap;

        await _gate.WaitAsync(ct);
        try
        {
            snap = _cache;
            if (snap is not null && snap.ExpiresAt > DateTimeOffset.UtcNow)
                return snap;

            snap = await BuildAsync(ct);
            _cache = snap;
            return snap;
        }
        finally { _gate.Release(); }
    }

    private async Task<CachedSet> BuildAsync(CancellationToken ct)
    {
        // Plex first if it's configured. If it yields zero results (Plex
        // empty or unreachable) we still try the filename fallback so the
        // user isn't left with no signal.
        if (_plex.IsConfigured)
        {
            var movies = await _plex.ListMoviesAsync(ct);
            if (movies.Count > 0)
            {
                var idx = BuildIndex(movies.Select(m => (Title: m.Title, Year: m.Year)));
                _log.LogInformation("MovieInventory: built {Count}-entry owned set from Plex.", idx.Count);
                return new CachedSet(idx, "plex", DateTimeOffset.UtcNow.AddMinutes(5));
            }
            _log.LogInformation("MovieInventory: Plex returned no movies; falling back to DSM filename matching.");
        }

        // DSM fallback: list the configured movie root and parse filenames.
        var s = _settings.Current;
        var root = s.SynologyMovieDestination;
        if (string.IsNullOrWhiteSpace(root))
            return new CachedSet(new Dictionary<(string, int?), string>(), "none", DateTimeOffset.UtcNow.AddMinutes(5));

        try
        {
            var listed = await _dsm.ListFolderAsync(root, ct);
            if (!listed.Exists)
            {
                _log.LogInformation("MovieInventory: movie root /{Root} not found on NAS.", root);
                return new CachedSet(new Dictionary<(string, int?), string>(), "dsm-missing", DateTimeOffset.UtcNow.AddMinutes(5));
            }
            var parsed = listed.Files
                .Where(f => !f.IsDir && !f.Name.StartsWith('.'))
                .Select(f => (Title: ExtractTitle(f.Name), Year: ExtractYear(f.Name), Raw: f.Name))
                .Where(p => p.Title is not null);
            var idx = BuildIndex(parsed.Select(p => (Title: p.Title!, Year: p.Year, Raw: p.Raw)));
            _log.LogInformation("MovieInventory: built {Count}-entry owned set from DSM /{Root}.", idx.Count, root);
            return new CachedSet(idx, "dsm", DateTimeOffset.UtcNow.AddMinutes(5));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MovieInventory: DSM listing failed; movies will appear as 'not on disk' until next refresh.");
            return new CachedSet(new Dictionary<(string, int?), string>(), "dsm-error", DateTimeOffset.UtcNow.AddMinutes(1));
        }
    }

    private Dictionary<(string, int?), string> BuildIndex(IEnumerable<(string Title, int? Year)> items)
        => BuildIndex(items.Select(i => (i.Title, i.Year, Raw: i.Title)));

    private Dictionary<(string, int?), string> BuildIndex(IEnumerable<(string Title, int? Year, string Raw)> items)
    {
        var dict = new Dictionary<(string, int?), string>();
        foreach (var (title, year, raw) in items)
        {
            var norm = _normalizer.Normalize(title);
            if (string.IsNullOrEmpty(norm)) continue;
            dict[(norm, year)] = raw;
            // Also index year-agnostic so titles whose catalog year is
            // missing or differs by more than the ±1 window still resolve.
            dict[(norm, (int?)null)] = raw;
        }
        return dict;
    }

    // "The.Dark.Knight.2008.1080p.mkv" → "The Dark Knight"
    // "Pacific Rim Uprising (2018).mkv" → "Pacific Rim Uprising"
    // "Inception 2010 FRENCH.mkv"      → "Inception"
    private static readonly Regex YearRx = new(@"[\.\s_\(\[\-]+(?<y>(?:19|20)\d{2})(?:\D|$)",
        RegexOptions.Compiled);

    public static string? ExtractTitle(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;
        var stem = Path.GetFileNameWithoutExtension(filename);
        var m = YearRx.Match(stem);
        var titlePart = m.Success ? stem.Substring(0, m.Index) : stem;
        titlePart = titlePart.Replace('.', ' ').Replace('_', ' ').Replace('-', ' ').Trim();
        return string.IsNullOrWhiteSpace(titlePart) ? null : titlePart;
    }

    public static int? ExtractYear(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;
        var stem = Path.GetFileNameWithoutExtension(filename);
        var m = YearRx.Match(stem);
        return m.Success && int.TryParse(m.Groups["y"].Value, out var y) ? y : null;
    }

    private sealed record CachedSet(
        IReadOnlyDictionary<(string Title, int? Year), string> Items,
        string Source,
        DateTimeOffset ExpiresAt);
}
