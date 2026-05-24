# Improvements Queue

Agreed for the next session. Five items, in priority order.

The two bugs in [KNOWN_BUGS.md](./KNOWN_BUGS.md) should be tackled first
or in parallel — they share the same code area as several of these
improvements.

---

## 5. Bulk send from the catalog grid

### Why

Today, sending a title to DSM requires opening its detail modal, picking
links, and clicking **Send to NAS**. A power user browsing the catalog
wants to multi-select tiles in the result grid and send everything in
one batch.

### Scope

- Add a "select mode" toggle button to the catalog toolbar.
- In select mode, each tile gains a corner checkbox; the bottom of the
  result list grows a sticky action bar showing "*N* titles selected ·
  Send all best variants · Clear."
- "Send all best variants" pre-resolves the best hoster per selected
  title using the existing priority list, then calls
  `POST /api/catalog/links/send` with the consolidated link list.
- Error reporting per title (some may have no Rapidgator/1fichier link).

### Out of scope

- Per-link selection across multiple titles (use the existing detail
  modal for that).
- Drag-to-select / shift-click.

### Acceptance

- Selecting 10 titles and clicking Send creates 10+ DSM tasks in one
  round-trip.
- Failures are reported per title with the same friendly messages as
  improvement #10.

---

## 7. `/healthz` endpoint + Fly health check

### Why

Today Fly's proxy detects an unresponsive app only at request time. If
the .NET process deadlocks (e.g. via a future SQLite bug or a runaway
ingestor), the only signal is "users get 500s." A proper health check
restarts the machine within seconds of detection.

### Scope

- Add an unauthenticated `GET /healthz` endpoint that returns 200 if
  the app can:
  - Open the SQLite connection (`SELECT 1`)
  - Read `AppSettings` (validates DataProtection key ring loaded)
  - Return within 2 s.
- Otherwise 503 with a JSON body describing which check failed.
- Add `[[http_service.checks]]` block in `fly.toml` pointing at
  `/healthz`, 30 s interval, 5 s timeout, 3 failures → restart.

### Acceptance

- `curl https://link-harvester.fly.dev/healthz` returns 200 in <500 ms
  during normal operation.
- Simulating a SQLite outage (rename the db file via SSH, briefly)
  causes the next health check to fail and Fly to restart the machine
  automatically.

---

## 8. Retry-failed-enrichments button (and auto-cleanup of lock failures)

### Why

Closely related to BUG-2. Even after the bug is fixed, the catalog
contains rows with `EnrichmentSource = 'failed'` from past runs. There's
no UI affordance to retry them, and a user looking at "71 failed" in the
catalog stats has no recourse short of SSH + raw SQL.

### Scope

- New endpoint: `POST /api/catalog/enrichment/reset-failed` with optional
  `?onlyLockErrors=true` query flag. Behaviour:
  - Without flag: resets *all* `failed` rows to `pending`, attempts = 0,
    `LastError = NULL`.
  - With `onlyLockErrors=true`: resets only rows whose `LastError` matches
    the lock/timeout patterns from BUG-2.
- Settings → Catalog: add two buttons —
  "Retry all failed (*N*)" and "Retry transient failures only (*M*)" —
  with confirmation dialog.
- Auto-call `?onlyLockErrors=true` once on app startup, after the BUG-2
  prevention is in place.

### Acceptance

- Clicking the button immediately drops the `enrichmentFailed` counter
  and the next enricher batch picks the reset titles up.
- Startup logs confirm `"reset N transient enrichment failures"` on a
  fresh boot if any such rows exist.

---

## 9. Friendlier empty / first-run states

### Why

A brand-new user signing in for the first time sees: an empty Inbox
("No new items."), a Catalog page with 0 results, and no obvious
breadcrumb to "go to Settings to configure stuff." A returning user
whose enricher hasn't picked up TMDB yet sees a Catalog where year /
genre filters do nothing.

### Scope

Targeted empty-state cards:

- **Inbox empty + no scan ever run:**
  *"Nothing to review yet. ZT scanner runs every 30 min — try [Scan
  now] — or import a JSON dump from [Settings → Catalog]."*
- **Catalog has 0 titles:**
  *"Catalog is empty. Upload a JSON dump or paste a public URL in
  [Settings → Catalog]."*
- **Catalog has titles but 0% enriched:**
  *"⚠ TMDB enrichment hasn't started yet. Set your API key in
  [Settings → Catalog] to enable year / genre / rating filters."*
  (with a banner across the top of `/catalog` while enrichment <50%)
- **Send-to-NAS pressed without DSM configured:**
  *"Synology isn't set up. Go to [Settings → Synology] to add your DSM
  URL and credentials."* (instead of a stack trace)

### Acceptance

- Walking through a fresh deploy produces an obvious next-action at
  every step. No dead-ends.

---

## 10. Send-to-NAS error messages that name the actual DSM failure

### Why

Today, when `POST /api/articles/{id}/send` or
`POST /api/catalog/links/send` fails, the user sees a generic
"Failed: <ExceptionMessage>" toast. The underlying DSM error codes are
much more actionable:

- `105` — insufficient permissions
- `119` — session expired (we already handle this internally; should
  not surface)
- `400` — task creation failed (could mean: destination doesn't exist,
  hoster not registered with .host plugin, URL malformed)
- `403` — auth failed
- `404` — DSM endpoint not reachable

### Scope

- Extend `DownloadStationClient` to throw a typed
  `DsmException(int code, string humanMessage)`.
- Map known codes to friendly strings:
  - 105 → *"The DSM user '{username}' doesn't have DownloadStation
    permission. Check DSM → Control Panel → User → Applications."*
  - 400 (with `error.errors[].url`) → *"DSM refused the URL: {url}.
    Most likely the AllDebrid .host plugin isn't installed or this
    hoster isn't registered in it."*
  - Connection-refused / DNS errors → *"Couldn't reach DSM at
    `{baseUrl}`. Is your QuickConnect URL correct and online?"*
- `SubmissionService` and both catalog endpoints catch
  `DsmException` and surface `humanMessage` (not the technical detail)
  in the response body.

### Acceptance

- Misconfiguring each scenario above produces the matching friendly
  message in the UI, not a stack trace.

---

## Out of scope (deferred / dropped)

These came up during the 2026-05-24 planning session and are
explicitly **not** scheduled. Future sessions can promote them.

- **Force-password-change on first login** when password is `change-me`.
  (Dropped from this queue; rely on the README + first-run banner instead.)
- **Auto-resume catalog ingestion on machine restart.** Useful only on
  future re-imports; not a current pain point.
- **Resilient ingest resume via byte-position checkpoint.**
- **Hoster reachability probe via DSM.**
- **Hydracker as a live `IFeedSource`** (alongside the static JSON catalog).
- **DB and DataProtection-key backups** to remote storage.
- **FTS5 BM25 ranking tuning.**
- **Schedule-based machine stop/start** for further cost savings.
- **Notifications** (Telegram / Discord / Gotify).
- **2FA on the app login.**
- **Multi-user support.**
- **Audit log of every Send action.**
- **Integrations beyond DSM** (Transmission, qBittorrent, Sabnzbd).
