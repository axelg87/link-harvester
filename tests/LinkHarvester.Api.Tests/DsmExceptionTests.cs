using System.Net;
using System.Net.Sockets;
using LinkHarvester.Synology;

namespace LinkHarvester.Api.Tests;

/// <summary>
/// Improvement #10 from IMPROVEMENTS.md: DSM error codes must surface as
/// user-actionable strings, not raw "code=105" messages. These tests pin
/// the friendly-message mapping for every code mentioned in the spec plus
/// the synthetic transport-failure codes.
/// </summary>
public class DsmExceptionTests
{
    [Theory]
    [InlineData(105, "admin", "DownloadStation permission")]
    [InlineData(105, "", "DownloadStation permission")]
    [InlineData(119, "admin", "session expired")]
    [InlineData(403, "admin", "username and password")]
    [InlineData(404, "admin", "endpoint not found")]
    [InlineData(407, "admin", "OTP")]
    public void ForCode_maps_known_codes_to_actionable_messages(int code, string username, string mustContain)
    {
        var ex = DsmException.ForCode(code, username, "http://nas.local:5000");
        Assert.Equal(code, ex.Code);
        Assert.Contains(mustContain, ex.HumanMessage, StringComparison.OrdinalIgnoreCase);
        // Must never leak the raw "code=N" pattern that improvement #10
        // explicitly wants to replace.
        Assert.DoesNotContain("code=", ex.HumanMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForCode_400_with_failed_url_calls_out_alldebrid_plugin()
    {
        var ex = DsmException.ForCode(400, "admin", "http://nas.local:5000",
            failedUrl: "https://rapidgator.net/file/abc");
        Assert.Equal(400, ex.Code);
        Assert.Equal("https://rapidgator.net/file/abc", ex.FailedUrl);
        Assert.Contains("rapidgator.net/file/abc", ex.HumanMessage);
        Assert.Contains("AllDebrid", ex.HumanMessage);
    }

    [Fact]
    public void ForCode_400_without_failed_url_still_mentions_destination_or_plugin()
    {
        var ex = DsmException.ForCode(400, "admin", "http://nas.local:5000");
        Assert.Equal(400, ex.Code);
        Assert.Null(ex.FailedUrl);
        Assert.Matches("destination|AllDebrid", ex.HumanMessage);
    }

    [Fact]
    public void ForCode_unknown_falls_back_to_code_in_text_but_friendly_phrasing()
    {
        var ex = DsmException.ForCode(9999, "admin", "http://nas.local:5000");
        Assert.Equal(9999, ex.Code);
        Assert.Contains("9999", ex.HumanMessage);
        // Phrasing must still be a sentence, not a raw token.
        Assert.StartsWith("Synology rejected", ex.HumanMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ForTransport_HostNotFound_says_check_hostname()
    {
        var inner = new HttpRequestException("DNS",
            new SocketException((int)SocketError.HostNotFound));
        var ex = DsmException.ForTransport("http://nope.invalid:5000", inner);
        Assert.Equal(DsmException.SyntheticUnreachable, ex.Code);
        Assert.Contains("hostname", ex.HumanMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nope.invalid", ex.HumanMessage);
    }

    [Fact]
    public void ForTransport_ConnectionRefused_says_check_downloadstation_port()
    {
        var inner = new HttpRequestException("refused",
            new SocketException((int)SocketError.ConnectionRefused));
        var ex = DsmException.ForTransport("http://nas.local:5000", inner);
        Assert.Equal(DsmException.SyntheticUnreachable, ex.Code);
        Assert.Contains("refused", ex.HumanMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DownloadStation", ex.HumanMessage);
    }

    [Fact]
    public void ForTransport_Timeout_says_timed_out()
    {
        var inner = new TaskCanceledException("timeout");
        var ex = DsmException.ForTransport("http://nas.local:5000", inner);
        Assert.Equal(DsmException.SyntheticTimeout, ex.Code);
        Assert.Contains("timed out", ex.HumanMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForHttp_404_says_endpoint_not_found_with_base_url()
    {
        var ex = DsmException.ForHttp(HttpStatusCode.NotFound, "http://nas.local:5000/foo", "html...");
        Assert.Equal(404, ex.Code);
        Assert.Contains("endpoint not found", ex.HumanMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("http://nas.local:5000/foo", ex.HumanMessage);
    }

    [Fact]
    public void ForUnparseable_does_not_leak_response_body()
    {
        const string body = "<html><script>alert('xss')</script>this isn't JSON";
        var ex = DsmException.ForUnparseable("http://nas.local:5000", body);
        Assert.Equal(DsmException.SyntheticUnparseable, ex.Code);
        Assert.DoesNotContain("<html>", ex.HumanMessage);
        Assert.DoesNotContain("script", ex.HumanMessage);
        Assert.Contains("nas.local", ex.HumanMessage);
    }

    [Fact]
    public void NotConfigured_points_to_settings()
    {
        var ex = DsmException.NotConfigured("Base URL");
        Assert.Equal(DsmException.SyntheticNotConfigured, ex.Code);
        Assert.Contains("Base URL", ex.HumanMessage);
        Assert.Contains("Settings", ex.HumanMessage);
    }
}
