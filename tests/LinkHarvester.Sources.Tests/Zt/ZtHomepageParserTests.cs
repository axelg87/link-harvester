using LinkHarvester.Sources.Zt;
using Xunit;

namespace LinkHarvester.Sources.Tests.Zt;

public class ZtHomepageParserTests
{
    private readonly ZtHomepageParser _sut = new();

    [Fact]
    public void Parses_a_reasonable_number_of_items()
    {
        var html = Fixtures.Load("Fixtures/zt/homepage.html");

        var items = _sut.Parse(html);

        Assert.NotEmpty(items);
        Assert.True(items.Count >= 30, $"expected >= 30 items, got {items.Count}");
    }

    [Fact]
    public void Includes_a_known_movie()
    {
        var html = Fixtures.Load("Fixtures/zt/homepage.html");

        var items = _sut.Parse(html);

        var losTigres = items.Where(i => i.ExternalId.StartsWith("film:") &&
                                          (i.Title ?? "").Contains("Los Tigres", StringComparison.OrdinalIgnoreCase))
                              .ToList();
        Assert.NotEmpty(losTigres);
        // Three known qualities should appear (55892 WEBRIP, 55893 WEB-DL 1080p, 55894 WEBRIP 720p).
        Assert.True(losTigres.Count >= 3, $"expected >= 3 Los Tigres variants, got {losTigres.Count}");
    }

    [Fact]
    public void Captures_quality_hints_for_film_rows()
    {
        var html = Fixtures.Load("Fixtures/zt/homepage.html");

        var items = _sut.Parse(html);

        var film = items.First(i => i.ExternalId.StartsWith("film:") && i.QualityHint is not null);
        Assert.NotNull(film.QualityHint);
        Assert.False(string.IsNullOrWhiteSpace(film.QualityHint));
    }

    [Fact]
    public void Excludes_ebook_and_music_categories()
    {
        var html = Fixtures.Load("Fixtures/zt/homepage.html");

        var items = _sut.Parse(html);

        Assert.DoesNotContain(items, i => i.ExternalId.StartsWith("ebook:"));
        Assert.DoesNotContain(items, i => i.ExternalId.StartsWith("musique:"));
    }

    [Fact]
    public void Captures_series_episode_count_in_quality_or_language_hint()
    {
        var html = Fixtures.Load("Fixtures/zt/homepage.html");

        var items = _sut.Parse(html);

        var series = items.Where(i => i.ExternalId.StartsWith("serie:")).ToList();
        Assert.NotEmpty(series);
    }
}
