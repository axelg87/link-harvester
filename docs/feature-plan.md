# Feature Plan — Search, Following, Discovery, Telegram

Branch: `claude/app-feature-brainstorm-ZEYzu`

This document specifies four independent, sequenceable features for Link Harvester. Each phase is shippable on its own. An implementing agent should read this end-to-end, then verify the file references against the current codebase before coding.

---

## 0. Context the implementing agent needs

Link Harvester is a .NET solution that:
- Scrapes Zone Telechargement (ZT) listing + article pages via `LinkHarvester.Sources` and a `ScanPipeline` in `LinkHarvester.Core`.
- Persists a 2.3M-link catalog plus per-article release metadata via `LinkHarvester.Persistence` (`HarvesterDbContext`).
- Enriches titles via TMDB (`LinkHarvester.Enrichment` → `TmdbEnricherService`).
- Resolves dl-protect intermediate URLs to direct hoster URLs on demand via `LinkHarvester.Resolution` (`DlProtectResolver`).
- Sends downloads to a Synology DSM via `LinkHarvester.Synology` and `LinkHarvester.Core/SubmissionService`.
- Exposes APIs through `LinkHarvester.Api`, served alongside a Blazor PWA in `LinkHarvester.Web`.
- Runs scheduled scans via `LinkHarvester.Worker`.

### Key entities and services (verify paths before editing)

- `Entities.cs`
  - `TitleEntity` — release-level metadata; has `SeasonNumber`.
  - `ArticleEntity` — one ZT article; stores `AggregatorDlProtectUrl` + `HostersJson`.
  - `ResolvedLinkEntity { ArticleId, Hoster, Url, EpisodeIndex, ResolvedAt }` — persisted cache of dl-protect → direct hoster URL.
  - `SubmissionEntity { ArticleId, CatalogTitleId, DisplayTitle, Destination, UrlCount, SubmittedUrlsJson, DsmTaskIdsJson, Status, SubmittedAt, CompletedAt }` — every send attempt to DSM.
- `CatalogEntities.cs`
  - `CatalogTitleEntity` — one row per movie or per series-season aggregate. Has `CanonicalKey, TitleName, OriginalTitle, NormalizedTitle, ImdbId, TmdbId, CategoryName, TitlePoster, LinkCount, EpisodeCount, IsHidden, HiddenReason, Metadata`.
  - `CatalogEpisodeEntity { TitleId, SeasonNumber, EpisodeNumber, EpisodeName, IsFullSeason, … }`.
  - `CatalogLinkEntity { TitleId, EpisodeId?, LinkUrl, HostName, QualityName, AudioLangs, SubLangs, HealthStatus, HealthCheckedAt }`. **These are direct hoster URLs, not dl-protect.**
- `CatalogFts.cs` — FTS5 virtual table `CatalogTitlesFts` over `TitleName + OriginalTitle`, diacritic-stripped.
- `SubmissionService.cs`
  - `SendToDsmAsync` (~lines 110–253) — main entry point: dedup window check (~2 min), resolution, DSM submission, history write.
  - `ResolveArticleAsync` (~lines 49–108) — lazy resolution against dl-protect; persists `ResolvedLinkEntity` rows; subsequent calls hit cache.
- `ScanPipeline.cs` → `IngestOneAsync` (~lines 112–216) — fetches one ZT article, upserts `TitleEntity` + `ArticleEntity`, applies rule-based skip, calls `CatalogPromoter.PromoteArticleAsync`.
- `ZtTitleParser` (`LinkHarvester.Sources`) — parses release strings to season/episode.
- `AppSettingsEntity` — global single-row settings. Confirm whether it already holds hoster priority / quality preference; extend if not.

### Explicit non-goals for this plan

- **No background pre-resolution of hoster links.** Lazy on-send resolution is fine and stays as-is.
- **No multi-user, no requests, no per-user attribution.** Single-user assumption holds.
- **No in-app notifications.** Phase 2 detects missing episodes but does not raise toasts or banners; surfacing happens later via Phase 4 (Telegram).
- **No explicit "subscribe to series" UI.** Following is purely derived from history.
- **No "subscribe to discovery alerts" feature.** Discovery is a browse page only.

