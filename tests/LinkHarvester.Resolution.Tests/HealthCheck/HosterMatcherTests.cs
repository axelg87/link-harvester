using LinkHarvester.Resolution.HealthCheck;
using LinkHarvester.Resolution.HealthCheck.Hosters;
using Xunit;

namespace LinkHarvester.Resolution.Tests.HealthCheck;

public class HosterMatcherTests
{
    // ---- 1Fichier ----------------------------------------------------------
    [Fact]
    public void OneFichier_clean_404_with_dead_phrase_is_dead()
    {
        var sut = new OneFichierMatcher();
        var body = "<html><title>1fichier.com: Cloud Storage</title>...The requested file does not exist...";
        Assert.Equal(LinkHealth.Dead, sut.Evaluate(404, body, null));
    }

    [Fact]
    public void OneFichier_transport_reset_is_alive_protective()
    {
        var sut = new OneFichierMatcher();
        Assert.Equal(LinkHealth.Alive, sut.Evaluate(null, null, new HttpRequestException("RST")));
    }

    [Fact]
    public void OneFichier_404_without_dead_phrase_is_unknown()
    {
        var sut = new OneFichierMatcher();
        Assert.Equal(LinkHealth.Unknown, sut.Evaluate(404, "<html>some other 404</html>", null));
    }

    [Fact]
    public void OneFichier_200_is_alive()
    {
        var sut = new OneFichierMatcher();
        Assert.Equal(LinkHealth.Alive, sut.Evaluate(200, "<html>file landing</html>", null));
    }

    [Fact]
    public void OneFichier_matches_url_or_hoster_name()
    {
        var sut = new OneFichierMatcher();
        Assert.True(sut.Matches("https://1fichier.com/?abc&af=1", null));
        Assert.True(sut.Matches("https://example.com/x", "1Fichier"));
        Assert.False(sut.Matches("https://uploady.io/x", "uploady"));
    }

    // ---- Uploady -----------------------------------------------------------
    [Fact]
    public void Uploady_404_is_dead()
    {
        var sut = new UploadyMatcher();
        Assert.Equal(LinkHealth.Dead, sut.Evaluate(404, "<title>Download | Uploady.io</title>", null));
    }

    [Fact]
    public void Uploady_200_with_filename_title_is_alive()
    {
        var sut = new UploadyMatcher();
        var body = "<title>Download Obsession.1976.MULTi.VF2.1080p.HDLight.AC3.2.0.H264-LiHDL-Wawacity.WIN.mkv | Uploady.io</title>";
        Assert.Equal(LinkHealth.Alive, sut.Evaluate(200, body, null));
    }

    [Fact]
    public void Uploady_200_without_filename_falls_back_alive_non_dead()
    {
        // Without a filename signal, 200 still means the host responded — treat alive (conservative non-dead).
        var sut = new UploadyMatcher();
        Assert.Equal(LinkHealth.Alive, sut.Evaluate(200, "<title>Something</title>", null));
    }

    // ---- Rapidgator -------------------------------------------------------
    [Fact]
    public void Rapidgator_title_with_filename_is_alive()
    {
        var sut = new RapidgatorMatcher();
        var body = "<title>Télécharger le fichier Obsession.1976.MULTi.1080p.HDLight.H264.mkv</title>";
        Assert.Equal(LinkHealth.Alive, sut.Evaluate(200, body, null));
    }

    [Fact]
    public void Rapidgator_302_is_dead()
    {
        var sut = new RapidgatorMatcher();
        Assert.Equal(LinkHealth.Dead, sut.Evaluate(302, null, null));
    }

    [Fact]
    public void Rapidgator_premium_landing_is_dead()
    {
        var sut = new RapidgatorMatcher();
        var body = "<title>Rapidgator: Buy premium account</title>";
        Assert.Equal(LinkHealth.Dead, sut.Evaluate(200, body, null));
    }

    // ---- DailyUploads -----------------------------------------------------
    [Fact]
    public void DailyUploads_bare_title_is_dead()
    {
        var sut = new DailyUploadsMatcher();
        var body = "<title>Téléchargement </title>";
        Assert.Equal(LinkHealth.Dead, sut.Evaluate(200, body, null));
    }

    [Fact]
    public void DailyUploads_filename_in_title_is_alive()
    {
        var sut = new DailyUploadsMatcher();
        var body = "<title>Téléchargement Obsession 1976 MULTi 1080p HDLight H264 mkv</title>";
        Assert.Equal(LinkHealth.Alive, sut.Evaluate(200, body, null));
    }

    // ---- NitroFlare -------------------------------------------------------
    [Fact]
    public void NitroFlare_404_is_dead()
    {
        var sut = new NitroFlareMatcher();
        Assert.Equal(LinkHealth.Dead, sut.Evaluate(404, "<title>NitroFlare - Upload Files</title>", null));
    }

    [Fact]
    public void NitroFlare_generic_landing_is_dead_even_at_200()
    {
        var sut = new NitroFlareMatcher();
        Assert.Equal(LinkHealth.Dead, sut.Evaluate(200, "<title>NitroFlare - Upload Files</title>", null));
    }

    [Fact]
    public void NitroFlare_title_with_filename_is_alive()
    {
        var sut = new NitroFlareMatcher();
        var body = "<title>Download Big.Movie.2024.1080p.WEB-DL.x264.mkv - NitroFlare</title>";
        Assert.Equal(LinkHealth.Alive, sut.Evaluate(200, body, null));
    }

    // ---- Unknown fallback -------------------------------------------------
    [Fact]
    public void Unknown_matcher_always_returns_unknown()
    {
        var sut = new UnknownHosterMatcher();
        Assert.True(sut.Matches("https://darkibox.com/x", "darkibox"));
        Assert.Equal(LinkHealth.Unknown, sut.Evaluate(503, "<html>blocked</html>", null));
        Assert.Equal(LinkHealth.Unknown, sut.Evaluate(403, "<title>Just a moment...</title>", null));
        Assert.Equal(LinkHealth.Unknown, sut.Evaluate(200, "<html></html>", null));
        Assert.Equal(LinkHealth.Unknown, sut.Evaluate(null, null, new HttpRequestException("nope")));
    }
}
