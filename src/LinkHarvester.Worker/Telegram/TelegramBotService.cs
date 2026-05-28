using LinkHarvester.Core;
using LinkHarvester.Persistence;
using LinkHarvester.Persistence.Catalog;
using LinkHarvester.Synology;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace LinkHarvester.Worker.Telegram;

/// <summary>
/// Long-polling Telegram bot. Authorises only the chat id stored in
/// <c>AppSettingsEntity.TelegramOwnerChatId</c>; all other chats are
/// silently dropped (logged at debug only).
///
/// Commands:
///   /start             Reply with chat id + authorisation status. First-time helper.
///   /find &lt;query&gt;       Search the catalog, return top 3 with inline send keys.
///   /recent            Last 10 ingested ZT articles with one-tap send keys.
///
/// Callback queries route through this same service: each inline button
/// carries a compact payload that we parse and dispatch.
/// </summary>
public sealed class TelegramBotService : IHostedService
{
    private readonly ISettingsService _settings;
    private readonly IDbContextFactory<HarvesterDbContext> _factory;
    private readonly IServiceProvider _sp;
    private readonly ILogger<TelegramBotService> _log;

    private CancellationTokenSource? _cts;
    private TelegramBotClient? _client;
    private Task? _runLoop;
    private string? _runningToken;

    public TelegramBotService(
        ISettingsService settings,
        IDbContextFactory<HarvesterDbContext> factory,
        IServiceProvider sp,
        ILogger<TelegramBotService> log)
    {
        _settings = settings;
        _factory = factory;
        _sp = sp;
        _log = log;
    }

    public TelegramBotClient? Client => _client;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _settings.Changed += OnSettingsChanged;
        TryStart();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.Changed -= OnSettingsChanged;
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    private void OnSettingsChanged()
    {
        var token = _settings.Current.TelegramBotToken;
        if (!string.Equals(_runningToken, token, StringComparison.Ordinal))
        {
            _cts?.Cancel();
            TryStart();
        }
    }

    private void TryStart()
    {
        var token = _settings.Current.TelegramBotToken;
        _runningToken = token;
        _client = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.LogInformation("Telegram bot idle: no token configured.");
            return;
        }

