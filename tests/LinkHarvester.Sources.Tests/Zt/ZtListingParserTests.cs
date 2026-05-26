using LinkHarvester.Sources.Zt;
using Xunit;

namespace LinkHarvester.Sources.Tests.Zt;

public class ZtListingParserTests
{
    private readonly ZtListingParser _sut = new();

    [Fact]
    public void Parses_films_page_1_into_25_items()
    {
        var html = Fixtures.Load("Fixtures/zt/films1.html");

        var items = _sut.Parse(html);

        Assert.True(items.Count >= 20 && items.Count <= 30,
            $"expected ~25 items, got {items.Count}");
        Assert.All(items, i => Assert.StartsWith("film:", i.ExternalId));
    }

    [Fact]
    public void Populates_publish_date_from_listing_time_tag()
    {
        var html = Fixtures.Load("Fixtures/zt/films1.html");

        var items = _sut.Parse(html);

        Assert.All(items, i => Assert.NotNull(i.PublishedAt));
        // Page 1 should hold recent items — newest within ~30 days of capture (May 2026).
        var newest = items.Max(i => i.PublishedAt!.Value);
        Assert.True(newest.Year >= 2026, $"expected recent publish dates, newest={newest:u}");
    }

    [Fact]
    public void Old_page_has_older_dates()
    {
        var html = Fixtures.Load("Fixtures/zt/films100.html");

        var items = _sut.Parse(html);

        Assert.NotEmpty(items);
        var newest = items.Max(i => i.PublishedAt!.Value);
        // Page 100 was Feb 2026; assert it's older than May 2026.
        Assert.True(newest < new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            $"expected page 100 items < 2026-05, got newest={newest:u}");
    }

    [Fact]
    public void Series_page_parses_serie_items()
    {
        var html = Fixtures.Load("Fixtures/zt/series1.html");

        var items = _sut.Parse(html);

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.StartsWith("serie:", i.ExternalId));
    }

    [Fact]
    public void Manga_page_parses_manga_items()
    {
        var html = Fixtures.Load("Fixtures/zt/mangas1.html");

        var items = _sut.Parse(html);

        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.StartsWith("manga:", i.ExternalId));
    }

    [Fact]
    public void Extracts_quality_and_language_hints()
    {
        var html = Fixtures.Load("Fixtures/zt/films1.html");

        var items = _sut.Parse(html);

        // At least some films should have quality + language hints (BLU-RAY 1080p, MULTI, etc.).
        Assert.Contains(items, i => !string.IsNullOrEmpty(i.QualityHint));
        Assert.Contains(items, i => !string.IsNullOrEmpty(i.LanguageHint));
    }

    [Fact]
    public void Deduplicates_repeated_articles_within_page()
    {
        var html = Fixtures.Load("Fixtures/zt/films1.html");

        var items = _sut.Parse(html);

        var ids = items.Select(i => i.ExternalId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Returns_empty_for_blank_html()
    {
        Assert.Empty(_sut.Parse(""));
    }
}
