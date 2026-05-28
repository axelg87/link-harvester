using LinkHarvester.Persistence.Maintenance;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Xunit;

namespace LinkHarvester.Api.Tests;

/// <summary>
/// <see cref="DateTimeOffsetEncoding.Encode"/> must match EF Core 8's
/// <c>DateTimeOffsetToBinaryConverter</c> exactly — anything <i>writing</i>
/// to a DateTimeOffset column via raw SQL (currently
/// <see cref="LinkHarvester.Persistence.Catalog.CatalogIngestor"/>) goes
/// through it, and a one-bit mismatch silently corrupts every row.
/// Pinned against the actual EF converter rather than a paper formula
/// because the original bug was a paper-formula mismatch.
/// </summary>
public class DateTimeOffsetEncodingTests
{
    private static readonly DateTimeOffsetToBinaryConverter EfConverter = new();
    private static long EfEncode(DateTimeOffset dto) => (long)EfConverter.ConvertToProvider(dto)!;
    private static DateTimeOffset EfDecode(long v) => (DateTimeOffset)EfConverter.ConvertFromProvider(v)!;

    [Theory]
    [InlineData("2026-05-28T11:00:00Z")]
    [InlineData("1970-01-01T00:00:00Z")]
    [InlineData("0001-01-01T00:00:00Z")]
    [InlineData("2024-01-01T00:00:00Z")]
    [InlineData("2025-12-31T23:59:59Z")]
    public void Matches_EF_converter_for_UTC(string iso)
    {
        var dto = DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(EfEncode(dto), DateTimeOffsetEncoding.Encode(dto));
        Assert.Equal(dto, EfDecode(DateTimeOffsetEncoding.Encode(dto)));
    }

    [Theory]
    [InlineData(60)]    // +1h
    [InlineData(-60)]   // -1h
    [InlineData(330)]   // India +5:30
    [InlineData(-300)]  // EST -5h
    [InlineData(840)]   // boundary +14h
    [InlineData(-840)]  // boundary -14h
    public void Matches_EF_converter_for_non_UTC_offsets(int offsetMin)
    {
        var dto = new DateTimeOffset(2026, 5, 28, 12, 0, 0, TimeSpan.FromMinutes(offsetMin));
        Assert.Equal(EfEncode(dto), DateTimeOffsetEncoding.Encode(dto));
        Assert.Equal(dto, EfDecode(DateTimeOffsetEncoding.Encode(dto)));
    }
}
