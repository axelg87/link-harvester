using System.Net;
using System.Text;
using LinkHarvester.Synology;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinkHarvester.Api.Tests;

public class QuickConnectResolverTests
{
    [Fact]
    public async Task ResolveAsync_follows_regional_resolver_and_returns_working_relay()
    {
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.Host == "global.quickconnect.to")
            {
                return Json("""[{"command":"get_server_info","errno":4,"sites":["dec.quickconnect.to"],"suberrno":2,"version":1}]""");
            }

            if (req.RequestUri.Host == "dec.quickconnect.to")
            {
                return Json("""
                    [{
                      "command":"get_server_info",
                      "errno":0,
                      "server":{"ds_state":"CONNECTED"},
                      "service":{
                        "port":5001,
                        "ext_port":0,
                        "pingpong":"DISCONNECTED",
                        "relay_dn":"synr-cz3.TEST.direct.quickconnect.to",
                        "relay_port":30773
                      },
                      "smartdns":{
                        "host":"TEST.direct.quickconnect.to",
                        "external":"syn4-test.TEST.direct.quickconnect.to"
                      }
                    }]
                    """);
            }

            if (req.RequestUri.Host == "synr-cz3.test.direct.quickconnect.to")
            {
                return Json("""
                    {
                      "success": true,
                      "data": {
                        "SYNO.API.Auth": { "path": "entry.cgi" },
                        "SYNO.DownloadStation2.Task": { "path": "entry.cgi" }
                      }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var resolver = new QuickConnectResolver(new HttpClient(handler), NullLogger<QuickConnectResolver>.Instance);

        var result = await resolver.ResolveAsync("test", CancellationToken.None);

        Assert.Equal("https://synr-cz3.TEST.direct.quickconnect.to:30773", result.BaseUrl);
        Assert.Contains("https://synr-cz3.TEST.direct.quickconnect.to:30773", result.ProbedUrls);
    }

    [Fact]
    public async Task ResolveAsync_rejects_endpoint_without_downloadstation_api()
    {
        var handler = new StubHttpHandler(req =>
        {
            if (req.RequestUri!.Host == "global.quickconnect.to")
            {
                return Json("""
                    [{
                      "command":"get_server_info",
                      "errno":0,
                      "service":{
                        "port":5001,
                        "pingpong":"DISCONNECTED",
                        "relay_dn":"synr-cz3.TEST.direct.quickconnect.to",
                        "relay_port":30773
                      }
                    }]
                    """);
            }

            if (req.RequestUri.Host == "synr-cz3.test.direct.quickconnect.to")
            {
                return Json("""{"success":true,"data":{"SYNO.API.Auth":{"path":"entry.cgi"}}}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var resolver = new QuickConnectResolver(new HttpClient(handler), NullLogger<QuickConnectResolver>.Instance);

        var ex = await Assert.ThrowsAsync<QuickConnectResolveException>(
            () => resolver.ResolveAsync("test", CancellationToken.None));

        Assert.Contains("none returned DSM DownloadStation", ex.Message);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}