        _cts = new CancellationTokenSource();
        _client = new TelegramBotClient(token);
        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery }
        };
        _runLoop = Task.Run(async () =>
        {
            try
            {
                await _client.ReceiveAsync(
                    updateHandler: HandleUpdateAsync,
                    errorHandler: HandleErrorAsync,
                    receiverOptions: receiverOptions,
                    cancellationToken: _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Telegram bot loop crashed");
            }
        });
        _log.LogInformation("Telegram bot started (polling).");
    }

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        var ownerChat = _settings.Current.TelegramOwnerChatId;
        var chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id ?? 0;

        try
        {
            if (update.Message is { Text: { } text } msg)
            {
                // /start is the only command authorised before the chat is
                // bound — it returns the chat id so the user can paste it
                // into Settings. Everything else requires owner-chat auth.
                if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
                {
                    var bound = ownerChat == msg.Chat.Id;
                    var body = bound
                        ? $"Hello. Authorised owner chat (id={msg.Chat.Id}). Try /find <query> or /recent."
                        : $"Chat id: {msg.Chat.Id}\n\nPaste this into Settings → Telegram → Owner chat id, then send any command.";
                    await client.SendMessage(msg.Chat.Id, body, cancellationToken: ct);
                    return;
                }

                if (ownerChat == 0 || msg.Chat.Id != ownerChat)
                {
                    _log.LogDebug("Telegram update dropped: unauthorised chat {ChatId}", msg.Chat.Id);
                    return;
                }

                if (text.StartsWith("/find ", StringComparison.OrdinalIgnoreCase))
                {
                    var q = text["/find ".Length..].Trim();
                    await SendSearchResultsAsync(client, msg.Chat.Id, q, ct);
                    return;
                }
                if (text.Equals("/find", StringComparison.OrdinalIgnoreCase))
                {
                    await client.SendMessage(msg.Chat.Id, "Usage: /find <title>", cancellationToken: ct);
                    return;
                }
                if (text.StartsWith("/recent", StringComparison.OrdinalIgnoreCase))
                {
                    await SendRecentAsync(client, msg.Chat.Id, ct);
                    return;
                }
            }
            else if (update.CallbackQuery is { Data: { } data } cq && cq.Message is not null)
            {
                if (ownerChat == 0 || cq.Message.Chat.Id != ownerChat)
                {
                    await client.AnswerCallbackQuery(cq.Id, "Unauthorised.", cancellationToken: ct);
                    return;
                }
                await HandleCallbackAsync(client, cq, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Telegram update handling failed for chat {ChatId}", chatId);
        }
    }

    private async Task SendSearchResultsAsync(ITelegramBotClient client, long chatId, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            await client.SendMessage(chatId, "Type at least 2 characters after /find.", cancellationToken: ct);
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct);
        var escaped = SanitizeFtsQuery(query);
        var ids = await db.Database.SqlQueryRaw<int>(
            "SELECT rowid AS Value FROM CatalogTitlesFts WHERE CatalogTitlesFts MATCH {0} LIMIT 20",
            escaped).ToListAsync(ct);
        if (ids.Count == 0)
        {
            await client.SendMessage(chatId, $"No results for: {query}", cancellationToken: ct);
            return;
        }

        var titles = await db.CatalogTitles.AsNoTracking()
            .Where(t => !t.IsHidden && ids.Contains(t.Id))
            .Include(t => t.Metadata)
            .Include(t => t.Links)
            .Include(t => t.Episodes).ThenInclude(e => e.Links)
            .Take(3)
            .ToListAsync(ct);

        var s = _settings.Current;
        var lines = new List<string>();
        var buttons = new List<InlineKeyboardButton[]>();
        var idx = 0;
        foreach (var t in titles)
        {
            idx++;
            var allLinks = t.Links.Concat(t.Episodes.SelectMany(e => e.Links)).ToList();
            var best = BestVariantPicker.Pick(allLinks, s.HosterPriority, s.EffectiveQualityPreference, s.AudioPreference);
            var year = t.Metadata?.Year is int y ? $" ({y})" : string.Empty;
            var icon = t.EpisodeCount > 0 ? "📺" : "🎬";
            var bestStr = best is null
                ? $"{icon} {idx}. {t.TitleName}{year} — no link"
                : $"{icon} {idx}. {t.TitleName}{year} — {best.QualityName ?? "?"} · {best.HostName}";
            lines.Add(bestStr);
            if (best is not null)
            {
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData($"📥 Send {idx}", $"send:{best.Id}")
                });
            }
        }

        var keyboard = new InlineKeyboardMarkup(buttons);
        await client.SendMessage(
            chatId,
            string.Join('\n', lines),
            replyMarkup: keyboard,
            cancellationToken: ct);
    }

    private async Task SendRecentAsync(ITelegramBotClient client, long chatId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var recent = await db.CatalogTitles.AsNoTracking()
            .Where(t => !t.IsHidden)
            .OrderByDescending(t => t.LastSeenAt)
            .Take(10)
            .Include(t => t.Links)
            .Include(t => t.Episodes).ThenInclude(e => e.Links)
            .ToListAsync(ct);

        if (recent.Count == 0)
        {
            await client.SendMessage(chatId, "Catalog is empty.", cancellationToken: ct);
            return;
        }

        var s = _settings.Current;
        var lines = new List<string>();
        var buttons = new List<InlineKeyboardButton[]>();
        var idx = 0;
        foreach (var t in recent)
        {
            idx++;
            var allLinks = t.Links.Concat(t.Episodes.SelectMany(e => e.Links)).ToList();
            var best = BestVariantPicker.Pick(allLinks, s.HosterPriority, s.EffectiveQualityPreference, s.AudioPreference);
            var icon = t.EpisodeCount > 0 ? "📺" : "🎬";
            lines.Add($"{icon} {idx}. {t.TitleName}");
            if (best is not null)
            {
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData($"📥 Send {idx}", $"send:{best.Id}")
                });
            }
        }
        await client.SendMessage(
            chatId,
            "Recent in catalog:\n" + string.Join('\n', lines),
            replyMarkup: new InlineKeyboardMarkup(buttons),
            cancellationToken: ct);
    }

    private async Task HandleCallbackAsync(ITelegramBotClient client, CallbackQuery cq, CancellationToken ct)
    {
        var data = cq.Data ?? string.Empty;
        if (data.StartsWith("send:", StringComparison.Ordinal))
        {
            if (!int.TryParse(data["send:".Length..], out var linkId))
            {
                await client.AnswerCallbackQuery(cq.Id, "Bad link id", cancellationToken: ct);
                return;
            }
            await client.AnswerCallbackQuery(cq.Id, "Sending…", cancellationToken: ct);
            var result = await SendLinkAsync(linkId, ct);
            await client.EditMessageText(
                chatId: cq.Message!.Chat.Id,
                messageId: cq.Message.MessageId,
                text: (cq.Message.Text ?? string.Empty) + "\n\n" + (result.Ok ? "✅ Sent." : $"❌ {result.Error}"),
                cancellationToken: ct);
            return;
        }
        if (data.StartsWith("skip:", StringComparison.Ordinal))
        {
            if (long.TryParse(data["skip:".Length..], out var detectionId))
            {
                await using var db = await _factory.CreateDbContextAsync(ct);
                var row = await db.FollowingDetectionLog.FirstOrDefaultAsync(d => d.Id == (int)detectionId, ct);
                if (row is not null)
                {
                    row.ConsumedAt = DateTimeOffset.UtcNow;
                    row.ConsumedBy = "telegram_skip";
                    await db.SaveChangesAsync(ct);
                }
            }
            await client.AnswerCallbackQuery(cq.Id, "Skipped.", cancellationToken: ct);
        }
    }

    private async Task<(bool Ok, string? Error)> SendLinkAsync(int linkId, CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<HarvesterDbContext>>()
            .CreateDbContextAsync(ct);
        var submissions = scope.ServiceProvider.GetRequiredService<SubmissionService>();
        var dsm = scope.ServiceProvider.GetRequiredService<IDownloadStationClient>();

        var link = await db.CatalogLinks.AsNoTracking()
            .Include(l => l.Title)
            .FirstOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null) return (false, "Link not found.");

        try
        {
            if (string.Equals(link.LinkSource, "zt", StringComparison.OrdinalIgnoreCase)
                && link.HarvesterArticleId is int articleId)
            {
                var sub = await submissions.SendToDsmAsync(articleId, ct);
                return sub.Status == SubmissionStatus.Sent
                    ? (true, null)
                    : (false, sub.ResponseMessage ?? "Send failed.");
            }
            await dsm.CreateTasksAsync(new[] { link.LinkUrl }, null, ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private Task HandleErrorAsync(ITelegramBotClient client, Exception ex, CancellationToken ct)
    {
        if (ex is ApiRequestException are)
            _log.LogWarning("Telegram API error {Code}: {Message}", are.ErrorCode, are.Message);
        else
            _log.LogWarning(ex, "Telegram poll error");
        return Task.CompletedTask;
    }

    private static string SanitizeFtsQuery(string raw)
    {
        var cleaned = new string(raw.Select(c => char.IsLetterOrDigit(c) || c == ' ' ? c : ' ').ToArray());
        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return "\"\"";
        return string.Join(' ', tokens.Select(t => $"\"{t}\"*"));
    }
}
