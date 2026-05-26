using LinkHarvester.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Persistence;

public sealed class SettingsService : ISettingsService
{
    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly IDataProtector _protector;
    private readonly IConfiguration _config;
    private readonly ILogger<SettingsService> _log;
    private AppSettingsSnapshot _current = Empty;
    public event Action? Changed;

    private static readonly AppSettingsSnapshot Empty = new(
        SynologyBaseUrl: "",
        SynologyUsername: "",
        SynologyPassword: "",
        SynologyOtpCode: null,
        SynologyMovieDestination: "video/movies",
        SynologySeriesDestination: "video/series",
        SynologyAnimeDestination: "video/anime",
        ScanIntervalMinutes: 30,
        ScanOnStartup: true,
        HosterPriority: new[] { "1fichier", "Rapidgator" },
        AuthUsername: "admin",
        AuthPassword: "change-me",
        TmdbApiKey: "",
        TmdbEnrichmentEnabled: true,
        TmdbEnrichmentConcurrency: 4);

    public SettingsService(IDbContextFactory<HarvesterDbContext> factory,
                           IDataProtectionProvider dpProvider,
                           IConfiguration config,
                           ILogger<SettingsService> log)
    {
        _factory = factory;
        _protector = dpProvider.CreateProtector("LinkHarvester.AppSettings.v1");
        _config = config;
        _log = log;
    }

    public AppSettingsSnapshot Current => _current;

