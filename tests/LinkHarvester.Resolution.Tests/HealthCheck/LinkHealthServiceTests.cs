using System.Net;
using LinkHarvester.Resolution.HealthCheck;
using LinkHarvester.Resolution.HealthCheck.Hosters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkHarvester.Resolution.Tests.HealthCheck;

public class LinkHealthServiceTests
{
    private sealed class FixedMatcher : IHosterHealthMatcher
    {
        private readonly string _needle;
        private readonly LinkHealth _verdict;
        public FixedMatcher(string needle, LinkHealth verdict) { _needle = needle; _verdict = verdict; }
        public bool Matches(string url, string? hosterName) =>
            url.Contains(_needle, StringComparison.OrdinalIgnoreCase) ||
            (hosterName?.Contains(_needle, StringComparison.OrdinalIgnoreCase) ?? false);
        public LinkHealth Evaluate(int? status, string? body, Exception? err) => _verdict;
    }

    private static (ILinkHealthService svc, List<string> requested) BuildSvc(params (string needle, LinkHealth verdict)[] matchers)
    {
        var requested = new List<string>();
        var handler = new RecordingHandler(requested);
        var factory = new TestHttpClientFactory(handler);
        var ms = matchers.Select(m => (IHosterHealthMatcher)new FixedMatcher(m.needle, m.verdict)).ToList();
        var svc = new LinkHealthService(factory, NullLogger<LinkHealthService>.Instance, ms, new UnknownHosterMatcher());
        return (svc, requested);
    }

    [Fact]
    public async Task CheckUntilAlive_returns_first_non_dead_and_stops()
    {
        var (svc, hits) = BuildSvc(
            ("dead-host", LinkHealth.Dead),
            ("alive-host", LinkHealth.Alive),
            ("never", LinkHealth.Alive));

        var r = await svc.CheckUntilAliveAsync(new[]
        {
            ("https://dead-host/a", (string?)"dead-host"),
            ("https://alive-host/b", (string?)"alive-host"),
            ("https://never/c", (string?)"never"),
        }, CancellationToken.None);

        Assert.Equal(LinkHealth.Alive, r.Health);
        Assert.Equal(2, hits.Count); // never probes the third candidate
    }

    [Fact]
    public async Task CheckUntilAlive_treats_unknown_as_alive_for_shortcircuit()
    {
        var (svc, hits) = BuildSvc(
            ("dead-host", LinkHealth.Dead),
            ("blocked", LinkHealth.Unknown),
            ("never", LinkHealth.Alive));

        var r = await svc.CheckUntilAliveAsync(new[]
        {
            ("https://dead-host/a", (string?)"dead-host"),
            ("https://blocked/b", (string?)"blocked"),
            ("https://never/c", (string?)"never"),
        }, CancellationToken.None);

        Assert.Equal(LinkHealth.Unknown, r.Health);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task CheckUntilAlive_returns_last_dead_when_all_dead()
    {
        var (svc, hits) = BuildSvc(
            ("a", LinkHealth.Dead),
            ("b", LinkHealth.Dead),
            ("c", LinkHealth.Dead));

        var r = await svc.CheckUntilAliveAsync(new[]
        {
            ("https://a/x", (string?)"a"),
            ("https://b/x", (string?)"b"),
            ("https://c/x", (string?)"c"),
        }, CancellationToken.None);

        Assert.Equal(LinkHealth.Dead, r.Health);
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public async Task CheckUntilAlive_no_candidates_returns_unknown()
    {
        var (svc, _) = BuildSvc(("z", LinkHealth.Alive));
        var r = await svc.CheckUntilAliveAsync(Array.Empty<(string, string?)>(), CancellationToken.None);
        Assert.Equal(LinkHealth.Unknown, r.Health);
    }

    // ----- minimal HTTP plumbing for tests -----
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly List<string> _hits;
        public RecordingHandler(List<string> hits) { _hits = hits; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _hits.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<title>test</title>")
            });
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public TestHttpClientFactory(HttpMessageHandler handler) { _handler = handler; }
        public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
    }
}
