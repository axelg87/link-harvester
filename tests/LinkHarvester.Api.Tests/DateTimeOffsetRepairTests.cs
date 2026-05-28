using System.Globalization;
using LinkHarvester.Persistence.Maintenance;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LinkHarvester.Api.Tests;

/// <summary>
/// The repair UPDATE rewrites unix-ms longs into EF's binary-encoded
/// format using SQL arithmetic. If the SQL diverges from
/// <see cref="DateTimeOffsetRepairService.EncodeBinary"/> by a single bit,
/// every healed row reads back as the wrong moment in time — silently. So
/// we verify the algebraic identity end-to-end against an in-memory SQLite.
/// </summary>
public class DateTimeOffsetRepairTests
{
    [Theory]
    [InlineData(0L)]                    // 1970-01-01 00:00:00 UTC
    [InlineData(1L)]                    // +1ms
    [InlineData(1_000L)]                // 1970-01-01 00:00:01 UTC
    [InlineData(1_700_000_000_000L)]    // mid-2023
    [InlineData(1_735_689_600_000L)]    // 2025-01-01 00:00:00 UTC
    [InlineData(1_767_225_600_000L)]    // 2026-01-01 00:00:00 UTC
    [InlineData(2_000_000_000_000L)]    // ~2033
    public void Sql_formula_matches_C_sharp_EncodeBinary(long unixMs)
    {
        // C# reference.
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        var expected = DateTimeOffsetRepairService.EncodeBinary(dto);

        // SQL formula in an in-memory SQLite, matching the production
        // UPDATE arithmetic exactly.
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT (621355968000000 + $u * 10) * 2048 + 1024";
        cmd.Parameters.AddWithValue("$u", unixMs);
        var actual = (long)(cmd.ExecuteScalar()
            ?? throw new InvalidOperationException("null result"));

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Belt-and-suspenders heal path: if a row's already-encoded value has
    /// low 11 bits that decode to an out-of-range offset, the SQL forces
    /// the offset back to UTC (encoded as 1024) while keeping the upper
    /// bits (which encode the wall-clock ticks).
    /// </summary>
    [Theory]
    [InlineData(0L)]      // offset stored = 0 ⇒ decoded = -1024 (out of range)
    [InlineData(183L)]    // offset stored = 183 ⇒ decoded = -841 (out of range)
    [InlineData(1865L)]   // offset stored = 1865 ⇒ decoded = +841 (out of range)
    [InlineData(2047L)]   // offset stored = 2047 ⇒ decoded = +1023 (out of range)
    public void BadOffset_heal_pins_offset_bits_to_UTC(long badOffsetBits)
    {
        // Take a plausible mid-2024 encoded value and corrupt its offset bits.
        var baseDto = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000L);
        var baseEncoded = DateTimeOffsetRepairService.EncodeBinary(baseDto);
        var corrupted = (baseEncoded & ~2047L) | badOffsetBits;

        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ($v & ~2047) | 1024";
        cmd.Parameters.AddWithValue("$v", corrupted);
        var healed = (long)(cmd.ExecuteScalar()
            ?? throw new InvalidOperationException("null result"));

        // High bits preserved, low bits pinned to UTC (1024).
        Assert.Equal(baseEncoded & ~2047L, healed & ~2047L);
        Assert.Equal(1024L, healed & 2047L);

        // The healed value can be decoded into a DateTimeOffset without
        // throwing — which is the whole point.
        var ticks = (healed >> 11) * 1000;
        var offsetMin = (int)((healed & 2047) - 1024);
        var roundtrip = new DateTimeOffset(ticks, TimeSpan.FromMinutes(offsetMin));
        Assert.Equal(TimeSpan.Zero, roundtrip.Offset);
    }
}
