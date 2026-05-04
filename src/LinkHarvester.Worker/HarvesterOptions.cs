namespace LinkHarvester.Worker;

/// <summary>
/// Static, developer-time-only options. Everything user-facing lives on
/// <see cref="LinkHarvester.Core.AppSettingsSnapshot"/> and is editable from the UI.
/// </summary>
public sealed class HarvesterOptions
{
    public int MaxArticlesPerScan { get; set; } = 60;
}