---

## Phase 1 — Top Search Bar (catalog + live ZT)

### Goal

A single full-width search bar at the top of every page that returns instant results from the local catalog and, in parallel, live results from ZT's own site search. Each result has an inline send button. No detail page navigation. Daily-driver UX.

### Backend

**Endpoint 1 — Catalog search**
- Route: `GET /api/catalog/search?q=<query>&limit=10`
- Auth: existing cookie auth.
- Implementation: query `CatalogTitlesFts` MATCH against the normalized query (apply the same diacritic stripping the FTS table uses on input). Join back to `CatalogTitleEntity` and aggregate "best variant" from `CatalogLinkEntity` rows (priority: quality rank desc, hoster preference, audio MULTI/VFF preference — pull from `AppSettingsEntity`).
- Response shape:
  ```jsonc
  {
    "results": [
      {
        "titleId": 12345,
        "title": "Pulp Fiction",
        "year": 1994,
        "poster": "https://...",
        "category": "Film",
        "linkCount": 12,
        "bestLink": {
          "linkId": 98765,
          "quality": "1080p REMUX",
          "size": "5.4GB",
          "audio": "MULTI",
          "host": "1fichier"
        }
      }
    ]
  }
  ```
- Performance target: p95 < 50 ms for 2.3M-row catalog (FTS5 + small join should clear this comfortably).

**Endpoint 2 — Live ZT search**
- Route: `GET /api/zt/livesearch?q=<query>&limit=5`
- Implementation: new method on the existing ZT client in `LinkHarvester.Sources`. ZT exposes a search URL (verify the exact path against current client — likely `/?do=search&story=<q>` or equivalent). Parse the result listing using the existing listing parser. Filter out results whose article URL is already present in `ArticleEntity` (i.e. already ingested).
- Response shape mirrors Endpoint 1 but each item has `articleUrl` instead of `titleId` + `linkId`:
  ```jsonc
  {
    "results": [
      {
        "articleUrl": "https://www.zone-telechargement.../article-xyz",
        "title": "Pulp Fiction",
        "year": 1994,
        "category": "Film",
        "quality": "1080p",
        "poster": "https://..."
      }
    ]
  }
  ```
- Performance target: p95 < 1.5 s (depends on ZT response time; show skeleton row in UI until it lands).
- Failure mode: on timeout or error, return empty `results` and a `degraded: true` flag. UI hides the section silently.

**Endpoint 3 — Send by catalog link**
- Route: `POST /api/catalog/send` body `{ "titleId": 12345, "linkId": 98765 }`.
- Implementation: thin wrapper that locates the `CatalogLinkEntity`, resolves the host's article id (if linked), and calls `SubmissionService.SendToDsmAsync`. If the link is a bare catalog URL (no associated ZT article), submit the URL directly to DSM using the existing Synology client. Persist a `SubmissionEntity` with `CatalogTitleId` set.

**Endpoint 4 — Add & send for live ZT result**
- Route: `POST /api/zt/add-and-send` body `{ "articleUrl": "..." }`.
- Implementation: call `ScanPipeline.IngestOneAsync(articleUrl)` → await persistence → call `SubmissionService.SendToDsmAsync` with the resulting `ArticleId`, picking the best variant per the user's hoster/quality preference. Single transactional flow; surface failures distinctly so the UI can show "Ingest failed" vs "Resolution failed" vs "DSM rejected."

### Settings additions

Extend `AppSettingsEntity` (or add new rows in whatever single-row settings table exists) with:
- `HosterPriority` — ordered list of hoster names (e.g. `["1fichier", "rapidgator", "nitroflare", "turbobit", "uploady", "dailyuploads"]`).
- `QualityPreference` — ordered list of quality tokens (e.g. `["REMUX", "BLURAY", "WEB-DL", "WEBRIP", "HDTV"]`) plus a resolution preference (`2160p`, `1080p`, `720p`).
- `AudioPreference` — one of `MULTI`, `VFF`, `VFI`, `VOSTFR`, `VO`.

Add a small Settings panel under the existing settings page to edit these. Default values match the lists above.

### UI

Component: `<TopSearchBar />` injected into `LinkHarvester.Web/Shared/MainLayout.razor` (verify exact filename).

