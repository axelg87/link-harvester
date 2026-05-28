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

    private static CatalogLinkEntity Make(int id, string quality, string host, string? audio = null, string? normalizedHost = null) =>
        new()
        {
            Id = id,
            QualityName = quality,
            HostName = host,
            NormalizedHost = normalizedHost ?? host.ToLowerInvariant(),
            AudioLangs = audio ?? "MULTI"
        };
}
