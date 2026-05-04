# Link Harvester

A self-hosted ASP.NET Core 8 + Blazor WebAssembly PWA that scans pluggable
content sources, extracts the best-quality release per title, and lets you
review each candidate from a phone-friendly inbox before pushing the
hoster URL to your Synology DownloadStation.

**Nothing is ever sent to your NAS automatically.** Every send is a
deliberate user action.

## Features

- **Pluggable sources.** A new provider is one `IFeedSource` implementation;
  Zone-Téléchargement is shipped as the first provider.
- **Title-level grouping.** Multiple articles for the same movie / season are
  collapsed into one inbox card; the highest-quality variant is preselected.
- **"Better quality available" detection.** When a 4K release lands days
  after you grabbed the 1080p, the title pops back into the inbox flagged as
  an upgrade candidate. Worse-than-already-submitted releases are silently
  superseded.
- **Per-hoster ordered submission.** The chosen hoster is the first match in
  your priority list (default: 1fichier → Rapidgator). Only that hoster's
  links are sent to DSM.
- **dl-protect resolver.** Playwright in a persistent Chromium profile;
  CapSolver Turnstile fallback with a configurable monthly USD budget.
- **Mobile-first PWA.** Installable to home screen on iOS/Android, dark
  theme, single-user cookie auth.
- **One Docker image** runs anywhere; expose publicly with Cloudflare
  Tunnel, Tailscale, or a reverse proxy.

## Project layout

```
LinkHarvester/
├── src/
│   ├── LinkHarvester.Core           Domain models, interfaces, scoring
│   ├── LinkHarvester.Persistence    EF Core entities + SQLite migrations
│   ├── LinkHarvester.Sources        IFeedSource implementations (ZT today)
│   ├── LinkHarvester.Resolution     Playwright dl-protect resolver + CapSolver
│   ├── LinkHarvester.Synology       DSM 7 DownloadStation client
│   ├── LinkHarvester.Worker         Scan pipeline + scheduler + submission
│   ├── LinkHarvester.Api            ASP.NET Core API + cookie auth
│   └── LinkHarvester.Web            Blazor WASM PWA
└── tests/
    └── LinkHarvester.Sources.Tests  Snapshot tests for ZT parsers
```

## Run locally

Requires .NET 8 SDK.

```bash
cd src/LinkHarvester.Api
dotnet run --urls http://127.0.0.1:5099
```

Open http://127.0.0.1:5099, sign in (`admin` / `change-me` by default), hit
**Scan now**.

## Run in Docker

```bash
cp .env.example .env  # then edit
docker compose up -d --build
```

The compose file mounts `./data` for the SQLite database and the persistent
Chromium profile so cookies / fingerprint warm-up survive restarts.

## Configuration

All knobs live in `src/LinkHarvester.Api/appsettings.json` and can be
overridden by environment variables (use `__` as a separator, e.g.
`Synology__BaseUrl`).

| Section          | Key                          | Notes                                                               |
| ---------------- | ---------------------------- | ------------------------------------------------------------------- |
| `Auth`           | `Username`, `Password`       | Single user. Change before exposing publicly.                       |
| `Harvester`      | `ScanIntervalMinutes`        | Background scan period; 0 disables.                                  |
| `Harvester`      | `HosterPriority`             | Ordered list of hoster names. Default `["1fichier", "Rapidgator"]`. |
| `Resolver`       | `Headed`                     | Set to `true` to debug the Playwright browser visibly.              |
| `CapSolver`      | `ApiKey`, `Enabled`          | Optional fallback for Cloudflare Turnstile.                          |
| `CapSolver`      | `MonthlyBudgetUsd`           | Hard cap; default $20.                                               |
| `Synology`       | `BaseUrl`, `Username`, `…`   | DSM 7 endpoint and DownloadStation credentials.                     |
| `Synology`       | `DefaultMovieDestination`    | DSM share path used for movies.                                     |
| `Synology`       | `DefaultSeriesDestination`   | DSM share path used for series.                                     |

## Adding a new source

1. Create a new folder under `LinkHarvester.Sources/<Name>/` with the
   parsers (`*HomepageParser`, `*ArticleParser`).
2. Implement `IFeedSource` with `Id`, `DisplayName`, `ListNewAsync`,
   `FetchArticleAsync`, `BuildTitleKey`.
3. Register it in `SourcesServiceCollectionExtensions.AddHarvesterSources`.
4. Drop a few fixture HTMLs under
   `tests/LinkHarvester.Sources.Tests/Fixtures/<id>/` and write snapshot
   tests; the existing ZT tests are the template.

## Tests

```bash
dotnet test
```

23 tests cover quality scoring, title normalization, and ZT homepage +
article parsing against captured fixtures.
