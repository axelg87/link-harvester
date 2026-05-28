using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace LinkHarvester.Worker.Telegram;

/// <summary>
/// Polls <see cref="FollowingDetectionLogEntity"/> for unconsumed rows and
/// fires one Telegram message per detection. Marks consumed only on
/// successful delivery so a transient bot outage doesn't drop the
/// notification.
///
/// No work done unless the bot is configured AND an owner chat is set.
/// Tight polling interval (60s) is fine — the table is tiny and
/// near-empty 99% of the time.
/// </summary>
public sealed class TelegramNotificationDispatcher : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly ISettingsService _settings;
    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly TelegramBotService _bot;
    private readonly ILogger<TelegramNotificationDispatcher> _log;

    public TelegramNotificationDispatcher(
        ISettingsService settings,
        IDbContextFactory<HarvesterDbContext> factory,
        TelegramBotService bot,
        ILogger<TelegramNotificationDispatcher> log)
    {
        _settings = settings;
        _factory = factory;
        _bot = bot;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay to let the bot service finish wiring up.
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "telegram dispatcher iteration failed");
            }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var s = _settings.Current;
        if (string.IsNullOrWhiteSpace(s.TelegramBotToken) || s.TelegramOwnerChatId == 0) return;
        if (_bot.Client is not { } client) return;

        await using var db = await _factory.CreateDbContextAsync(ct);
        var pending = await db.FollowingDetectionLog
            .Where(d => d.ConsumedAt == null)
            .OrderBy(d => d.DetectedAt)
            .Take(20)
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        var titleIds = pending.Select(p => p.CatalogTitleId).Distinct().ToList();
        var titles = await db.CatalogTitles.AsNoTracking()
            .Where(t => titleIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        foreach (var det in pending)
        {
            if (!titles.TryGetValue(det.CatalogTitleId, out var title)) continue;
            var linkId = det.CatalogLinkId;
            var body = $"📺 New episode available\n{title.TitleName} — {det.EpisodeCode}";
            var keyboard = linkId is int lid
                ? new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("📥 Send to NAS", $"send:{lid}"),
                        InlineKeyboardButton.WithCallbackData("🚫 Skip", $"skip:{det.Id}")
                    }
                })
                : null;
            try
            {
                await client.SendMessage(s.TelegramOwnerChatId, body, replyMarkup: keyboard, cancellationToken: ct);
                det.ConsumedAt = DateTimeOffset.UtcNow;
                det.ConsumedBy = "telegram_notified";
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "failed to deliver detection {Id} to telegram", det.Id);
                // Leave ConsumedAt null so the next pass retries.
                return;
            }
        }
    }
}
