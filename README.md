# Link Harvester

A self-hosted ASP.NET Core 8 + Blazor WebAssembly PWA that lets you browse a
2-million-link catalog and a live RSS-style feed, review each candidate from
a phone-friendly UI, and push the hoster URL to Synology DownloadStation
**only when you click "Send to NAS."** Nothing is ever submitted
automatically.

Built around three independent sources of "what's downloadable today":

- **Catalog mode** — search 100k+ titles / 2M+ links ingested from a static
  JSON dump.
- **Scanner mode** — periodic scrape of pluggable live feeds (Zone-Téléchargement
  is the first provider).
- **Both flow into the same Inbox → resolve → Send-to-NAS pipeline**, with
  per-hoster routing and DSM-side AllDebrid handling the actual unlock.

## Live deployment

| | |
|---|---|
| URL | https://link-harvester.fly.dev/ |
| Region | Mumbai (`bom`) |
| Host | Fly.io, `shared-cpu-1x` / 2 GB RAM / 3 GB persistent volume |
| Monthly cost | ~$6 – $7.50 |

Default credentials live in `appsettings.json`; **change them from
Settings → Login the first time you sign in**.

## Architecture

```
┌─────────────────────────────────── Fly.io machine (bom) ───────────────────────────────────┐
│                                                                                            │
│   ┌────────────────┐    ┌──────────────────┐    ┌────────────────────────┐                 │
│   │ Blazor WASM PWA│◄──►│ ASP.NET Core API │◄──►│ Pooled DbContext + EF  │                 │
│   │ (browser)      │    │ + cookie auth    │    │ + SQLite FTS5          │                 │
│   └────────────────┘    │ + DataProtection │    └─────────┬──────────────┘                 │
│                         └────────┬─────────┘              │                                │
│                                  │                        │                                │
│                                  │              ┌─────────▼──────────┐                     │
│                                  │              │ /data (volume)     │                     │
│                                  │              │  - linkharvester.db│                     │
│                                  │              │  - dp-keys/        │                     │
│                                  │              └────────────────────┘                     │
│                                                                                            │
│   ┌──────────────────┐  ┌──────────────────┐  ┌───────────────────┐  ┌───────────────────┐ │
│   │ ScanScheduler    │  │ CatalogIngestor  │  │ TmdbEnricher      │  │ DlProtectResolver │ │
│   │ (BackgroundSvc)  │  │ + IngestionRunner│  │ (BackgroundSvc)   │  │ (HTTP only)       │ │
│   └────────┬─────────┘  └────────┬─────────┘  └─────────┬─────────┘  └─────────┬─────────┘ │
│            │                     │                      │                      │           │
└────────────┼─────────────────────┼──────────────────────┼──────────────────────┼───────────┘
             │                     │                      │                      │
             ▼                     ▼                      ▼                      ▼
   zone-telechargement      Google Drive /            api.themoviedb.org    dl-protect.link
   (live feed)              local upload                                    (POST flow,
                                                                            no captcha)

                                  │
                                  │ Send-to-NAS click
                                  ▼
                          ┌─────────────────┐
                          │ Synology DSM 7  │     (in UAE, residential IP)
                          │ DownloadStation │
                          │  + AllDebrid    │  ← unlocks Rapidgator/1fichier/etc.
                          │    .host plugin │     using your AllDebrid apikey
                          └─────────────────┘
```

### Why this shape

- **Single SQLite file** on a persistent volume. Simple, fast, backup-friendly,
  no extra service. FTS5 handles search across 100k+ titles in <100 ms.
- **AllDebrid integration lives on the NAS, not in this app.** The official
  Synology `.host` plugin unlocks links from the NAS's residential UAE IP,
  which AllDebrid allows — datacenter IPs (Fly, Vercel, anything cloud) are
  blocked from the unlock endpoint. This app just submits raw hoster URLs;
  the NAS does the unlock.
- **dl-protect resolver is pure HttpClient + AngleSharp**, no Playwright, no
  Chromium. A `GET → POST subform=unlock` sequence with the session cookie
  returns the final hoster URL directly; the Turnstile token isn't currently
  validated server-side. CapSolver fallback is wired but unused.

## Project layout

