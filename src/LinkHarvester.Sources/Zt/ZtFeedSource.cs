using System.Runtime.CompilerServices;
using LinkHarvester.Core;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Sources.Zt;

/// <summary>
/// IFeedSource backed by ZT homepage scraping (RSS is broken on the site).
/// Also implements <see cref="IBackfillFeedSource"/> via paginated category pages.
/// </summary>
public sealed class ZtFeedSource : IBackfillFeedSource
{
    public string Id => ZtHomepageParser.SourceId;
    public string DisplayName => "Zone-Téléchargement";

    /// <summary>Polite pause between paginated requests.</summary>
    public static readonly TimeSpan DefaultPageDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Hard cap on backfill pages per kind, in case the date cutoff is never reached.</summary>
    public const int MaxBackfillPages = 600;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ZtHomepageParser _homepage;
    private readonly ZtListingParser _listing;
    private readonly ZtArticleParser _article;
    private readonly ITitleNormalizer _normalizer;
    private readonly ILogger<ZtFeedSource> _log;
    private readonly TimeSpan _pageDelay;

    public ZtFeedSource(IHttpClientFactory httpFactory,
                        ZtHomepageParser homepage,
                        ZtListingParser listing,
                        ZtArticleParser article,
                        ITitleNormalizer normalizer,
                        ILogger<ZtFeedSource> log)
        : this(httpFactory, homepage, listing, article, normalizer, log, DefaultPageDelay)
    {
    }

    internal ZtFeedSource(IHttpClientFactory httpFactory,
                          ZtHomepageParser homepage,
                          ZtListingParser listing,
                          ZtArticleParser article,
                          ITitleNormalizer normalizer,
                          ILogger<ZtFeedSource> log,
                          TimeSpan pageDelay)
    {
        _httpFactory = httpFactory;
        _homepage = homepage;
        _listing = listing;
        _article = article;
        _normalizer = normalizer;
        _log = log;
        _pageDelay = pageDelay;
    }

    public async Task<IReadOnlyList<RawListItem>> ListNewAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(NamedClients.Zt);
        using var resp = await http.GetAsync(ZtUrls.BaseUrl + "/", ct);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct);

        var items = _homepage.Parse(html);
        _log.LogInformation("ZT homepage: {Count} candidate items", items.Count);
        return items;
    }

    public async Task<ArticleDetails> FetchArticleAsync(string articleUrl, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(NamedClients.Zt);
        using var resp = await http.GetAsync(articleUrl, ct);
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(ct);
        return _article.Parse(articleUrl, html);
    }

    public TitleKey BuildTitleKey(ArticleDetails article)
    {
        var normalized = _normalizer.Normalize(article.Title);
        return new TitleKey(normalized, article.Year, article.Kind, article.SeasonNumber);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BackfillListingPage> ListSinceAsync(
        string kind,
        DateTimeOffset since,
        int startPage,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("kind required", nameof(kind));

        var http = _httpFactory.CreateClient(NamedClients.Zt);
        var page = Math.Max(1, startPage);
        var maxPage = page + MaxBackfillPages;

        while (page < maxPage)
        {
            ct.ThrowIfCancellationRequested();

            var url = ZtUrls.ListingUrl(kind, page);
            string html;
            try
            {
                using var resp = await http.GetAsync(url, ct);
                resp.EnsureSuccessStatusCode();
                html = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex)
            {
                _log.LogWarning(ex, "ZT listing fetch failed: {Url}", url);
                yield break;
            }

            var items = _listing.Parse(html);
            if (items.Count == 0)
            {
                _log.LogInformation("ZT listing empty at {Url}; stopping backfill", url);
                yield break;
            }

            var dated = items.Where(i => i.PublishedAt.HasValue).ToList();
            var oldest = dated.Count > 0 ? dated.Min(i => i.PublishedAt!.Value) : (DateTimeOffset?)null;
            var newest = dated.Count > 0 ? dated.Max(i => i.PublishedAt!.Value) : (DateTimeOffset?)null;

            yield return new BackfillListingPage(page, kind, items, oldest, newest);

            // Stop once oldest item on the page is before the cutoff.
            if (oldest is { } o && o < since)
            {
                _log.LogInformation("ZT backfill reached cutoff: page {Page} oldest {Oldest:u} < {Since:u}", page, o, since);
                yield break;
            }

            page++;
            if (_pageDelay > TimeSpan.Zero)
                await Task.Delay(_pageDelay, ct);
        }

        _log.LogWarning("ZT backfill hit page cap {Cap} for kind {Kind} starting at {Start}", MaxBackfillPages, kind, startPage);
    }
}
