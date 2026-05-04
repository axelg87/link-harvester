namespace LinkHarvester.Resolution;

public sealed class CapSolverOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.capsolver.com";
    public decimal MonthlyBudgetUsd { get; set; } = 20m;
    public decimal AssumedCostPerSolveUsd { get; set; } = 0.0008m;
    public int PollIntervalMs { get; set; } = 2000;
    public int MaxPollAttempts { get; set; } = 60;
    public bool Enabled { get; set; } = false;
}
