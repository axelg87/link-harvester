using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Worker.Backfill;

/// <summary>
/// Tracks at most one active backfill and one active health-sweep at any time.
/// Provides "fire and forget" trigger methods used by the admin tab; the
/// public <c>Snapshot()</c> exposes live counters for the UI to poll.
/// </summary>
public sealed class BackfillJobRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackfillJobRunner> _log;
    private readonly object _gate = new();

    private CancellationTokenSource? _backfillCts;
    private Task? _backfillTask;
    private BackfillStatus _backfillStatus = BackfillStatus.Idle();

    private CancellationTokenSource? _sweepCts;
    private Task? _sweepTask;
    private SweepStatus _sweepStatus = SweepStatus.Idle();

    public BackfillJobRunner(IServiceScopeFactory scopeFactory, ILogger<BackfillJobRunner> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public bool TryStartBackfill(string sourceId, string kind, DateTimeOffset fromDate, int startPage, out string? reason)
    {
        lock (_gate)
        {
            if (_backfillTask is not null && !_backfillTask.IsCompleted)
            {
                reason = "backfill already running";
                return false;
            }

            _backfillCts = new CancellationTokenSource();
            _backfillStatus = new BackfillStatus(
                Running: true, SourceId: sourceId, Kind: kind, FromDate: fromDate,
                StartPage: startPage, LastCompletedPage: 0,
                Discovered: 0, Promoted: 0, Skipped: 0,
                StartedAt: DateTimeOffset.UtcNow, Error: null);

            var ct = _backfillCts.Token;
            _backfillTask = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<BackfillService>();
                    var run = await svc.RunAsync(sourceId, kind, fromDate, startPage, ct);
                    _backfillStatus = _backfillStatus with
                    {
                        Running = false,
                        LastCompletedPage = run.LastCompletedPage,
                        Discovered = run.Discovered,
                        Promoted = run.Promoted,
                        Skipped = run.Skipped,
                        Error = run.Error,
                    };
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Backfill task crashed");
                    _backfillStatus = _backfillStatus with { Running = false, Error = ex.Message };
                }
            }, ct);

            reason = null;
            return true;
        }
    }

    public bool TryStartSweep(string? hosterFilter, bool resume, out string? reason)
    {
        lock (_gate)
        {
            if (_sweepTask is not null && !_sweepTask.IsCompleted)
            {
                reason = "sweep already running";
                return false;
            }

            _sweepCts = new CancellationTokenSource();
            _sweepStatus = new SweepStatus(
                Running: true, HosterFilter: hosterFilter,
                Checked: 0, Alive: 0, Dead: 0, Unknown: 0, HiddenTitles: 0,
                LastCheckedCatalogLinkId: 0,
                StartedAt: DateTimeOffset.UtcNow, Error: null);

            var ct = _sweepCts.Token;
            _sweepTask = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<LinkHealthSweepService>();
                    var run = await svc.RunAsync(hosterFilter, resume, ct);
                    _sweepStatus = _sweepStatus with
                    {
                        Running = false,
                        Checked = run.Checked,
                        Alive = run.Alive,
                        Dead = run.Dead,
                        Unknown = run.Unknown,
                        HiddenTitles = run.HiddenTitles,
                        LastCheckedCatalogLinkId = run.LastCheckedCatalogLinkId,
                        Error = run.Error,
                    };
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Sweep task crashed");
                    _sweepStatus = _sweepStatus with { Running = false, Error = ex.Message };
                }
            }, ct);

            reason = null;
            return true;
        }
    }

    public void CancelBackfill() { lock (_gate) { _backfillCts?.Cancel(); } }
    public void CancelSweep() { lock (_gate) { _sweepCts?.Cancel(); } }

    public BackfillStatus BackfillSnapshot() => _backfillStatus;
    public SweepStatus SweepSnapshot() => _sweepStatus;
}

public sealed record BackfillStatus(
    bool Running,
    string? SourceId,
    string? Kind,
    DateTimeOffset? FromDate,
    int StartPage,
    int LastCompletedPage,
    int Discovered,
    int Promoted,
    int Skipped,
    DateTimeOffset? StartedAt,
    string? Error)
{
    public static BackfillStatus Idle() => new(false, null, null, null, 0, 0, 0, 0, 0, null, null);
}

public sealed record SweepStatus(
    bool Running,
    string? HosterFilter,
    int Checked,
    int Alive,
    int Dead,
    int Unknown,
    int HiddenTitles,
    int LastCheckedCatalogLinkId,
    DateTimeOffset? StartedAt,
    string? Error)
{
    public static SweepStatus Idle() => new(false, null, 0, 0, 0, 0, 0, 0, null, null);
}
