namespace LinkHarvester.Resolution;

public interface ICapSolverBudget
{
    /// <summary>
    /// Returns true if the budget allows another solve attempt.
    /// </summary>
    Task<bool> CanSolveAsync(CancellationToken ct);

    /// <summary>
    /// Records a solve attempt (success or failure both cost a budget unit).
    /// </summary>
    Task RecordSolveAsync(decimal costUsd, CancellationToken ct);

    /// <summary>
    /// Gets the spend snapshot for the current month.
    /// </summary>
    Task<(decimal SpentUsd, int Calls, decimal Limit)> GetSpendAsync(CancellationToken ct);
}