    public async Task LoadAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = SeedFromConfig();
            await using var write = await _factory.CreateDbContextAsync(ct);
            write.AppSettings.Add(row);
            await write.SaveChangesAsync(ct);
            _log.LogInformation("Seeded AppSettings row from configuration.");
        }
        _current = ToSnapshot(row);
        Changed?.Invoke();
    }

    public async Task UpdateAsync(AppSettingsSnapshot updated, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.AppSettings.FirstOrDefaultAsync(ct) ?? new AppSettingsEntity { Id = 1 };

        row.SynologyBaseUrl = updated.SynologyBaseUrl ?? string.Empty;
        row.SynologyConnectionMode = (int)updated.SynologyConnectionMode;
        row.SynologyQuickConnectId = updated.SynologyQuickConnectId ?? string.Empty;
        row.SynologyResolvedBaseUrl = updated.SynologyResolvedBaseUrl ?? string.Empty;
        row.SynologyResolvedAt = updated.SynologyResolvedAt;
        row.SynologyUsername = updated.SynologyUsername ?? string.Empty;
        if (!string.IsNullOrEmpty(updated.SynologyPassword))
            row.SynologyPasswordEncrypted = _protector.Protect(updated.SynologyPassword);
        row.SynologyOtpCode = string.IsNullOrWhiteSpace(updated.SynologyOtpCode) ? null : updated.SynologyOtpCode;
        row.SynologyMovieDestination = updated.SynologyMovieDestination ?? "video/movies";
        row.SynologySeriesDestination = updated.SynologySeriesDestination ?? "video/series";
        row.SynologyAnimeDestination = updated.SynologyAnimeDestination ?? "video/anime";

        row.ScanIntervalMinutes = Math.Max(0, updated.ScanIntervalMinutes);
        row.ScanOnStartup = updated.ScanOnStartup;
        row.HosterPriorityCsv = string.Join(',', (updated.HosterPriority ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

        row.AuthUsername = string.IsNullOrWhiteSpace(updated.AuthUsername) ? row.AuthUsername : updated.AuthUsername;
        if (!string.IsNullOrEmpty(updated.AuthPassword))
            row.AuthPasswordEncrypted = _protector.Protect(updated.AuthPassword);

        if (!string.IsNullOrEmpty(updated.TmdbApiKey))
            row.TmdbApiKeyEncrypted = _protector.Protect(updated.TmdbApiKey);
        row.TmdbEnrichmentEnabled = updated.TmdbEnrichmentEnabled;
        row.TmdbEnrichmentConcurrency = Math.Clamp(updated.TmdbEnrichmentConcurrency, 1, 8);

        row.UpdatedAt = DateTimeOffset.UtcNow;

        if (row.Id == 0 || !await db.AppSettings.AnyAsync(s => s.Id == row.Id, ct))
        {
            row.Id = 1;
            db.AppSettings.Add(row);
        }
        else
        {
            db.AppSettings.Update(row);
        }
        await db.SaveChangesAsync(ct);

        _current = ToSnapshot(row);
        Changed?.Invoke();
        _log.LogInformation("AppSettings updated.");
    }

    public bool VerifyCredentials(string username, string password)
    {
        var c = _current;
        return string.Equals(username, c.AuthUsername, StringComparison.Ordinal)
            && string.Equals(password, c.AuthPassword, StringComparison.Ordinal);
    }

    private AppSettingsEntity SeedFromConfig()
    {
        // First-run seed from appsettings.json (so a fresh deploy starts with
        // the developer-time defaults). Passwords are encrypted on write.
        var auth = _config.GetSection("Auth");
        var syn = _config.GetSection("Synology");
        var harv = _config.GetSection("Harvester");

        var entity = new AppSettingsEntity
        {
            Id = 1,
            AuthUsername = auth.GetValue<string>("Username") ?? "admin",
            SynologyBaseUrl = syn.GetValue<string>("BaseUrl") ?? "",
            SynologyConnectionMode = (int)(syn.GetValue<string>("ConnectionMode") is { } mode
                && Enum.TryParse<SynologyConnectionMode>(mode, ignoreCase: true, out var parsed)
                    ? parsed
                    : SynologyConnectionMode.Direct),
            SynologyQuickConnectId = syn.GetValue<string>("QuickConnectId") ?? "",
            SynologyResolvedBaseUrl = syn.GetValue<string>("ResolvedBaseUrl") ?? "",
            SynologyUsername = syn.GetValue<string>("Username") ?? "",
            SynologyOtpCode = syn.GetValue<string>("OtpCode"),
            SynologyMovieDestination = syn.GetValue<string>("DefaultMovieDestination") ?? "video/movies",
            SynologySeriesDestination = syn.GetValue<string>("DefaultSeriesDestination") ?? "video/series",
            SynologyAnimeDestination = syn.GetValue<string>("DefaultAnimeDestination") ?? "video/anime",
            ScanIntervalMinutes = harv.GetValue<int?>("ScanIntervalMinutes") ?? 30,
            ScanOnStartup = harv.GetValue<bool?>("ScanOnStartup") ?? true,
            HosterPriorityCsv = string.Join(',',
                harv.GetSection("HosterPriority").Get<string[]>() ?? new[] { "1fichier", "Rapidgator" }),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var authPwd = auth.GetValue<string>("Password") ?? "change-me";
        var synPwd = syn.GetValue<string>("Password") ?? "";
        entity.AuthPasswordEncrypted = _protector.Protect(authPwd);
        entity.SynologyPasswordEncrypted = string.IsNullOrEmpty(synPwd) ? "" : _protector.Protect(synPwd);
        return entity;
    }

    private AppSettingsSnapshot ToSnapshot(AppSettingsEntity row) => new(
        SynologyBaseUrl: row.SynologyBaseUrl,
        SynologyUsername: row.SynologyUsername,
        SynologyPassword: SafeUnprotect(row.SynologyPasswordEncrypted),
        SynologyOtpCode: row.SynologyOtpCode,
        SynologyMovieDestination: row.SynologyMovieDestination,
        SynologySeriesDestination: row.SynologySeriesDestination,
        SynologyAnimeDestination: string.IsNullOrEmpty(row.SynologyAnimeDestination) ? "video/anime" : row.SynologyAnimeDestination,
        ScanIntervalMinutes: row.ScanIntervalMinutes,
        ScanOnStartup: row.ScanOnStartup,
        HosterPriority: row.HosterPriorityCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        AuthUsername: row.AuthUsername,
        AuthPassword: SafeUnprotect(row.AuthPasswordEncrypted),
        TmdbApiKey: SafeUnprotect(row.TmdbApiKeyEncrypted),
        TmdbEnrichmentEnabled: row.TmdbEnrichmentEnabled,
        TmdbEnrichmentConcurrency: row.TmdbEnrichmentConcurrency <= 0 ? 4 : row.TmdbEnrichmentConcurrency,
        SynologyConnectionMode: Enum.IsDefined(typeof(SynologyConnectionMode), row.SynologyConnectionMode)
            ? (SynologyConnectionMode)row.SynologyConnectionMode
            : SynologyConnectionMode.Direct,
        SynologyQuickConnectId: row.SynologyQuickConnectId,
        SynologyResolvedBaseUrl: row.SynologyResolvedBaseUrl,
        SynologyResolvedAt: row.SynologyResolvedAt);

    private string SafeUnprotect(string ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return string.Empty;
        try { return _protector.Unprotect(ciphertext); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to decrypt setting; returning empty.");
            return string.Empty;
        }
    }
}
