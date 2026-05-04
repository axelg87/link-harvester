namespace LinkHarvester.Resolution;

public sealed class ResolverOptions
{
    /// <summary>Where to keep the persistent Chromium profile (cookies, fingerprint warm-up).</summary>
    public string PersistentProfileDirectory { get; set; } = "data/playwright-profile";

    /// <summary>Run headed (visible browser) for debugging.</summary>
    public bool Headed { get; set; } = false;

    /// <summary>Total budget for a single dl-protect resolution.</summary>
    public int OverallTimeoutSeconds { get; set; } = 60;

    /// <summary>How many attempts before giving up (each may invoke CapSolver if enabled).</summary>
    public int MaxAttemptsPerLink { get; set; } = 3;
}