Visual:
- Full-width sticky bar at top of viewport. Single text input, placeholder "Search catalog + ZT… (⌘K)".
- Dropdown unfurls below on focus or first keystroke. Two stacked sections:
  - **"In your catalog"** — populates after `Endpoint 1` returns (debounce 120 ms).
  - **"Live on Zone Telechargement"** — populates after `Endpoint 2` returns (debounce 400 ms), shown only when results exist.
- Each result row:
  - Left: 40 × 60 poster thumbnail (or placeholder).
  - Center: title in primary, "year · category · audio" subtitle in secondary, "best: 1080p REMUX 5.4GB · 1fichier" tertiary.
  - Right: SEND button (green for catalog) or ADD & SEND button (orange for live).
- Empty states:
  - Both empty + still searching: "Searching ZT live…"
  - Both empty + done: "No matches anywhere."
  - Catalog empty + live results pending: catalog section hidden, live skeleton shown.
- On click of SEND / ADD & SEND: optimistic toast slides up from bottom-right ("Sent to NAS — <title> <quality>") for 4 s. Dropdown stays open so user can grab a second thing. Errors replace the toast with a red one ("Send failed: <reason>") for 8 s.

Keyboard:
- ⌘K / Ctrl+K: focus the bar from anywhere. Esc: close dropdown + blur. Down/Up: move highlight. Enter: send the highlighted row (catalog only — live rows require explicit click to avoid accidental ingest).

### Acceptance criteria

- Typing "pulp" in the bar surfaces "Pulp Fiction" within 200 ms from the local catalog.
- Typing a string that exists on ZT but not in catalog surfaces it under "Live on ZT" within 2 s.
- Clicking SEND on a catalog row submits to DSM and shows a success toast within the existing dedup behaviour; no detail page navigation occurs.
- Clicking ADD & SEND on a live row ingests, then submits, within ~5 s for a typical article.
- ⌘K focuses the bar from any page.
- All existing pages still render correctly with the bar inserted into the layout.

---

## Phase 2 — Following Page (derived, no notifications)

### Goal

A page that lists every series the user is implicitly following — derived from submission history — with per-show state pulled from TMDB. Forever-window (no time decay). No notifications surface in this phase; the missing-episode signal is exposed as data only, ready for Phase 4 to consume.

### Backend

**Schema changes**

