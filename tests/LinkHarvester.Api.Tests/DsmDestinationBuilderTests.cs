using LinkHarvester.Synology;
using Xunit;

namespace LinkHarvester.Api.Tests;

public class DsmDestinationBuilderTests
{
    [Fact]
    public void Movie_uses_flat_root()
    {
        var p = DsmDestinationBuilder.ForMovie("video/movies");
        Assert.Equal("video/movies", p.FullPath);
        Assert.Empty(p.ParentSegments);
    }

    [Fact]
    public void Series_with_season_produces_title_and_season_folder()
    {
        var p = DsmDestinationBuilder.ForSeries("video/series", "Vikings: Valhalla", 2);
        Assert.Equal("video/series/Vikings Valhalla/Season 2", p.FullPath);
        Assert.Equal(new[] { "video/series/Vikings Valhalla", "video/series/Vikings Valhalla/Season 2" }, p.ParentSegments);
    }

    [Fact]
    public void Series_without_season_stops_at_title_folder()
    {
        var p = DsmDestinationBuilder.ForSeries("video/series", "Halo", null);
        Assert.Equal("video/series/Halo", p.FullPath);
        Assert.Equal(new[] { "video/series/Halo" }, p.ParentSegments);
    }

    [Fact]
    public void Anime_uses_anime_root()
    {
        var p = DsmDestinationBuilder.ForAnime("video/anime", "Attack on Titan", 4);
        Assert.StartsWith("video/anime/Attack on Titan", p.FullPath);
        Assert.EndsWith("Season 4", p.FullPath);
    }

    [Theory]
    [InlineData("Pulp Fiction", "Pulp Fiction")]
    [InlineData("  Pulp Fiction  ", "Pulp Fiction")]
    [InlineData("Crocodile/Dundee", "Crocodile Dundee")]
    [InlineData("Bonjour:Tristesse?", "Bonjour Tristesse")]
    [InlineData("L'Été en pente douce", "L'Ete en pente douce")] // diacritics stripped
    [InlineData("...hidden", "hidden")]                                     // leading dots dropped
    [InlineData("", "Unknown")]                                              // empty → safe default
    [InlineData("   ", "Unknown")]                                           // whitespace → safe default
    public void SanitizeFolderName_handles_edge_cases(string input, string expected)
    {
        Assert.Equal(expected, DsmDestinationBuilder.SanitizeFolderName(input));
    }

    [Fact]
    public void SanitizeFolderName_caps_overly_long_titles()
    {
        var long_ = new string('a', 250);
        var sanitized = DsmDestinationBuilder.SanitizeFolderName(long_);
        Assert.True(sanitized.Length <= 120, $"length was {sanitized.Length}");
    }

    [Fact]
    public void Root_trailing_slash_is_normalised()
    {
        var p = DsmDestinationBuilder.ForSeries("video/series/", "Foo", 1);
        Assert.Equal("video/series/Foo/Season 1", p.FullPath);
    }

    [Fact]
    public void Season_zero_treated_as_no_season()
    {
        var p = DsmDestinationBuilder.ForSeries("video/series", "Foo", 0);
        Assert.Equal("video/series/Foo", p.FullPath);
    }
}
