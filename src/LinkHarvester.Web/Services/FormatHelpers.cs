namespace LinkHarvester.Web.Services;

/// <summary>
/// Tiny formatting helpers shared by Razor pages. Lives outside any single
/// page so two copies don't drift — the production-readiness audit flagged
/// the duplicate <c>FormatSize</c> in Catalog and Inbox specifically.
/// </summary>
public static class FormatHelpers
{
    private const double KB = 1024d;
    private const double MB = 1024d * 1024;
    private const double GB = 1024d * 1024 * 1024;

    public static string FormatSize(long bytes)
    {
        if (bytes >= GB) return $"{bytes / GB:0.0} GB";
        if (bytes >= MB) return $"{bytes / MB:0.0} MB";
        return $"{bytes / KB:0.0} KB";
    }

    public static string FormatSize(long? bytes) => bytes.HasValue ? FormatSize(bytes.Value) : "";
}
