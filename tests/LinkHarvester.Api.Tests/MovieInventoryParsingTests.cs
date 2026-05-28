using LinkHarvester.Synology;
using Xunit;

namespace LinkHarvester.Api.Tests;

/// <summary>
/// Title/year extraction from movie filenames. The fallback path when Plex
/// isn't configured walks /Films and tries to recognise scene-style
/// filenames — we just want to be sure the regex catches the formats users
/// actually have.
/// </summary>
public class MovieInventoryParsingTests
{
    [Theory]
    [InlineData("The.Dark.Knight.2008.1080p.BluRay.x264-FW.mkv", "The Dark Knight", 2008)]
    [InlineData("Pacific Rim Uprising (2018).mkv", "Pacific Rim Uprising", 2018)]
    [InlineData("Inception 2010 FRENCH 1080p WEB.mkv", "Inception", 2010)]
    [InlineData("Le.Reveil.de.la.Momie.2026.mkv", "Le Reveil de la Momie", 2026)]
    [InlineData("The_Punisher-One_Last_Kill-2026.mkv", "The Punisher One Last Kill", 2026)]
    [InlineData("Dune Part Two [2024].mkv", "Dune Part Two", 2024)]
    public void Extracts_title_and_year(string filename, string expectedTitle, int expectedYear)
    {
        var title = MovieInventoryService.ExtractTitle(filename);
        var year = MovieInventoryService.ExtractYear(filename);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    [Theory]
    [InlineData("RandomFile.mkv", "RandomFile", null)]
    [InlineData("notavideo.txt", "notavideo", null)]
    public void Title_only_when_no_year(string filename, string expectedTitle, int? expectedYear)
    {
        Assert.Equal(expectedTitle, MovieInventoryService.ExtractTitle(filename));
        Assert.Equal(expectedYear, MovieInventoryService.ExtractYear(filename));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_returns_null(string filename)
    {
        Assert.Null(MovieInventoryService.ExtractTitle(filename));
        Assert.Null(MovieInventoryService.ExtractYear(filename));
    }
}
