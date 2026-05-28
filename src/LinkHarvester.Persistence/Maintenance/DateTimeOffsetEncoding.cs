namespace LinkHarvester.Persistence.Maintenance;

/// <summary>
/// Mirror of EF Core 8's
/// <c>Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter</c>.
/// Used by <see cref="Catalog.CatalogIngestor"/> when it bulk-inserts via
/// raw SQL — going through this helper instead of
/// <c>ToUnixTimeMilliseconds()</c> keeps raw-SQL writes schema-compatible
/// with EF reads. Verified against EF source at
/// <c>dotnet/efcore@v8.0.10/src/EFCore/Storage/ValueConversion/DateTimeOffsetToBinaryConverter.cs</c>
/// and covered by <c>DateTimeOffsetEncodingTests</c>.
/// </summary>
public static class DateTimeOffsetEncoding
{
    /// <summary>
    /// EF Core's packed long: <c>((Ticks / 1000) &lt;&lt; 11) | (offsetMinutes &amp; 0x7FF)</c>
    /// — the low 11 bits hold the offset as a two's-complement signed
    /// integer (legal range −840…+840 min, anything else throws on decode).
    /// </summary>
    public static long Encode(DateTimeOffset dto)
    {
        var offsetMin = (long)dto.Offset.TotalMinutes;
        return ((dto.Ticks / 1000) << 11) | (offsetMin & 0x7FF);
    }
}
