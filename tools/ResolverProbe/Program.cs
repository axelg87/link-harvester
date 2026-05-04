using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Resolution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var urls = args.Length > 0 ? args : new[]
{
    "https://dl-protect.link/bb84328e?fn=TG9zIFRpZ3JlcyBbV0VCLURMIDEwODBwXSAtIE1VTFRJIChUUlVFRlJFTkNIKQ%3D%3D&rl=a2",
    "https://dl-protect.link/f70dfa80?fn=TG9zIFRpZ3JlcyBbV0VCLURMIDEwODBwXSAtIE1VTFRJIChUUlVFRlJFTkNIKQ%3D%3D&rl=a2",
    "https://dl-protect.link/d1b71d1c?fn=VGhlIEJveXMgLSBTYWlzb24gNSBFcGlzb2RlIDEgLSBbVkZd&rl=b2",
    "https://dl-protect.link/d74e5827?fn=VGhlIEJveXMgLSBTYWlzb24gNSBFcGlzb2RlIDUgLSBbVkZd&rl=b2"
};

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
                          .SetMinimumLevel(LogLevel.Warning));
services.Configure<ResolverOptions>(o => { o.OverallTimeoutSeconds = 30; o.MaxAttemptsPerLink = 2; });
services.Configure<CapSolverOptions>(o => { o.Enabled = false; });
services.AddPooledDbContextFactory<HarvesterDbContext>(o => o.UseSqlite("Data Source=/tmp/lh-probe.db"));
services.AddSingleton<ICapSolverBudget, CapSolverBudget>();
services.AddHttpClient<CapSolverClient>();
services.AddHttpClient(NamedClients.DlProtect);
services.AddSingleton<ILinkResolver, DlProtectResolver>();

var sp = services.BuildServiceProvider();
await using (var db = sp.GetRequiredService<IDbContextFactory<HarvesterDbContext>>().CreateDbContext())
    await db.Database.EnsureCreatedAsync();

var resolver = sp.GetRequiredService<ILinkResolver>();

int ok = 0, fail = 0;
foreach (var url in urls)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var outcome = await resolver.ResolveAsync(url, CancellationToken.None);
    sw.Stop();
    if (outcome.Result == ResolutionAttemptResult.Success && outcome.Links.Count > 0)
    {
        ok++;
        foreach (var l in outcome.Links)
            Console.WriteLine($"[OK {sw.Elapsed.TotalSeconds:0.0}s] {url[35..50]}.. -> [{l.Hoster}] {l.Url}");
    }
    else
    {
        fail++;
        Console.WriteLine($"[FAIL] {url[35..50]}.. -> {outcome.Result} ({outcome.ErrorMessage})");
    }
}
Console.WriteLine($"---\nresults: {ok} ok, {fail} fail");
