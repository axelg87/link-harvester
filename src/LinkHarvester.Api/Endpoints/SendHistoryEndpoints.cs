using System.Text.Json;
using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Synology;
using LinkHarvester.Worker;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkHarvester.Api.Endpoints;

public static class SendHistoryEndpoints
{
    public static IEndpointRouteBuilder MapSendHistoryEndpoints(this IEndpointRouteBuilder routes)
    {
        var grp = routes.MapGroup("/api/sends").RequireAuthorization();

        grp.MapGet("", async (
            HarvesterDbContext db,
            [FromQuery] string? status,
            [FromQuery] string? source,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            CancellationToken ct = default) =>
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);
            var q = db.Submissions.AsNoTracking().AsQueryable();
            if (Enum.TryParse<SubmissionStatus>(status, true, out var st))
                q = q.Where(s => s.Status == st);
            if (Enum.TryParse<SendSourceKind>(source, true, out var src))
                q = q.Where(s => s.Source == src);

            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(s => s.SubmittedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(s => new SendAttemptSummaryDto(
                    s.Id, s.Source.ToString(), s.Status.ToString(), s.DisplayTitle,
                    s.UrlCount, s.SubmittedAt, s.CompletedAt, s.ResponseMessage,
                    s.ResendOfSubmissionId))
                .ToListAsync(ct);

            return Results.Ok(new SendHistoryPageDto(total, page, pageSize, rows));
        });

        grp.MapPost("/{id:int}/resend", async (
            int id,
            HarvesterDbContext db,
            SubmissionService articleSubmissions,
            IDownloadStationClient dsm,
            ISettingsService settings,
            CancellationToken ct) =>
        {
            var original = await db.Submissions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (original is null) return Results.NotFound();

            if (original.Source == SendSourceKind.ZtArticle && original.ArticleId is int articleId)
            {
                var retry = await articleSubmissions.SendToDsmAsync(articleId, ct);
                var retryRow = await db.Submissions.FirstAsync(s => s.Id == retry.Id, ct);
                retryRow.ResendOfSubmissionId = original.Id;
                retryRow.AttemptNumber = original.AttemptNumber + 1;
                await db.SaveChangesAsync(ct);
                return Results.Ok(ToResendResult(retryRow));
            }

            var linkIds = JsonSerializer.Deserialize<List<int>>(original.CatalogLinkIdsJson) ?? new();
            var links = await db.CatalogLinks.Include(l => l.Title)
                .Where(l => linkIds.Contains(l.Id))
                .ToListAsync(ct);
            if (links.Count == 0) return Results.BadRequest(new { error = "Original catalog links no longer exist." });

            var urls = links.Select(l => l.LinkUrl).Distinct().ToList();
            var dest = links.All(l => string.Equals(l.Title.CategoryName, "Films", StringComparison.OrdinalIgnoreCase))
                ? settings.Current.SynologyMovieDestination
                : settings.Current.SynologySeriesDestination;
            var attempt = new SubmissionEntity
            {
                Source = SendSourceKind.Catalog,
                CatalogTitleId = original.CatalogTitleId,
                CatalogLinkIdsJson = JsonSerializer.Serialize(linkIds),
                DisplayTitle = original.DisplayTitle,
                Destination = dest,
                UrlCount = urls.Count,
                SubmittedUrlsJson = JsonSerializer.Serialize(urls),
                Status = SubmissionStatus.Pending,
                SubmittedAt = DateTimeOffset.UtcNow,
                ResendOfSubmissionId = original.Id,
                AttemptNumber = original.AttemptNumber + 1
            };
            db.Submissions.Add(attempt);
            await db.SaveChangesAsync(ct);

            try
            {
                var taskIds = await dsm.CreateTasksAsync(urls, dest, ct);
                attempt.Status = SubmissionStatus.Sent;
                attempt.DsmTaskIdsJson = JsonSerializer.Serialize(taskIds);
            }
            catch (DsmException dx)
            {
                attempt.Status = SubmissionStatus.Failed;
                attempt.ResponseMessage = dx.HumanMessage;
                attempt.DsmErrorCode = dx.Code;
                attempt.DsmFailedUrl = dx.FailedUrl;
            }
            attempt.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResendResult(attempt));
        });

        return routes;
    }

    private static SendResendResultDto ToResendResult(SubmissionEntity s)
        => new(s.Id, s.Status.ToString(), s.ResponseMessage,
            JsonSerializer.Deserialize<List<string>>(s.DsmTaskIdsJson) ?? new());

    public sealed record SendHistoryPageDto(int Total, int Page, int PageSize, List<SendAttemptSummaryDto> Items);

    public sealed record SendAttemptSummaryDto(
        int Id,
        string Source,
        string Status,
        string DisplayTitle,
        int UrlCount,
        DateTimeOffset SubmittedAt,
        DateTimeOffset? CompletedAt,
        string? ResponseMessage,
        int? ResendOfSubmissionId);

    public sealed record SendResendResultDto(int SubmissionId, string Status, string? Response, List<string> TaskIds);
}
