namespace LinkHarvester.Persistence.Cards;

/// <summary>
/// Process-wide flag that gates card-reading endpoints. Set to true once
/// the startup backfill has finished writing card rows for every existing
/// base row. Until then, the new endpoints return 503 and the WASM client
/// falls back to the legacy reader path.
/// </summary>
public sealed class CardSyncState
{
    private int _ready;
    public bool IsReady => Volatile.Read(ref _ready) == 1;
    public void MarkReady() => Volatile.Write(ref _ready, 1);
    public void MarkNotReady() => Volatile.Write(ref _ready, 0);
}
