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
}
