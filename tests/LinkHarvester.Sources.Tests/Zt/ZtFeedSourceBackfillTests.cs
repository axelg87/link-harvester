using LinkHarvester.Core;
using LinkHarvester.Sources;
using LinkHarvester.Sources.Zt;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkHarvester.Sources.Tests.Zt;

public class ZtFeedSourceBackfillTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = new();
        public Func<string, HttpResponseMessage> Responder { get; set; } = _ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            return Task.FromResult(Responder(request.RequestUri!.ToString()));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _h;
        public StubFactory(HttpMessageHandler h) { _h = h; }
        public HttpClient CreateClient(string name) => new(_h, disposeHandler: false);
    }

    private static ZtFeedSource BuildSource(StubHandler handler)
    {
        var titles = new ZtTitleParser();
        return new ZtFeedSource(
            new StubFactory(handler),
            new ZtHomepageParser(),
            new ZtListingParser(),
            new ZtArticleParser(titles),
            new LinkHarvester.Core.TitleNormalizer(),
            NullLogger<ZtFeedSource>.Instance,
            pageDelay: TimeSpan.Zero);
    }

    [Fact]
    public async Task ListSinceAsync_yields_pages_in_order_and_stops_at_cutoff()
    {
        var films1 = await File.ReadAllTextAsync("Fixtures/zt/films1.html");
        var films100 = await File.ReadAllTextAsync("Fixtures/zt/films100.html");

        var handler = new StubHandler
        {
            Responder = url =>
            {
                // Page 1 → recent, Page 2 → older (use page100 fixture).
                var body = url.Contains("page=1") && !url.Contains("page=10") ? films1 : films100;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
            },
        };
        var src = BuildSource(handler);

        // Cutoff = April 2026 so page 1 (May 2026) doesn't stop us, page 2 (Feb 2026) does.
        var since = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var pages = new List<BackfillListingPage>();
        await foreach (var p in src.ListSinceAsync("films", since, 1, CancellationToken.None))
            pages.Add(p);

        Assert.Equal(2, pages.Count);
        Assert.Equal(1, pages[0].Page);
        Assert.Equal(2, pages[1].Page);
        Assert.True(pages[0].OldestPublishedAt >= since);
        Assert.True(pages[1].OldestPublishedAt < since);
    }

    [Fact]
    public async Task ListSinceAsync_stops_when_page_has_no_items()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html>empty</html>") },
        };
        var src = BuildSource(handler);

        var pages = new List<BackfillListingPage>();
        await foreach (var p in src.ListSinceAsync("films", DateTimeOffset.MinValue, 1, CancellationToken.None))
            pages.Add(p);

        Assert.Empty(pages);
        Assert.Single(handler.RequestedUrls);
    }

    [Fact]
    public async Task ListSinceAsync_stops_on_http_failure()
    {
        var handler = new StubHandler
        {
            Responder = _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError),
        };
        var src = BuildSource(handler);

        var pages = new List<BackfillListingPage>();
        await foreach (var p in src.ListSinceAsync("films", DateTimeOffset.MinValue, 1, CancellationToken.None))
            pages.Add(p);

        Assert.Empty(pages);
    }

    [Fact]
    public async Task ListingUrl_maps_kind_to_zt_slug()
    {
        Assert.EndsWith("?p=films&page=1", ZtUrls.ListingUrl("films", 1));
        Assert.EndsWith("?p=series&page=42", ZtUrls.ListingUrl("series", 42));
        Assert.EndsWith("?p=mangas&page=3", ZtUrls.ListingUrl("manga", 3));
    }
}
