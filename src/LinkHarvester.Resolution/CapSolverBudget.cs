using LinkHarvester.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LinkHarvester.Resolution;

public sealed class CapSolverBudget : ICapSolverBudget
{
    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly CapSolverOptions _opts;

    public CapSolverBudget(IDbContextFactory<HarvesterDbContext> factory, IOptions<CapSolverOptions> opts)
    {
        _factory = factory;
        _opts = opts.Value;
    }

    public async Task<bool> CanSolveAsync(CancellationToken ct)
    {
        var (spent, _, limit) = await GetSpendAsync(ct);
        return spent + _opts.AssumedCostPerSolveUsd <= limit;
    }

    public async Task RecordSolveAsync(decimal costUsd, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.CapSolverSpends
            .FirstOrDefaultAsync(c => c.Year == now.Year && c.Month == now.Month, ct);
        if (row is null)
        {
            row = new CapSolverSpendEntity { Year = now.Year, Month = now.Month };
            db.CapSolverSpends.Add(row);
        }
        row.SpentUsd += costUsd;
        row.Calls += 1;
        await db.SaveChangesAsync(ct);
    }

    public async Task<(decimal SpentUsd, int Calls, decimal Limit)> GetSpendAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.CapSolverSpends
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Year == now.Year && c.Month == now.Month, ct);
        return (row?.SpentUsd ?? 0m, row?.Calls ?? 0, _opts.MonthlyBudgetUsd);
    }
}
