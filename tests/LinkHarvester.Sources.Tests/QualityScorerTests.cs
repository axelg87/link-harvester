using LinkHarvester.Core;
using Xunit;

namespace LinkHarvester.Sources.Tests;

public class QualityScorerTests
{
    private readonly QualityScorer _sut = new();

    [Theory]
    [InlineData("WEB-DL 4K", "MULTI (TRUEFRENCH)", "4K")]
    [InlineData("BLU-RAY 1080p", "MULTI (TRUEFRENCH)", "1080p")]
    [InlineData("WEB-DL 1080p", "MULTI (TRUEFRENCH)", "1080p")]
    [InlineData("WEBRIP 720p", "TRUEFRENCH", "720p")]
    [InlineData("WEBRIP", "TRUEFRENCH", "WEBRIP")]
    [InlineData("HDLIGHT 1080p", "MULTI (TRUEFRENCH)", "1080p")]
    public void Resolves_resolution_label(string quality, string lang, string expected)
    {
        var rating = _sut.Score(quality, lang);
        Assert.Equal(expected, rating.Resolution);
    }

    [Fact]
    public void Score_4K_dominates_1080p_dominates_720p_dominates_WEBRIP()
    {
        var s4k = _sut.Score("WEB-DL 4K", "MULTI").Score;
        var s1080 = _sut.Score("BLU-RAY 1080p", "MULTI").Score;
        var s720 = _sut.Score("WEBRIP 720p", "TRUEFRENCH").Score;
        var sw = _sut.Score("WEBRIP", "TRUEFRENCH").Score;

        Assert.True(s4k > s1080, $"4K {s4k} should beat 1080p {s1080}");
        Assert.True(s1080 > s720, $"1080p {s1080} should beat 720p {s720}");
        Assert.True(s720 > sw, $"720p {s720} should beat WEBRIP {sw}");
    }

    [Fact]
    public void Bluray_1080_beats_webdl_1080()
    {
        var bluray = _sut.Score("BLU-RAY 1080p", "MULTI").Score;
        var webdl = _sut.Score("WEB-DL 1080p", "MULTI").Score;
        Assert.True(bluray > webdl);
    }
}

public class TitleNormalizerTests
{
    private readonly TitleNormalizer _sut = new();

    [Theory]
    [InlineData("Avatar : De feu et de cendres", "avatar de feu et de cendres")]
    [InlineData("Les Enquêtes de Murdoch", "les enquetes de murdoch")]
    [InlineData("LOL : Qui rit, sort !", "lol qui rit sort")]
    [InlineData("The Boys", "the boys")]
    public void Normalizes_correctly(string raw, string expected)
    {
        Assert.Equal(expected, _sut.Normalize(raw));
    }
}
