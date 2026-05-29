using LinkHarvester.Resolution;
using Xunit;

namespace LinkHarvester.Resolution.Tests;

/// <summary>
/// Pins the three extraction layers in <c>DlProtectResolver.ExtractFinalUrls</c>.
/// Live dl-protect.link shapes shift every few weeks; each shape we've
/// observed in production should end up with a test here so the next
/// rotation gets fixed by adding one fixture rather than chasing a
/// silent regression.
/// </summary>
public class DlProtectExtractFinalUrlsTests
{
    // Layer 1 — legacy: hoster URL on the anchor's href attribute.
    [Fact]
    public void Legacy_anchor_href_extracts_hoster_url()
    {
        const string body = """
            <html><body>
            <a class="dest-url" href="https://rapidgator.net/file/abc123/Movie.mkv.html">Download</a>
            </body></html>
            """;
        var urls = DlProtectResolver.ExtractFinalUrlsForTesting(body);
        Assert.Single(urls);
        Assert.Contains("rapidgator.net/file/abc123", urls[0]);
    }

    // Layer 1.5 — current dl-protect serves anchors with the real URL on
    // a `data-href` (or `data-link`/`data-url`) attribute and `href="#"`
    // or a relative path on the anchor itself. Diagnostic in PR #35
    // showed `hasDestUrlAnchor=True` plus all-anchor fallback returning
    // zero, which is exactly this case.
    [Theory]
    [InlineData("data-href")]
    [InlineData("data-link")]
    [InlineData("data-url")]
    public void Data_attribute_on_anchor_extracts_hoster_url(string attribute)
    {
        var body = $"""
            <html><body>
            <a class="dest-url" href="#" {attribute}="https://1fichier.com/?xyz789">Continuer</a>
            </body></html>
            """;
        var urls = DlProtectResolver.ExtractFinalUrlsForTesting(body);
        Assert.Single(urls);
        Assert.Contains("1fichier.com", urls[0]);
    }

    // Layer 2 — anchor with no special class but a known hoster href.
    [Fact]
    public void Fallback_picks_up_plain_anchor_when_dest_url_class_absent()
    {
        const string body = """
            <html><body>
            <a href="https://uploady.io/abc/Some.Film.mkv">go</a>
            </body></html>
            """;
        var urls = DlProtectResolver.ExtractFinalUrlsForTesting(body);
        Assert.Single(urls);
        Assert.Contains("uploady.io", urls[0]);
    }

    // Layer 3 — URL embedded in inline JS or any non-anchor text node.
    // Same dl-protect mutation as layer 1.5 above but with the click
    // handler doing the redirect via JS rather than an anchor attribute.
    [Fact]
    public void Body_text_regex_extracts_hoster_url_from_inline_javascript()
    {
        const string body = """
            <html><body>
            <a class="dest-url" href="#" onclick="window.location='https://rapidgator.net/file/zzz999/Movie.mkv.html';return false">Continuer</a>
            </body></html>
            """;
        var urls = DlProtectResolver.ExtractFinalUrlsForTesting(body);
        Assert.Single(urls);
        Assert.Contains("rapidgator.net", urls[0]);
    }

    // Defensive — must NOT pick up dl-protect's own URL even if it
    // happens to be in the body.
    [Fact]
    public void Does_not_return_dl_protect_self_url()
    {
        const string body = """
            <html><body>
            <a class="dest-url" href="https://dl-protect.link/22801b6b?fn=abc">Continuer</a>
            </body></html>
            """;
        var urls = DlProtectResolver.ExtractFinalUrlsForTesting(body);
        Assert.Empty(urls);
    }

    // Defensive — unknown hoster domains must NOT be returned. We'd
    // rather log the failure and add the host to FinalHosterDomainHints
    // than ship a garbage URL to DSM.
    [Fact]
    public void Unknown_hoster_domain_is_not_returned()
    {
        const string body = """
            <html><body>
            <a class="dest-url" href="https://random-unknown-host.example/file/abc">go</a>
            </body></html>
            """;
        var urls = DlProtectResolver.ExtractFinalUrlsForTesting(body);
        Assert.Empty(urls);
    }

    // Several layers of mutation simultaneously — anchor has href="#",
    // data-link with the real URL, and a hoster URL also appears in an
    // inline script. We should de-dup and return one entry.
    [Fact]
    public void Multiple_layers_with_same_url_dedupe_to_one_entry()
    {
        const string body = """
            <html><body>
            <a class="dest-url" href="#" data-link="https://1fichier.com/?abc">Continuer</a>
            <script>var u='https://1fichier.com/?abc';</script>
            </body></html>
            """;
        var urls = DlProtectResolver.ExtractFinalUrlsForTesting(body);
        Assert.Single(urls);
    }

    // Defensive — must extract the new hosters we added to the hint list.
    [Theory]
    [InlineData("https://nitroflare.net/view/abc")]
    [InlineData("https://fikper.com/abc/movie.mkv.html")]
    [InlineData("https://rg.to/file/abc")]
    [InlineData("https://katfile.com/abc")]
    [InlineData("https://ddownload.com/abc")]
    public void Newly_added_hoster_domains_are_recognised(string url)
    {
        var body = $"""<html><body><a class="dest-url" href="{url}">go</a></body></html>""";
        var urls = DlProtectResolver.ExtractFinalUrlsForTesting(body);
        Assert.Single(urls);
        Assert.Equal(url, urls[0]);
    }
}
