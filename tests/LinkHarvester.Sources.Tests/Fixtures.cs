namespace LinkHarvester.Sources.Tests;

public static class Fixtures
{
    public static string Load(string relativePath)
    {
        var dir = Path.GetDirectoryName(typeof(Fixtures).Assembly.Location)!;
        var path = Path.Combine(dir, relativePath);
        if (!File.Exists(path)) throw new FileNotFoundException($"Fixture not found: {path}");
        return File.ReadAllText(path);
    }
}