```
LinkHarvester/
├── src/
│   ├── LinkHarvester.Core            Domain models, interfaces, scoring,
│   │                                  ISettingsService, AppSettingsSnapshot
│   ├── LinkHarvester.Persistence    EF Core, SQLite, DataProtection,
│   │                                  catalog ingestion + ingestor + FTS5
│   ├── LinkHarvester.Sources         IFeedSource implementations (ZT today)
│   ├── LinkHarvester.Resolution      dl-protect HTTP resolver
│   ├── LinkHarvester.Synology        DSM 7 DownloadStation client
│   ├── LinkHarvester.Worker          ScanScheduler + SubmissionService
│   ├── LinkHarvester.Enrichment     TMDB API client + background enricher
│   ├── LinkHarvester.Api             ASP.NET Core API + cookie auth + WASM host
│   └── LinkHarvester.Web             Blazor WASM PWA (inbox + catalog + settings)
├── tests/
│   └── LinkHarvester.Sources.Tests   23 snapshot tests for ZT parsers
├── tools/
│   └── ResolverProbe                 CLI to validate dl-protect flow live
├── Dockerfile
├── docker-compose.yml
├── fly.toml                          Mumbai region, 2 GB RAM, 3 GB volume
├── KNOWN_BUGS.md                     ← next-session triage list
└── IMPROVEMENTS.md                   ← next-session improvement queue
```

## Run locally

Requires **.NET 8 SDK**.

```bash
cd src/LinkHarvester.Api
dotnet run --urls http://127.0.0.1:5099
```

Open http://127.0.0.1:5099, sign in (`admin` / `change-me` by default).

## Deploy

```bash
flyctl deploy --remote-only --ha=false
```

`fly.toml` is preconfigured: Mumbai, single instance, 2 GB RAM, 3 GB volume
on `/data`, `auto_stop_machines = "off"`, `min_machines_running = 1`. On a
paid Fly tier the machine stays up 24/7; on the free trial it gets throttled
into 5-minute restart cycles.

## In-app Settings (everything user-editable)

| Section | Field | Notes |
|---|---|---|
| Synology | Base URL, username, password, OTP | DSM 7 web endpoint |
| Synology | Movies / series destination | DSM share path |
| Scanning | Auto-scan interval | Default 30 min |
| Scanning | Scan on startup | Default true |
| Hoster priority | Ordered list | Default `["1fichier", "Rapidgator"]` |
| Catalog | TMDB API key | v3 key from themoviedb.org/settings/api |
| Catalog | Enrichment enabled | Background, can be paused |
| Catalog | Enrichment concurrency | 1–8 workers, default 4 |
| Catalog | Import from URL / file upload | Drive URL auto-rewritten |
| Login | Username / password | DataProtection-encrypted at rest |

All passwords / API keys are encrypted at rest with ASP.NET Core
DataProtection. The key ring is persisted under `/data/dp-keys/`.

## Adding a new feed source

1. New folder under `src/LinkHarvester.Sources/<Name>/` with parsers
   (`*HomepageParser`, `*ArticleParser`).
2. Implement `IFeedSource` (`Id`, `DisplayName`, `ListNewAsync`,
   `FetchArticleAsync`, `BuildTitleKey`).
3. Register it in `SourcesServiceCollectionExtensions.AddHarvesterSources`.
4. Drop fixture HTMLs under `tests/LinkHarvester.Sources.Tests/Fixtures/<id>/`
   and add snapshot tests — the existing ZT tests are the template.

## Tests

```bash
dotnet test
```

23 tests covering quality scoring, title normalisation, and ZT homepage +
article parsing against captured HTML fixtures.

## Operational notes

- **Costs.** Always-on Fly machine + volume ≈ $6–7.50/month. The ingest and
  enricher run inside the same VM at no marginal cost. Catalog imports are
  free in/out of Fly bandwidth. TMDB API is free up to 50 req/s.
- **Backups.** Not configured. SQLite lives on a single Fly volume. See
  IMPROVEMENTS.md for the planned backup strategy.
- **Logs.** `flyctl logs --app link-harvester`. Serilog → console, JSON
  format in production.
- **SSH.** `flyctl ssh console --app link-harvester` gets you into the
  container. `/data` is the volume mount.
- **Stop to save cost.** `flyctl machine stop e827e00a6e41d8 --app link-harvester`.
  Volume + data preserved.

## Status

Catalog is fully ingested (**118,775 titles, 2,300,307 links, 393,417
episodes**). TMDB enrichment is running in the background and progressively
unlocks year / genre / rating filters in the UI.

For known issues and the agreed next-session work, see
**[KNOWN_BUGS.md](./KNOWN_BUGS.md)** and **[IMPROVEMENTS.md](./IMPROVEMENTS.md)**.
