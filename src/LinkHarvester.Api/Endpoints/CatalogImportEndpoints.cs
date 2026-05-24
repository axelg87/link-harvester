using System.IO.Compression;
using System.Text.Json;
using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Synology;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Endpoints;

public static class CatalogImportEndpoints
{
    public static IEndpointRouteBuilder MapCatalogImportEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/catalog/import").RequireAuthorization()
            .DisableAntiforgery();

        // ── Upload local file (multipart) ────────────────────────────────────
        grp.MapPost("/upload", async (
            HttpRequest req,
            CatalogIngestionRunner runner,
            IConfiguration config,
            CancellationToken ct) =>
        {
            if (!req.HasFormContentType)
                return Results.BadRequest(new { error = "expected multipart/form-data with a 'file' field" });

            var form = await req.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault();
            if (file is null) return Results.BadRequest(new { error = "missing file" });

            var dbDir = Path.GetDirectoryName(Path.GetFullPath(config.GetValue<string>("Persistence:DbPath") ?? "data/linkharvester.db"))!;
            var stagedPath = Path.Combine(dbDir, $"catalog-upload-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bin");
            await using (var fs = File.Create(stagedPath))
            {
                await file.CopyToAsync(fs, ct);
            }

            if (!runner.TryStart(_ => Task.FromResult(OpenForIngest(stagedPath)), $"upload:{file.FileName}", out var reason))
            {
                File.Delete(stagedPath);
                return Results.Conflict(new { error = reason });
            }

            return Results.Accepted("/api/catalog/import/status", new { fileName = file.FileName, bytes = file.Length });
        }).Accepts<IFormFile>("multipart/form-data");

        // ── Import from public URL (download server-side then ingest) ────────
        grp.MapPost("/from-url", async (
            ImportFromUrlReq req,
            CatalogIngestionRunner runner,
            IConfiguration config,
            IHttpClientFactory httpFactory,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Url))
                return Results.BadRequest(new { error = "missing url" });

            var log = loggerFactory.CreateLogger("CatalogImportFromUrl");
            var dbDir = Path.GetDirectoryName(Path.GetFullPath(config.GetValue<string>("Persistence:DbPath") ?? "data/linkharvester.db"))!;
            var stagedPath = Path.Combine(dbDir, $"catalog-import-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bin");

            // If a previously-downloaded staged file is present (e.g. the machine
            // restarted mid-ingest), reuse it — the ingest is idempotent thanks to
            // ON CONFLICT DO NOTHING, so we save a 1+ GB re-download.
            // Pick by size, not by timestamp: a half-finished re-download is younger
            // than the full original we want to keep using.
            var existing = Directory.GetFiles(dbDir, "catalog-import-*.bin")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.Length)
                .FirstOrDefault()?.FullName;

            if (!runner.TryStart(async token =>
            {
                string finalPath;
                if (existing is not null && new FileInfo(existing).Length > 0)
                {
                    log.LogInformation("reusing existing staged file {Path} ({Bytes:N0} bytes)", existing, new FileInfo(existing).Length);
                    finalPath = existing;
                }
                else
                {
                    var resolvedUrl = await GoogleDriveDirectAsync(req.Url, httpFactory.CreateClient("import"), log, token);
                    var http = httpFactory.CreateClient("import");
                    using var resp = await http.GetAsync(resolvedUrl, HttpCompletionOption.ResponseHeadersRead, token);
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync(token);
                    await using (var fs = File.Create(stagedPath))
                    {
                        await src.CopyToAsync(fs, token);
                    }
                    log.LogInformation("downloaded {Bytes:N0} bytes to {Path}",
                        new FileInfo(stagedPath).Length, stagedPath);
                    finalPath = stagedPath;
                }
                return OpenForIngest(finalPath);
            }, $"url:{req.Url}", out var reason))
            {
                return Results.Conflict(new { error = reason });
            }

            return Results.Accepted("/api/catalog/import/status");
        });

        // Cleanup staged files older than 6h on the next request — keeps the
        // volume from leaking 1.6 GB per import attempt.
        grp.MapPost("/cleanup", (IConfiguration config) =>
        {
            var dbDir = Path.GetDirectoryName(Path.GetFullPath(config.GetValue<string>("Persistence:DbPath") ?? "data/linkharvester.db"))!;
            var removed = 0;
            foreach (var f in Directory.GetFiles(dbDir, "catalog-import-*.bin"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(6))
                    {
                        File.Delete(f);
                        removed++;
                    }
                }
                catch { }
            }
            foreach (var f in Directory.GetFiles(dbDir, "catalog-upload-*.bin"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(6))
                    {
                        File.Delete(f);
                        removed++;
                    }
                }
                catch { }
            }
            return Results.Ok(new { removed });
        });

        grp.MapGet("/status", (CatalogIngestionRunner runner) => Results.Ok(runner.Snapshot()));

        grp.MapPost("/cancel", (CatalogIngestionRunner runner) => { runner.Cancel(); return Results.Ok(); });

        return routes;
    }

    /// <summary>
    /// Opens the staged file for streaming, transparently gunzip if it's
    /// a .gz / .json.gz upload.
    /// </summary>
    private static Stream OpenForIngest(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 256 * 1024, options: FileOptions.SequentialScan | FileOptions.Asynchronous);
        // Sniff gzip magic.
        var first = fs.ReadByte();
        var second = fs.ReadByte();
        fs.Position = 0;
        if (first == 0x1f && second == 0x8b)
        {
            return new GZipStream(fs, CompressionMode.Decompress, leaveOpen: false);
        }
        return fs;
    }

    /// <summary>
    /// Resolves Google Drive sharing URLs into a direct-download URL that bypasses
    /// the &gt;100 MB virus-scan warning.
    /// Accepts: <c>https://drive.google.com/file/d/&lt;ID&gt;/view?usp=...</c> or
    /// <c>https://drive.google.com/open?id=&lt;ID&gt;</c>; passes other URLs through.
    /// </summary>
    private static async Task<string> GoogleDriveDirectAsync(string input, HttpClient http, ILogger log, CancellationToken ct)
    {
        var url = input.Trim();
        if (!url.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase)) return url;
        var id = TryExtractDriveId(url);
        if (id is null) return url;

        // Use the "uc?export=download&confirm=t" pattern that works for large files.
        var first = $"https://drive.usercontent.google.com/download?id={id}&export=download&confirm=t";
        log.LogInformation("rewrote Google Drive url to {Url}", first);
        return first;
    }

    private static string? TryExtractDriveId(string url)
    {
        // /file/d/{ID}/
        var m = System.Text.RegularExpressions.Regex.Match(url, @"/file/d/(?<id>[^/?#]+)");
        if (m.Success) return m.Groups["id"].Value;
        // ?id={ID}
        m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]id=(?<id>[^&]+)");
        if (m.Success) return m.Groups["id"].Value;
        return null;
    }

    public sealed record ImportFromUrlReq(string Url);
}