Extend `CatalogTitleEntity` with three nullable columns (or add a `TitleTmdbStateEntity` 1:1 sidecar — implementer's choice based on EF migration cost):
- `TmdbStatus` — string. One of `Returning Series`, `Ended`, `Canceled`, `In Production`, `Pilot`, `Planned`.
- `NextEpisodeAirDate` — DateTimeOffset?
- `NextEpisodeNumber` — `{ season, episode }` packed as `"S02E07"` or two int columns.
- `LastAirDate` — DateTimeOffset?

Refresh policy: `TmdbEnricherService` already runs per-title enrichment. Add a background `TmdbStatusRefreshWorker` that re-fetches these fields for every Following title weekly (and on-demand when a title first enters the Following set).

New entity:
```csharp
public class FollowingDismissalEntity
{
  public int Id { get; set; }
  public long CatalogTitleId { get; set; }
  public DateTimeOffset DismissedAt { get; set; }
}
```
Index on `CatalogTitleId`.

**Derivation query**

A Following title is any `CatalogTitleEntity` where:
- It is a series (has at least one `CatalogEpisodeEntity` row OR `CategoryName` matches a series category like "Série", "Série VF", "Anime"), AND
- At least one `SubmissionEntity` exists with `Status = Sent` and `CatalogTitleId = this.Id`, AND
- No row in `FollowingDismissalEntity` for this title.

Single SQL or LINQ query, materialised on each page request (no cache needed at this scale — likely < 200 rows).

**Missing-episode computation**

For each Following title:
1. Query TMDB (or use locally cached TMDB episode list if present) for full episode list of the show.
2. Compute the set of `(season, episode)` you have grabbed:
   - From `SubmissionEntity ⋈ ResolvedLinkEntity` via `ArticleId`. Use `ResolvedLinkEntity.EpisodeIndex` and the article's `SeasonNumber` to identify episodes you've actually sent.
3. Diff against the TMDB list → list of missing episode coords.
4. For each missing coord, look up whether the catalog has an article that contains it (`CatalogEpisodeEntity` matching season/episode → `CatalogLinkEntity`). If yes, mark "available in catalog, never sent." If no, mark "not yet ingested."

This computation is read-only and per-page. Cache the result for 1 hour keyed by titleId.

**Endpoint**
- Route: `GET /api/following`
- Response: array of items:
  ```jsonc
  {
    "titleId": 4567,
    "title": "Severance",
    "poster": "...",
    "tmdbStatus": "Returning Series",
    "lastAirDate": "2026-04-12",
    "nextEpisodeAirDate": null,
    "lastGrabbedAt": "2026-04-10T20:14:00Z",
    "grabbedEpisodes": ["S01E01", "...", "S02E05"],
    "missing": [
      { "ep": "S02E06", "availableInCatalog": true, "catalogLinkId": 99887 },
      { "ep": "S02E07", "availableInCatalog": false }
    ]
  }
  ```
- Default sort: shows with `missing && availableInCatalog` first, then by `lastGrabbedAt` desc.

**Detection hook (data only, no notifications)**

At the tail of `ScanPipeline.IngestOneAsync`, after `CatalogPromoter.PromoteArticleAsync`:
- If the newly ingested article maps to a Following title AND brings an episode coord not present in `SubmissionEntity` history, write a row to a new `FollowingDetectionLogEntity { TitleId, EpisodeCoord, ArticleId, DetectedAt, ConsumedAt? }`.
- `ConsumedAt` stays null until something consumes the detection (Phase 4 Telegram worker will set it after sending the notification).
- No UI surface for this entity in Phase 2. It is data plumbing for Phase 4 only.

### UI

New page `/following` linked from the existing main nav.

Layout: vertical list of cards, one per Following show. Each card:
- Left: poster.
- Title row: show name + TMDB status pill (`Returning`, `Ended`, `Canceled`, `In Production`).
- Sub-row: "you have S01E01–E10, S02E01–E05" rendered as a compact run-length string.
- State row:
  - If `missing` contains `availableInCatalog: true` items → orange chip "S02E06 available — Send" with inline send button.
  - Else if `missing` is non-empty → grey chip "S02E06 not yet on ZT".
  - Else if `tmdbStatus = Returning Series` and `nextEpisodeAirDate` set → blue chip "S03E01 airs <date>".
  - Else if `tmdbStatus = Ended` and grabbed list is complete → hide by default behind a "Show completed (N)" expander.
- Right: small `✕` "Stop following" → writes to `FollowingDismissalEntity`. Tooltip warns "Re-downloading any episode will un-dismiss."

No notification UI. No toasts on detection.

### Acceptance criteria

- Page loads in < 1 s for a user with ~50 Following shows.
- A show whose last download was 18 months ago still appears (no time decay).
- Dismissing a show removes it from the list immediately. Submitting a new episode of a dismissed show restores it.
- A newly ingested article carrying a missing episode creates a `FollowingDetectionLogEntity` row (verifiable via DB inspection); no in-app toast or banner appears.
- TMDB status changes (Returning → Ended) reflect within one weekly refresh cycle.

---

## Phase 3 — Discovery Page (catalog-gated popularity)

### Goal

A browse page showing what's popular externally AND already grabable from your catalog. Solves "what big movies am I missing." Pure intersect of external popularity signals × local catalog. No theatrical-release noise; nothing that isn't already downloadable surfaces.

### Backend

**Schema**

```csharp
public class DiscoveryEntryEntity
{
  public int Id { get; set; }
  public long CatalogTitleId { get; set; }
  public string Source { get; set; }       // "tmdb_top_rated_movie", "trakt_trending_week", "plex_most_watchlisted", "letterboxd_popular_week", etc.
  public int Rank { get; set; }            // 1 = top of source list
  public string Reason { get; set; }       // human-readable: "TMDB Top Rated (#3)"
  public DateTimeOffset FetchedAt { get; set; }
}
```
Index on `(Source, Rank)` and `CatalogTitleId`.

**Worker**

New `DiscoveryRefreshWorker` in `LinkHarvester.Worker`:
- Schedule: nightly at 03:00 local time.
- For each source, fetch top N (suggest N = 500 for TMDB, 200 for Trakt/Plex/Letterboxd):
  - **TMDB**: extend `TmdbEnricherService` (or add `TmdbDiscoveryClient`) to call:
    - `/movie/top_rated`
    - `/movie/popular`
    - `/tv/top_rated`
    - `/tv/popular`
  - **Trakt**: new `TraktClient` in `LinkHarvester.Enrichment`. Endpoints: `/movies/trending`, `/movies/watched/weekly`, `/shows/trending`, `/shows/watched/weekly`. Needs API key in user secrets.
  - **Plex Discover**: scrape or use the public `metadata.provider.plex.tv` endpoint that returns top watchlisted titles. Confirm during implementation; if blocked, drop this source for v1.
  - **Letterboxd**: no API. Scrape `letterboxd.com/films/popular/this/week/` — paginated HTML with film slug + poster. Be polite (1 req/sec, cache 24 h).
- For each external result, resolve to a `CatalogTitleEntity` via:
  1. `TmdbId` match (preferred — TMDB and Trakt give it; Letterboxd has it in metadata)
  2. `ImdbId` match (Plex gives it)
  3. Fallback: `NormalizedTitle + year` match
- Drop external results that don't match any catalog row. **The page is strictly catalog-gated** — never show "coming soon" items.
- Upsert `DiscoveryEntryEntity` rows: replace all rows for the source on each refresh.

**Endpoint**
- Route: `GET /api/discover?source=<source>&limit=50&hideAlreadyGrabbed=true`
- Response: array of:
  ```jsonc
  {
    "titleId": 12345,
    "title": "Severance",
    "year": 2022,
    "poster": "...",
    "ranks": [
      { "source": "tmdb_top_rated_tv", "rank": 7, "reason": "TMDB Top Rated TV (#7)" },
      { "source": "trakt_trending_week", "rank": 2, "reason": "Trakt Trending This Week (#2)" }
    ],
    "linkCount": 28,
    "bestLink": { "linkId": 887766, "quality": "1080p WEB-DL", "audio": "MULTI" },
    "alreadyGrabbed": false
  }
  ```
- `alreadyGrabbed` true when any `SubmissionEntity` with `Status = Sent` exists for the title.
- Items appearing in multiple source lists are deduped and surface all `ranks`.

### UI

New page `/discover` linked from main nav.

Layout: grid of poster cards, ~6 columns desktop / 3 mobile.
- Card content: poster, title, year, small badges per source ("TMDB #7", "Trakt #2"), bottom-strip showing best variant ("1080p MULTI · 1fichier").
- Hover/tap: SEND button appears overlaid on poster.
- Top filter bar: toggle chips per source (TMDB Top Rated, TMDB Popular, Trakt Trending, etc.), one toggle "Hide already grabbed" (default on), one toggle "Movies / TV / Both".
- Default view: union of all sources, deduped, sorted by best (lowest) rank across sources.

### Acceptance criteria

- Page loads < 500 ms with all sources active.
- Every visible card has a real, non-empty `bestLink` (i.e. catalog-gated rule is enforced).
- A user can hide already-grabbed items with one click.
- Sources that fail to refresh do not break the page — their badges simply don't appear.
- Worker run time per night < 5 minutes for all sources.
- One-click SEND works the same as on the search bar.

---

## Phase 4 — Telegram Bot (search + send, plus Phase 2 push)

### Goal

A Telegram bot the user can DM from anywhere to search and send. Minimum surface: `/find` and `/recent`. Once Phase 2 is shipped, the bot also pushes "new episode available" notifications based on `FollowingDetectionLogEntity`.

### Setup (one-time)

1. User creates a bot via @BotFather in Telegram. Bot token goes into user secrets / fly secrets as `Telegram__BotToken`.
2. User gets their own Telegram chat ID (one-shot via `/start` to the bot, server logs it). Stored in `AppSettingsEntity.TelegramOwnerChatId`. Only this chat is authorized.

### Implementation

New project: `src/LinkHarvester.Telegram/LinkHarvester.Telegram.csproj`, referenced by `LinkHarvester.Worker` and `LinkHarvester.Api`.
- Use the `Telegram.Bot` NuGet package.
- Mode: long polling (simpler than webhooks; works fine for a single-user bot on Fly).
- Hosted service `TelegramBotWorker` registered in the worker host.
- Authorization filter: drop any update from a chat ID other than `TelegramOwnerChatId`.

### Commands

**`/find <query>`** or just `<query>` as plain text in DM:
- Calls internal search service used by `Endpoint 1` (Phase 1) — extract to a `CatalogSearchService` so both API and bot share it.
- Returns the top 3 catalog results as a single message:
  ```
  🎬 Pulp Fiction (1994) — 12 links
  📺 Severance S02 — 5 episodes
  🎬 Pulp Fiction Director's Cut (2008) — 3 links
  ```
- Each result has an inline keyboard button: `📥 Send best`. Tap → calls the same `POST /api/catalog/send` path internally → edits the message to `✅ Sent — <title> <quality>`. On failure, edits to `❌ Send failed — <reason>`.

**`/recent`** (optional in v1):
- Returns last 10 ingested articles from `ArticleEntity` ordered by `CreatedAt`, with inline `📥 Send` buttons that submit the best variant of the article.

**`/start`**:
- Replies with the chat ID and authorization status. First-time setup helper.

### Phase 2 push integration

A second hosted service `FollowingNotificationDispatcher`:
- Polls `FollowingDetectionLogEntity WHERE ConsumedAt IS NULL` every 60 s.
- For each row, sends a Telegram message to the owner chat:
  ```
  📺 New episode available
  Severance — S02E06
  1080p WEB-DL · MULTI · 2.1GB
  ```
  with inline keyboard `📥 Send to NAS` / `🚫 Skip`. Tap Send → submits and marks `ConsumedAt`. Tap Skip → marks `ConsumedAt` without sending.

### Acceptance criteria

- Sending `/find pulp` to the bot returns 3 results in < 2 s and tapping a button submits to DSM.
- Messages from any unauthorized chat are silently dropped (logged at debug level only).
- A new Following detection produces a Telegram message within 90 s of `ScanPipeline.IngestOneAsync` completing.
- Bot survives process restarts (long polling reconnects automatically).

---

## Sequencing and dependencies

```
Phase 1 ─────────────────►  Phase 4 (uses CatalogSearchService extracted in P1)
   │
   └─► Phase 2 ────────────►  Phase 4 push (consumes FollowingDetectionLogEntity)

Phase 3 is fully independent and can ship in parallel with any other phase.
```

Recommended order: **P1 → P2 → P3 → P4**. P3 can slot in between P1 and P2 if external API access blocks for some reason.

## Cross-cutting concerns

- **Tests**: each phase adds an integration test fixture in `tests/`. P1 needs FTS search tests and a fake ZT search response. P2 needs a TMDB stub and a fixture covering "show with mixed grabbed/missing episodes." P3 needs source stubs returning canned popularity lists. P4 needs a Telegram update fixture (Telegram.Bot has a test harness).
- **Migrations**: every schema change in this plan needs an EF migration. Confirm `dotnet ef migrations add <Name>` produces a clean diff before committing.
- **Settings backwards-compat**: `AppSettingsEntity` extensions must default sensibly so existing deployments keep working without manual intervention.
- **No new background work on the hot ingest path.** `ScanPipeline.IngestOneAsync` only writes a `FollowingDetectionLogEntity` row when it cheaply detects a Following match — no TMDB calls, no resolution, no network outside the existing flow.

## Open items the implementing agent should decide and document

- Exact ZT search URL syntax (verify by inspecting the existing client and ZT's site).
- Whether to extend `CatalogTitleEntity` directly with TMDB status columns or create a sidecar entity.
- Whether the Plex Discover source is feasible without a token; if not, drop from v1.
- Letterboxd scraping robustness — single page or paginate? 24 h cache TTL acceptable?
- Whether `CatalogSearchService` should live in `LinkHarvester.Core` (most likely) or `LinkHarvester.Persistence`.
