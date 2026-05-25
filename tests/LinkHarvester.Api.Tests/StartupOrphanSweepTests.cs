using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Tests;

/// <summary>
/// Pins the startup orphan-sweep behaviour added in Program.cs: any
/// <c>CatalogImportRuns</c> row left with <c>Status='running'</c> after a
/// process restart must be flipped to <c>'failed'</c>, so the UI doesn't
/// show a permanently-stuck "ingestion in progress" banner and the runner's
/// "another ingestion is already running" guard can't trip incorrectly.
/// Mirrors the exact SQL the Program.cs startup hook runs.
/// </summary>
public class StartupOrphanSweepTests : IAsyncLifetime
{
    private string _dbPath = "";
    private string _connStr = "";
    private TestDbContextFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lh-orphan-test-{Guid.NewGuid():N}.db");
        _connStr = $"Data Source={_dbPath}";
        _factory = new TestDbContextFactory(_connStr);
        await using var db = _factory.CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
        foreach (var sidecar in new[] { _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { if (File.Exists(sidecar)) File.Delete(sidecar); } catch { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Marks_only_running_rows_as_failed_and_preserves_terminal_states()
    {
        // Seed: one orphaned 'running', plus one of each terminal state we
        // must leave alone (succeeded, failed, cancelled, succeeded_fts_skipped).
        await using (var db = _factory.CreateDbContext())
        {
            db.CatalogImportRuns.AddRange(
                new CatalogImportRunEntity
                {
                    StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
                    Source = "upload",
                    SourceDescription = "orphan",
                    Status = "running",
                    Notes = null,
                },
                new CatalogImportRunEntity
                {
                    StartedAt = DateTimeOffset.UtcNow.AddDays(-1),
                    FinishedAt = DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(5),
                    Source = "upload",
                    SourceDescription = "done",
                    Status = "succeeded",
                    Notes = null,
                },
                new CatalogImportRunEntity
                {
                    StartedAt = DateTimeOffset.UtcNow.AddDays(-2),
                    FinishedAt = DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(3),
                    Source = "url",
                    SourceDescription = "earlier failure",
                    Status = "failed",
                    Notes = "original failure",
                },
                new CatalogImportRunEntity
                {
                    StartedAt = DateTimeOffset.UtcNow.AddDays(-3),
                    FinishedAt = DateTimeOffset.UtcNow.AddDays(-3).AddMinutes(1),
                    Source = "url",
                    SourceDescription = "user aborted",
                    Status = "cancelled",
                    Notes = null,
                });
            await db.SaveChangesAsync();
        }

        // Run the same SQL the Program.cs startup hook runs.
        await using (var db = _factory.CreateDbContext())
        {
            var n = await db.Database.ExecuteSqlRawAsync(@"
                UPDATE CatalogImportRuns
                SET Status = 'failed',
                    Notes = COALESCE(Notes, '') || ' [orphaned by app restart]'
                WHERE Status = 'running';");
            Assert.Equal(1, n);
        }

        // Verify post-state.
        await using (var db = _factory.CreateDbContext())
        {
            var rows = await db.CatalogImportRuns.OrderBy(r => r.Id).ToListAsync();
            Assert.Equal(4, rows.Count);

            // Row 1: orphan → now failed, Notes annotated.
            Assert.Equal("failed", rows[0].Status);
            Assert.Contains("[orphaned by app restart]", rows[0].Notes ?? "");

            // Row 2: succeeded → untouched.
            Assert.Equal("succeeded", rows[1].Status);
            Assert.Null(rows[1].Notes);

            // Row 3: failed → untouched, original Notes preserved.
            Assert.Equal("failed", rows[2].Status);
            Assert.Equal("original failure", rows[2].Notes);

            // Row 4: cancelled → untouched.
            Assert.Equal("cancelled", rows[3].Status);
        }
    }

    [Fact]
    public async Task Idempotent_second_sweep_is_a_no_op()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.CatalogImportRuns.Add(new CatalogImportRunEntity
            {
                StartedAt = DateTimeOffset.UtcNow,
                Source = "upload",
                Status = "running",
            });
            await db.SaveChangesAsync();
        }

        const string sql = @"
            UPDATE CatalogImportRuns
            SET Status = 'failed',
                Notes = COALESCE(Notes, '') || ' [orphaned by app restart]'
            WHERE Status = 'running';";

        await using (var db = _factory.CreateDbContext())
        {
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync(sql));
        }
        await using (var db = _factory.CreateDbContext())
        {
            // Second sweep finds nothing because the row is now 'failed'.
            Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(sql));
        }
    }
}
