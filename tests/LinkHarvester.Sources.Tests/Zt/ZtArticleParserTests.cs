using LinkHarvester.Core;
using LinkHarvester.Sources.Zt;
using Xunit;

namespace LinkHarvester.Sources.Tests.Zt;

public class ZtArticleParserTests
{
    private readonly ZtArticleParser _sut = new(new ZtTitleParser());

    [Fact]
    public void Movie_Los_Tigres_has_expected_fields()
    {
        var html = Fixtures.Load("Fixtures/zt/movie_los_tigres.html");

        var details = _sut.Parse("https://www.zone-telechargement.news/?p=film&id=55893-los-tigres", html);

        Assert.Equal("zt", details.SourceId);
        Assert.Equal("film:55893", details.ExternalId);
        Assert.Equal(TitleKind.Movie, details.Kind);
        Assert.Equal("Los Tigres", details.Title);
        Assert.Null(details.SeasonNumber);
        Assert.Equal("WEB-DL 1080p", details.QualityLabel);
        Assert.Contains("MULTI", details.Language ?? string.Empty);
        Assert.True(details.SizeBytes is > 6_000_000_000 and < 8_000_000_000,
            $"size {details.SizeBytes} not in expected range");
        Assert.Contains("dl-protect.link/rqts-url", details.AggregatorDlProtectUrl);
    }

    [Fact]
    public void Movie_Los_Tigres_has_three_hosters_each_with_one_link()
    {
        var html = Fixtures.Load("Fixtures/zt/movie_los_tigres.html");

        var details = _sut.Parse("https://www.zone-telechargement.news/?p=film&id=55893-los-tigres", html);

        Assert.Equal(3, details.Hosters.Count);
        var names = details.Hosters.Select(h => h.Hoster).ToList();
        Assert.Contains("Uploady", names);
        Assert.Contains("DailyUploads", names);
        Assert.Contains("Rapidgator", names);
        Assert.All(details.Hosters, h => Assert.Single(h.Links));
        Assert.All(details.Hosters, h => Assert.Null(h.Links[0].EpisodeIndex));
        Assert.All(details.Hosters, h => Assert.StartsWith("https://dl-protect.link/", h.Links[0].Url));
    }

    [Fact]
    public void Movie_Los_Tigres_lists_two_quality_siblings()
    {
        var html = Fixtures.Load("Fixtures/zt/movie_los_tigres.html");

        var details = _sut.Parse("https://www.zone-telechargement.news/?p=film&id=55893-los-tigres", html);

        Assert.Equal(2, details.SiblingArticleUrls.Count);
        Assert.Contains(details.SiblingArticleUrls, u => u.Contains("id=55892"));
        Assert.Contains(details.SiblingArticleUrls, u => u.Contains("id=55894"));
    }

    [Fact]
    public void Series_The_Boys_Saison5_parses_episode_count_and_per_episode_links()
    {
        var html = Fixtures.Load("Fixtures/zt/series_the_boys_s5.html");

        var details = _sut.Parse("https://www.zone-telechargement.news/?p=serie&id=26521-the-boys-saison5", html);

        Assert.Equal(TitleKind.Season, details.Kind);
        Assert.Equal("The Boys", details.Title);
        Assert.Equal(5, details.SeasonNumber);

        Assert.Equal(4, details.Hosters.Count);
        Assert.Contains(details.Hosters, h => h.Hoster == "Uploady");
        Assert.Contains(details.Hosters, h => h.Hoster == "DailyUploads");
        Assert.Contains(details.Hosters, h => h.Hoster == "Rapidgator");
        Assert.Contains(details.Hosters, h => h.Hoster == "Nitroflare");

        var rapidgator = details.Hosters.First(h => h.Hoster == "Rapidgator");
        Assert.Equal(5, rapidgator.Links.Count);
        Assert.All(rapidgator.Links, l => Assert.NotNull(l.EpisodeIndex));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 },
            rapidgator.Links.Select(l => l.EpisodeIndex!.Value).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Series_excludes_streaming_links()
    {
        var html = Fixtures.Load("Fixtures/zt/series_the_boys_s5.html");

        var details = _sut.Parse("https://www.zone-telechargement.news/?p=serie&id=26521-the-boys-saison5", html);

        Assert.DoesNotContain(details.Hosters, h => h.Hoster is "Netu" or "Vidoza");
    }

    [Fact]
    public void Content_hash_is_stable_for_identical_html()
    {
        var html = Fixtures.Load("Fixtures/zt/movie_los_tigres.html");

        var d1 = _sut.Parse("https://www.zone-telechargement.news/?p=film&id=55893-los-tigres", html);
        var d2 = _sut.Parse("https://www.zone-telechargement.news/?p=film&id=55893-los-tigres", html);

        Assert.Equal(d1.ContentHash, d2.ContentHash);
        Assert.Equal(64, d1.ContentHash.Length);
    }
}
