using LinkHarvester.Persistence.Maintenance;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Xunit;

namespace LinkHarvester.Api.Tests;

/// <summary>
/// The previous version of these tests verified my SQL formula matched my
/// C# <c>EncodeBinary</c> — both of which were wrong (they added <c>+1024</c>
/// to the offset bits, which is NOT what EF Core 8 does). This rewrite
/// pins everything to <see cref="DateTimeOffsetToBinaryConverter"/> itself
/// so the same mistake can't recur.
/// </summary>
public class DateTimeOffsetRepairTests
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
    public void EncodeBinary_matches_actual_EF_converter_for_UTC(string iso)
    {
        var dto = DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind);
        var efValue = EfEncode(dto);
        var ours = DateTimeOffsetRepairService.EncodeBinary(dto);
        Assert.Equal(efValue, ours);
        // And the round-trip through EF must produce the same DateTimeOffset.
        Assert.Equal(dto, EfDecode(ours));
    }

    [Theory]
    [InlineData(60)]    // +1h
    [InlineData(-60)]   // -1h
    [InlineData(330)]   // India +5:30
    [InlineData(-300)]  // EST -5h
    [InlineData(840)]   // boundary +14h
    [InlineData(-840)]  // boundary -14h
    public void EncodeBinary_matches_actual_EF_converter_for_non_UTC_offsets(int offsetMin)
    {
        var dto = new DateTimeOffset(2026, 5, 28, 12, 0, 0, TimeSpan.FromMinutes(offsetMin));
        Assert.Equal(EfEncode(dto), DateTimeOffsetRepairService.EncodeBinary(dto));
        Assert.Equal(dto, EfDecode(DateTimeOffsetRepairService.EncodeBinary(dto)));
    }

    /// <summary>
    /// The unix-ms re-encode SQL must produce the same long as
    /// <c>EncodeBinary(DateTimeOffset.FromUnixTimeMilliseconds(unixMs))</c>
    /// — proven by running both in an in-memory SQLite and comparing.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(1_000L)]
    [InlineData(1_700_000_000_000L)]
    [InlineData(1_735_689_600_000L)]
    [InlineData(1_767_225_600_000L)]
    public void Unix_ms_SQL_reencode_matches_EF_encoder(long unixMs)
    {
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        var expected = EfEncode(dto);

        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // The SQL formula used by DateTimeOffsetRepairService.RepairAsync.
        cmd.CommandText = "SELECT (621355968000000 + $u * 10) * 2048";
        cmd.Parameters.AddWithValue("$u", unixMs);
        var actual = (long)cmd.ExecuteScalar()!;

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The bad-offset heal SQL — clearing the low 11 bits via
    /// <c>col &amp; ~2047</c> — must produce a value EF can decode without
    /// throwing, and must preserve the ticks portion (the clock time
    /// survives the heal).
    /// </summary>
    [Theory]
    [InlineData(841)]   // start of illegal band
    [InlineData(1024)]  // my buggy +1024 signature
    [InlineData(1100)]
    [InlineData(1207)]  // end of illegal band
    public void Heal_SQL_recovers_corrupt_row_to_UTC_with_ticks_intact(long illegalBits)
    {
        // Build a "corrupted" value: take a legitimate 2025-01-01 UTC
        // encoding and stomp its low 11 bits with an illegal pattern.
        var goodDto = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var goodEncoded = EfEncode(goodDto);
        var corrupted = (goodEncoded & ~2047L) | illegalBits;

        // Decoding the corrupted value must throw — that's the premise.
        Assert.Throws<ArgumentOutOfRangeException>(() => EfDecode(corrupted));

        // Apply the heal SQL.
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT $v & ~2047";
        cmd.Parameters.AddWithValue("$v", corrupted);
        var healed = (long)cmd.ExecuteScalar()!;

        // Healed value must round-trip through EF.
        var roundTrip = EfDecode(healed);
        // Clock time preserved.
        Assert.Equal(goodDto.UtcDateTime, roundTrip.UtcDateTime);
        // Offset reset to UTC.
        Assert.Equal(TimeSpan.Zero, roundTrip.Offset);
    }

    /// <summary>
    /// The legal/illegal stored-bit window the WHERE clause uses
    /// (<c>BETWEEN 841 AND 1207</c>) must match EF's actual decode
    /// failure window. Probe every value in [0, 2047]; values that throw
    /// must be exactly the [841, 1207] band, and nothing else.
    /// </summary>
    [Fact]
    public void Illegal_low11_window_matches_EF_decode_failure_set()
    {
        var goodDto = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var goodHigh = EfEncode(goodDto) & ~2047L;

        for (long bits = 0; bits < 2048; bits++)
        {
            var encoded = goodHigh | bits;
            var inIllegalBand = bits >= 841 && bits <= 1207;
            var threw = false;
            try { EfDecode(encoded); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            Assert.True(threw == inIllegalBand,
                $"bits={bits}: threw={threw}, expectedThrew={inIllegalBand}");
        }
    }
}
