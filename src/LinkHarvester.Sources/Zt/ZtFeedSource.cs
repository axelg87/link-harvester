using LinkHarvester.Core;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Sources.Zt;

/// <summary>
/// IFeedSource backed by ZT homepage scraping (RSS is broken on the site).
/// </summary>
public sealed class ZtFeedSource : IFeedSource
{
    public string Id => ZtHomepageParser.SourceId;
    public string DisplayName => "Zone-Téléchargement";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ZtHomepageParser _homepage;
    private readonly ZtArticleParser _article;
    private readonly ITitleNormalizer _normalizer;
    private readonly ILogger<ZtFeedSource> _log;

    public ZtFeedSource(IHttpClientFactory httpFactory,
                        ZtHomepageParser homepage,
                        ZtArticleParser article,
                        ITitleNormalizer normalizer,
                        ILogger<ZtFeedSource> log)
    {
        _httpFactory = httpFactory;
        _homepage = homepage;
        _article = article;
        _normalizer = normalizer;
        _log = log;
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
}
