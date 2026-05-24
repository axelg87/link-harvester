# Known Bugs

Two real defects observed in production on 2026-05-24. Both are agreed for
the next session.

Severity legend:
- 🟥 **P0** — silently corrupts state or requires manual machine restart to recover.
- 🟧 **P1** — visible to user, has a workaround.
- 🟨 **P2** — cosmetic, no data impact.

---

## 🟥 BUG-1 — TMDB enricher does not wake up from a settings pause

### Symptom

After flipping `tmdbEnrichmentEnabled` from `true` → `false` → `true` via
`PUT /api/settings`, the `TmdbEnricherService` reports `state=running` but
performs no actual TMDB requests. The `enriched` counter stays flat
indefinitely. The only way to recover is `flyctl machine restart`.

### Reproduction

1. With `tmdbEnrichmentEnabled = true` and an API key set, let the enricher
   load a batch.
2. While that batch is in flight, set `tmdbEnrichmentEnabled = false`.
3. Wait 30 s.
4. Set `tmdbEnrichmentEnabled = true` again.
5. Observe: `GET /api/catalog/stats` returns `enrichmentRunState: "running"`
   but `enriched` does not increment. `flyctl logs` shows zero
   `api.themoviedb.org` requests after the resume.

### Root cause (suspected)

`src/LinkHarvester.Enrichment/TmdbEnricherService.cs`, `ExecuteAsync` loop.

The loop creates a bounded `Channel<EnrichWorkItem>` and N worker tasks per
batch. When the settings flip to `false`, the next loop iteration takes the
`Task.Delay(TimeSpan.FromSeconds(15))` branch — but the already-in-flight
batch's writer is still running and the workers stay blocked on
`channel.Reader.WaitToReadAsync` or in `bucket.WaitAsync`. The dispose
chain never fires for those workers; on re-enable, a fresh batch + channel
is created but the stale workers from the previous round consume from the
old (now-completed) reader and just exit without doing anything visible.
The new batch's workers may also race on a shared static token bucket if
that state survived.

### Suggested fix

In `TmdbEnricherService`:

- Hold a per-batch `CancellationTokenSource` and dispose it explicitly at
  the bottom of each loop iteration (including the disabled-branch) so any
  blocked workers can unblock.
- When entering the disabled branch, cancel the per-batch CTS so the
  workers from the previous round exit immediately rather than lingering.
- Re-create the `TokenBucket` per batch (or hold it as a field with proper
  reset semantics) so the burst capacity is fresh on resume.
- Add an INFO log on every batch load: `"enricher batch loaded: {Count}
  items"`. Currently the only log line is the warning on individual
  failures, which makes "stuck" indistinguishable from "no work."

### Acceptance criteria

- Manual repro above no longer reproduces.
- A new integration test that toggles `tmdbEnrichmentEnabled` mid-batch and
  asserts the next batch loads and processes within 30 s of the re-enable.

---

## 🟥 BUG-2 — Concurrent catalog ingest + TMDB enricher → false "failed" titles

### Symptom

When the catalog ingestor and the TMDB enricher run concurrently against
the same SQLite database, the enricher's per-title `UPDATE
CatalogTitleMetadata SET ...` statements intermittently fail with
`SQLite Error 5: 'database is locked'` after a 30-second command timeout.

The ingestor wins the lock because its transactions are larger and longer.

Each lock-induced failure increments the title's `Attempts` counter in
`CatalogTitleMetadata`. After 5 such failures the title is marked
permanently `failed` (with `LastError` containing "database is locked" or
"timeout"), and the enricher will never retry it until a code change or DB
mutation.

In the 2026-05-24 production run this falsely tagged 71 of 118 775 titles
(~0.06%) as `failed`. Small percentage but the behaviour is wrong in
principle — a transient infra error should not exhaust attempts.

### Reproduction

1. Have a catalog with >100 k titles and TMDB enrichment incomplete.
2. Trigger a fresh `POST /api/catalog/import/from-url` against any large
   JSON.
3. Set `tmdbEnrichmentEnabled = true` while the import is running.
4. Watch `enrichmentFailed` climb steadily for the duration of the import.
5. Inspect `LastError` on the affected `CatalogTitleMetadata` rows — they
   will all match `%database is locked%` or `%timeout%`.

### Root cause

SQLite's single-writer model. The catalog ingestor's batched transactions
hold a write lock for ~10-30 s at a time (25 000 inserts per commit).
The enricher's per-title `UPDATE` calls time out at 30 s of contention.
Both paths use the same `HarvesterDbContext` pooled factory, so they
serialise — but the enricher's catch handler treats every exception as a
data-level failure attributable to the title, not as a contention signal.

### Suggested fix

Two parts:

**Prevent.** In `TmdbEnricherService`, before loading a batch, query
`CatalogImportRuns` for any row with `Status = "running"`. If one exists,
sleep 60 s and re-check; do not load a batch. Equivalently, add a shared
`SemaphoreSlim` in `CatalogIngestor` that the enricher must acquire (with
a short timeout) before its own writes. This eliminates the contention at
the source.

**Heal.** On app startup (in `Program.cs`, right after `LoadAsync`),
auto-reset any `CatalogTitleMetadata` rows whose `LastError` matches
`%database is locked%` OR `%timeout%`:

```sql
UPDATE CatalogTitleMetadata
SET EnrichmentSource = 'pending',
    Attempts = 0,
    LastError = NULL
WHERE LastError LIKE '%database is locked%'
   OR LastError LIKE '%timeout%';
```

This is safe and cheap; it lets the enricher try again on those titles.

Optionally also expose a Settings → Catalog → "Retry failed enrichments"
button (see IMPROVEMENTS.md item #8).

### Acceptance criteria

- A run that includes a concurrent ingest + enrichment produces zero
  rows with `LastError LIKE '%database is locked%'`.
- Existing such rows are auto-reset on the next deploy.

---

## Outside scope of this file

Anything that isn't a defect — see [IMPROVEMENTS.md](./IMPROVEMENTS.md).
