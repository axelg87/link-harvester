namespace LinkHarvester.Resolution.HealthCheck.Hosters;

/// <summary>
/// Static registry of all known per-host health matchers, in priority order.
/// First matcher whose <see cref="IHosterHealthMatcher.Matches"/> returns true wins.
/// </summary>
public static class HosterHealthMatcherRegistry
{
    public static readonly IReadOnlyList<IHosterHealthMatcher> Default = new IHosterHealthMatcher[]
    {
        new OneFichierMatcher(),
        new UploadyMatcher(),
        new RapidgatorMatcher(),
        new DailyUploadsMatcher(),
        new NitroFlareMatcher(),
    };
}
