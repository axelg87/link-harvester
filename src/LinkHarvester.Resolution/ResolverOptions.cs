namespace LinkHarvester.Resolution;

public sealed class ResolverOptions
{
    /// <summary>Total budget for a single dl-protect resolution attempt.</summary>
    public int OverallTimeoutSeconds { get; set; } = 30;

    /// <summary>How many attempts before giving up (each may invoke CapSolver if enabled).</summary>
    public int MaxAttemptsPerLink { get; set; } = 2;
}

public static class NamedClients
{
    public const string DlProtect = "dlprotect";
}
