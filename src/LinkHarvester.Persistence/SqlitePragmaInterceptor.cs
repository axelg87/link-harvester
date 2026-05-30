using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LinkHarvester.Persistence;

/// <summary>
/// Applies performance PRAGMAs on every SQLite connection open. Connection
/// pragmas don't persist across connections, so the EF pool would otherwise
/// hand out connections that fell back to default cache_size / mmap_size.
///
/// Settings:
///   <c>journal_mode=WAL</c>        — readers don't block writers; required for our concurrency
///   <c>synchronous=NORMAL</c>      — safe with WAL, ~2× faster commits than FULL
///   <c>cache_size=-131072</c>      — 128 MB page cache (negative = KB)
///   <c>mmap_size=536870912</c>     — 512 MB mmap window over the file
///   <c>temp_store=MEMORY</c>       — temp B-trees in RAM, not /tmp
///   <c>foreign_keys=ON</c>         — match EF's expectation
///
/// Sized conservatively for the Fly shared-cpu-1x / 2 GB VM. The ingester
/// and TMDB enricher also hold sizable in-memory caches; raise these only
/// after watching memory under real load.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA cache_size = -131072;
PRAGMA mmap_size = 536870912;
PRAGMA temp_store = MEMORY;
PRAGMA foreign_keys = ON;";
        cmd.ExecuteNonQuery();
    }
}
