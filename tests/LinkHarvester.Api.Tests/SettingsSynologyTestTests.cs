using LinkHarvester.Api.Endpoints;
using LinkHarvester.Core;

namespace LinkHarvester.Api.Tests;

/// <summary>
/// Regression: "Test connection" must honour Synology fields from the form
/// (request body), not only values already persisted to the DB.
/// </summary>
public class SettingsSynologyTestTests
{
    private static readonly AppSettingsSnapshot Saved = new(
        SynologyBaseUrl: "http://saved.local:5000",
        SynologyUsername: "saved-user",
        SynologyPassword: "saved-pass",
        SynologyOtpCode: null,
        SynologyMovieDestination: "video/movies",
        SynologySeriesDestination: "video/series",
        ScanIntervalMinutes: 30,
        ScanOnStartup: true,
        HosterPriority: new[] { "1fichier" },
        AuthUsername: "admin",
        AuthPassword: "x",
        TmdbApiKey: "",
        TmdbEnrichmentEnabled: false,
        TmdbEnrichmentConcurrency: 4);

    [Fact]
    public void MergeSynologyForTest_null_req_returns_saved()
    {
        var merged = SettingsEndpoints.MergeSynologyForTest(Saved, null);
        Assert.Equal(Saved, merged);
    }

    [Fact]
    public void MergeSynologyForTest_overrides_base_url_and_username()
    {
        var req = new SettingsEndpoints.UpdateSettingsDto(
            SynologyBaseUrl: "http://form.local:5001",
            SynologyUsername: "form-user",
            SynologyPassword: null,
            SynologyOtpCode: null,
            SynologyMovieDestination: null,
            SynologySeriesDestination: null,
            ScanIntervalMinutes: null,
            ScanOnStartup: null,
            HosterPriority: null,
            AuthUsername: null,
            AuthPassword: null,
            TmdbApiKey: null,
            TmdbEnrichmentEnabled: null,
            TmdbEnrichmentConcurrency: null);

        var merged = SettingsEndpoints.MergeSynologyForTest(Saved, req);
        Assert.Equal("http://form.local:5001", merged.SynologyBaseUrl);
        Assert.Equal("form-user", merged.SynologyUsername);
        Assert.Equal("saved-pass", merged.SynologyPassword);
    }

    [Fact]
    public void MergeSynologyForTest_empty_password_keeps_saved()
    {
        var req = new SettingsEndpoints.UpdateSettingsDto(
            SynologyBaseUrl: null,
            SynologyUsername: null,
            SynologyPassword: "",
            SynologyOtpCode: null,
            SynologyMovieDestination: null,
            SynologySeriesDestination: null,
            ScanIntervalMinutes: null,
            ScanOnStartup: null,
            HosterPriority: null,
            AuthUsername: null,
            AuthPassword: null,
            TmdbApiKey: null,
            TmdbEnrichmentEnabled: null,
            TmdbEnrichmentConcurrency: null);

        var merged = SettingsEndpoints.MergeSynologyForTest(Saved, req);
        Assert.Equal("saved-pass", merged.SynologyPassword);
    }

    [Fact]
    public void MergeSynologyForTest_non_empty_password_replaces_saved()
    {
        var req = new SettingsEndpoints.UpdateSettingsDto(
            SynologyBaseUrl: null,
            SynologyUsername: null,
            SynologyPassword: "new-pass",
            SynologyOtpCode: "123456",
            SynologyMovieDestination: null,
            SynologySeriesDestination: null,
            ScanIntervalMinutes: null,
            ScanOnStartup: null,
            HosterPriority: null,
            AuthUsername: null,
            AuthPassword: null,
            TmdbApiKey: null,
            TmdbEnrichmentEnabled: null,
            TmdbEnrichmentConcurrency: null);

        var merged = SettingsEndpoints.MergeSynologyForTest(Saved, req);
        Assert.Equal("new-pass", merged.SynologyPassword);
        Assert.Equal("123456", merged.SynologyOtpCode);
    }
}
