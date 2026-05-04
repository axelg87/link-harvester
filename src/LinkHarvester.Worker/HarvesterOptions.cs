namespace LinkHarvester.Worker;

public sealed class HarvesterOptions
{
    public int ScanIntervalMinutes { get; set; } = 30;
    public bool ScanOnStartup { get; set; } = true;
    public int MaxArticlesPerScan { get; set; } = 60;
    public int MaxConcurrentResolutions { get; set; } = 1;
    public List<string> HosterPriority { get; set; } = new() { "1fichier", "Rapidgator" };
    public bool ResolveAutomaticallyAfterIngest { get; set; } = false;
}
