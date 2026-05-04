using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LinkHarvester.Persistence;

/// <summary>
/// Used by `dotnet ef` to instantiate the context at design time.
/// Must live in this project so migrations can be added against it.
/// </summary>
public class HarvesterDbContextFactory : IDesignTimeDbContextFactory<HarvesterDbContext>
{
    public HarvesterDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<HarvesterDbContext>()
            .UseSqlite("Data Source=linkharvester.db")
            .Options;
        return new HarvesterDbContext(opts);
    }
}
