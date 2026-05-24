using LinkHarvester.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Tests;

internal sealed class TestDbContextFactory : IDbContextFactory<HarvesterDbContext>
{
    private readonly string _connectionString;
    public TestDbContextFactory(string connectionString) => _connectionString = connectionString;

    public HarvesterDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<HarvesterDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new HarvesterDbContext(options);
    }
}
