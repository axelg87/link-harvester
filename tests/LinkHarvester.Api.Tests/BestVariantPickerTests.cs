using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using Xunit;

namespace LinkHarvester.Api.Tests;

public class BestVariantPickerTests
{
    [Fact]
    public void Picks_highest_preferred_quality_tier()
    {
        var links = new[]
        {
            Make(id: 1, quality: "720p WEBRIP", host: "1fichier"),
            Make(id: 2, quality: "1080p BLURAY", host: "1fichier"),
            Make(id: 3, quality: "1080p REMUX", host: "1fichier"),
        };
        var hp = new[] { "1fichier" };
        var qp = new[] { "REMUX", "BLURAY", "WEB-DL", "WEBRIP" };
        var pick = BestVariantPicker.Pick(links, hp, qp, "MULTI");
        Assert.NotNull(pick);
        Assert.Equal(3, pick!.Id);
    }

    [Fact]
    public void Prefers_audio_when_quality_ties()
    {
        var links = new[]
        {
            Make(id: 1, quality: "1080p BLURAY", host: "1fichier", audio: "VFF"),
            Make(id: 2, quality: "1080p BLURAY", host: "1fichier", audio: "MULTI"),
        };
        var hp = new[] { "1fichier" };
        var qp = new[] { "BLURAY" };
        var pick = BestVariantPicker.Pick(links, hp, qp, "MULTI");
        Assert.NotNull(pick);
        Assert.Equal(2, pick!.Id);
    }

    [Fact]
    public void Falls_back_when_no_preferred_quality_present()
    {
        var links = new[]
        {
            Make(id: 1, quality: "1080p HDTV", host: "1fichier"),
        };
        var hp = new[] { "1fichier" };
        var qp = new[] { "REMUX", "BLURAY" }; // nothing matches
        var pick = BestVariantPicker.Pick(links, hp, qp, "MULTI");
        Assert.NotNull(pick);
        Assert.Equal(1, pick!.Id);
    }

    [Fact]
    public void Empty_input_returns_null()
    {
        Assert.Null(BestVariantPicker.Pick(Array.Empty<CatalogLinkEntity>(),
            new[] { "1fichier" }, new[] { "REMUX" }, "MULTI"));
    }

    [Fact]
    public void Prefers_hoster_priority_when_quality_and_audio_tie()
    {
        var links = new[]
        {
            Make(id: 1, quality: "1080p BLURAY", host: "rapidgator", normalizedHost: "rapidgator"),
            Make(id: 2, quality: "1080p BLURAY", host: "1fichier", normalizedHost: "1fichier"),
        };
        var hp = new[] { "1fichier", "rapidgator" };
        var qp = new[] { "BLURAY" };
        var pick = BestVariantPicker.Pick(links, hp, qp, "MULTI");
        Assert.NotNull(pick);
        Assert.Equal(2, pick!.Id);
    }

    /// <summary>
    /// Real catalog data for "Vol de nuit pour Los Angeles" (titleId 366684).
    /// Live /api/catalog/lookup was returning the 1080p variant because the
    /// resolution branch only recognised "2160p" — never "4K"/"UHD" which is
    /// the literal token ZT uses. This regression test pins the 4K choice for
    /// the user-reported case so a future tweak can't silently regress it.
    /// </summary>
    [Fact]
    public void Picks_4K_over_1080p_when_quality_string_uses_4K_alias()
    {
        var links = new[]
        {
            Make(id: 4693622, quality: "WEBRIP",         host: "Uploady",      sizeBytes:    644_874_240),
            Make(id: 4693623, quality: "WEBRIP",         host: "DailyUploads", sizeBytes:    644_874_240),
            Make(id: 4693624, quality: "WEBRIP",         host: "Rapidgator",   sizeBytes:    644_874_240),
            Make(id: 4693625, quality: "WEBRIP",         host: "Turbobit",     sizeBytes:    644_874_240),
            Make(id: 4693626, quality: "WEBRIP 720p",    host: "Uploady",      sizeBytes:  1_825_361_100),
            Make(id: 4693627, quality: "WEBRIP 720p",    host: "DailyUploads", sizeBytes:  1_825_361_100),
            Make(id: 4693628, quality: "WEBRIP 720p",    host: "Rapidgator",   sizeBytes:  1_825_361_100),
            Make(id: 4693629, quality: "WEBRIP 720p",    host: "Turbobit",     sizeBytes:  1_825_361_100),
            Make(id: 4693630, quality: "WEB-DL 1080p",   host: "Uploady",      sizeBytes:  5_583_457_484),
            Make(id: 4693631, quality: "WEB-DL 1080p",   host: "DailyUploads", sizeBytes:  5_583_457_484),
            Make(id: 4693632, quality: "WEB-DL 1080p",   host: "Rapidgator",   sizeBytes:  5_583_457_484),
            Make(id: 4693633, quality: "WEB-DL 1080p",   host: "Turbobit",     sizeBytes:  5_583_457_484),
            Make(id: 4693634, quality: "WEB-DL 4K",      host: "Uploady",      sizeBytes: 12_025_908_428),
            Make(id: 4693635, quality: "WEB-DL 4K",      host: "DailyUploads", sizeBytes: 12_025_908_428),
            Make(id: 4693636, quality: "WEB-DL 4K",      host: "Rapidgator",   sizeBytes: 12_025_908_428),
            Make(id: 4693637, quality: "WEB-DL 4K",      host: "Turbobit",     sizeBytes: 12_025_908_428),
        };
        var hp = new[] { "1fichier", "Rapidgator" };
        var qp = new[] { "REMUX", "BLURAY", "WEB-DL", "WEBRIP", "HDTV" };
        var pick = BestVariantPicker.Pick(links, hp, qp, "MULTI");
        Assert.NotNull(pick);
        Assert.Equal(4693636, pick!.Id);          // 4K Rapidgator (preferred host).
        Assert.Equal("WEB-DL 4K", pick.QualityName);
    }

    [Theory]
    [InlineData("WEB-DL 4K", 60)]
    [InlineData("BLURAY UHD", 60)]
    [InlineData("REMUX 2160p", 60)]
    [InlineData("WEB-DL 1080p", 30)]
    [InlineData("HDLIGHT 720p", 10)]
    [InlineData("DVDRIP", 0)]
    public void Resolution_bonus_recognises_2160p_4K_and_UHD_aliases(string quality, int expectedBonus)
    {
        var noPref = Array.Empty<string>();
        var bonus = BestVariantPicker.Score(
            qualityName: quality, audioLangs: null,
            normalizedHost: null, hostName: null,
            healthStatus: null, sizeBytes: null,
            hosterPriority: noPref, qualityPreference: noPref, audioPreference: "MULTI");
        Assert.Equal(expectedBonus, bonus);
    }

    private static CatalogLinkEntity Make(int id, string quality, string host, string? audio = null, string? normalizedHost = null, long? sizeBytes = null) =>
        new()
        {
            Id = id,
            QualityName = quality,
            HostName = host,
            NormalizedHost = normalizedHost ?? host.ToLowerInvariant(),
            AudioLangs = audio ?? "MULTI (TRUEFRENCH)",
            SizeBytes = sizeBytes
        };
}
