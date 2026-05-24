using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinkHarvester.Persistence.Catalog;

/// <summary>
/// Holds an in-memory snapshot of a running ingestion so the UI can poll
/// progress without hitting the DB. Only one ingestion runs at a time.
/// </summary>
public sealed class CatalogIngestionRunner
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CatalogIngestionRunner> _log;
    private readonly object _gate = new();
    private Task? _current;
    private CancellationTokenSource? _cts;
    private IngestProgress _progress = new(0, 0, 0, 0, 0);
    private string _state = "idle";
    private string? _description;
    private string? _error;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;

    public CatalogIngestionRunner(IServiceProvider services, ILogger<CatalogIngestionRunner> log)
    {
        _services = services;
        _log = log;
    }

    public IngestionStatus Snapshot()
    {
        lock (_gate)
            return new IngestionStatus(_state, _description, _progress, _error, _startedAt, _finishedAt);
    }

    public bool TryStart(Func<CancellationToken, Task<Stream>> openStream, string description, out string? reason)
    {
        lock (_gate)
        {
            if (_current is { IsCompleted: false })
            {
                reason = "another ingestion is already running";
                return false;
            }

            _cts = new CancellationTokenSource();
            _state = "running";
            _description = description;
            _error = null;
            _startedAt = DateTimeOffset.UtcNow;
            _finishedAt = null;
            _progress = new IngestProgress(0, 0, 0, 0, 0);

            var ct = _cts.Token;
            _current = Task.Run(async () =>
            {
                try
                {
                    using var scope = _services.CreateScope();
                    var ingestor = scope.ServiceProvider.GetRequiredService<CatalogIngestor>();
                    await using var stream = await openStream(ct);
                    var progress = new Progress<IngestProgress>(p =>
                    {
                        lock (_gate) _progress = p;
                    });
                    var run = await ingestor.IngestAsync(stream, description, progress, ct);
                    lock (_gate)
                    {
                        _state = run.Status;
                        _finishedAt = run.FinishedAt;
                        _progress = new IngestProgress(run.TotalRecords, run.InsertedTitles, run.InsertedEpisodes,
                            run.InsertedLinks, run.FailedRecords);
                        if (run.Status != "succeeded") _error = run.Notes;
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_gate) { _state = "cancelled"; _finishedAt = DateTimeOffset.UtcNow; }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "ingestion crashed");
                    lock (_gate) { _state = "failed"; _error = ex.Message; _finishedAt = DateTimeOffset.UtcNow; }
                }
            }, ct);

            reason = null;
            return true;
        }
    }

    public void Cancel()
    {
        lock (_gate) _cts?.Cancel();
    }
}

public sealed record IngestionStatus(
    string State,
    string? Description,
    IngestProgress Progress,
    string? Error,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);
