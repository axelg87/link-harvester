using LinkHarvester.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LinkHarvester.Api.Tests;

public class MigrationDiscoveryTests
{
    [Fact]
    public void Every_migration_type_is_discoverable_by_ef()
    {
        var options = new DbContextOptionsBuilder<HarvesterDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new HarvesterDbContext(options);

        var migrationIds = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var migrationTypes = typeof(HarvesterDbContext).Assembly.GetTypes()
            .Where(t => t.IsAssignableTo(typeof(Migration)) && !t.IsAbstract)
            .ToList();

        Assert.NotEmpty(migrationTypes);
        foreach (var type in migrationTypes)
        {
            var attr = type.GetCustomAttributes(typeof(MigrationAttribute), inherit: false)
                .Cast<MigrationAttribute>()
                .SingleOrDefault();
            Assert.NotNull(attr);
            Assert.Contains(attr!.Id, migrationIds);
        }
    }

    [Fact]
    public async Task Migrations_create_schema_that_matches_current_model()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"linkharvester-migrations-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<HarvesterDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            await using var db = new HarvesterDbContext(options);

            await db.Database.MigrateAsync();

            foreach (var entity in db.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (string.IsNullOrWhiteSpace(table)) continue;

                var storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());
                var modelColumns = entity.GetProperties()
                    .Select(p => p.GetColumnName(storeObject))
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var dbColumns = await ReadSqliteColumnsAsync(db, table);

                foreach (var column in modelColumns)
                {
                    Assert.Contains(column!, dbColumns);
                }
            }
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    private static async Task<HashSet<string>> ReadSqliteColumnsAsync(HarvesterDbContext db, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }
        return columns;
    }
}
