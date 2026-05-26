using LinkHarvester.Sources.Zt;
using Xunit;

namespace LinkHarvester.Sources.Tests.Zt;

public class ZtFrenchDateTests
{
    [Theory]
    [InlineData("26 mai 2026", 2026, 5, 26)]
    [InlineData("9 juillet 2025", 2025, 7, 9)]
    [InlineData("01 janvier 2025", 2025, 1, 1)]
    [InlineData("15 février 2026", 2026, 2, 15)]
    [InlineData("3 août 2024", 2024, 8, 3)]
    [InlineData("31 décembre 2025", 2025, 12, 31)]
    [InlineData("Publié le 26 mai 2026", 2026, 5, 26)]
    public void Parses_known_french_dates(string input, int year, int month, int day)
    {
        var actual = ZtFrenchDate.TryParse(input);

        Assert.NotNull(actual);
        Assert.Equal(year, actual!.Value.Year);
        Assert.Equal(month, actual.Value.Month);
        Assert.Equal(day, actual.Value.Day);
        Assert.Equal(TimeSpan.Zero, actual.Value.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a date")]
    [InlineData("32 mai 2026")]
    [InlineData("15 not_a_month 2026")]
    public void Returns_null_for_unparseable(string? input)
    {
        Assert.Null(ZtFrenchDate.TryParse(input));
    }

    [Fact]
    public void Accepts_diacritic_free_variant()
    {
        // "fevrier" without accent should still parse, since some scrapes lose accents.
        var actual = ZtFrenchDate.TryParse("9 fevrier 2025");
        Assert.NotNull(actual);
        Assert.Equal(2, actual!.Value.Month);
    }
}
