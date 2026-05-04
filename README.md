# Link Harvester

A self-hosted ASP.NET Core 8 + Blazor WebAssembly PWA that scans pluggable
content sources, extracts the best-quality release per title, and lets you
review every candidate from a phone-friendly inbox before pushing the
hoster URL to your Synology DownloadStation.

**Nothing is ever sent to your NAS automatically.** Every send is a
deliberate user action.

## Features

- **Pluggable sources.** Adding a provider is one `IFeedSource` class.
  Zone-Téléchargement is the first provider.
- **Title-level grouping.** Variants of the same movie / season collapse
  into one inbox card with the highest-quality variant preselected.
- **"Better quality available" detection.** A 4K release that lands after
  you grabbed the 1080p re-surfaces the title as an upgrade candidate.
  Worse-than-already-submitted releases are silently superseded.
- **dl-protect resolver — HTTP only, no browser.** A clean GET-then-POST
  with the unlock form against dl-protect.link reveals the final hoster
  URL in milliseconds. No Playwright, no Chromium, no captcha service.
- **Per-hoster ordered submission.** The first hoster in your priority
  list (default: 1fichier → Rapidgator) is the only one whose links go to
  DownloadStation.
- **DSM 7 client.** `SYNO.API.Auth` v6 + `SYNO.DownloadStation2.Task` v2.
  Per-kind destination folder.
- **Mobile-first PWA.** Installable to phone home screen, dark theme,
  cookie auth.
- **All configuration in the app.** Synology URL/credentials/destinations,
  hoster priority, scan interval, login password, all live in the
  Settings screen. No env vars to set, no files to edit.

## Run locally

Requires .NET 8 SDK.

```bash
cd src/LinkHarvester.Api
dotnet run --urls http://127.0.0.1:5099
```

Open `http://127.0.0.1:5099`, sign in (default `admin` / `change-me`),
go to **Settings** to configure your NAS, change the password, and set
hoster priorities. Hit **Scan now** in the inbox to fetch.

## Run in Docker

```bash
docker compose up -d --build
```

The compose file mounts `./data` for the SQLite database and the
DataProtection key ring (so encrypted settings survive restarts). On
first start, the app uses defaults from `appsettings.json` then becomes
fully editable via the UI.

## Project layout

```
LinkHarvester/
├── src/
│   ├── LinkHarvester.Core           Domain, ISettingsService, scoring
│   ├── LinkHarvester.Persistence    EF Core + SQLite + SettingsService
│   ├── LinkHarvester.Sources        IFeedSource implementations (ZT today)
│   ├── LinkHarvester.Resolution     dl-protect HTTP resolver (+ optional CapSolver fallback)
│   ├── LinkHarvester.Synology       DSM 7 DownloadStation client
│   ├── LinkHarvester.Worker         Scan pipeline + scheduler + submission
│   ├── LinkHarvester.Api            ASP.NET Core API + cookie auth
│   └── LinkHarvester.Web            Blazor WASM PWA
└── tests/
    └── LinkHarvester.Sources.Tests  Snapshot tests for ZT parsers
```

## Settings screen → DB columns

| UI field                 | Stored in `AppSettings.*`         | Notes                              |
| ------------------------ | --------------------------------- | ---------------------------------- |
| Synology base URL        | `SynologyBaseUrl`                 | e.g. `http://nas.local:5000`       |
| Synology username        | `SynologyUsername`                |                                    |
| Synology password        | `SynologyPasswordEncrypted`       | DataProtection-encrypted at rest   |
| Synology OTP code        | `SynologyOtpCode`                 | Optional, for 2FA accounts         |
| Movies destination       | `SynologyMovieDestination`        | DSM share path                     |
| Series destination       | `SynologySeriesDestination`       | DSM share path                     |
| Auto-scan interval       | `ScanIntervalMinutes`             | Hot-reloaded                       |
| Scan on startup          | `ScanOnStartup`                   |                                    |
| Hoster priority          | `HosterPriorityCsv`               | Ordered, drag in UI                |
| App login username       | `AuthUsername`                    |                                    |
| App login password       | `AuthPasswordEncrypted`           | DataProtection-encrypted at rest   |

## Adding a new source

1. New folder under `LinkHarvester.Sources/<Name>/` with parsers
   (`*HomepageParser`, `*ArticleParser`).
2. Implement `IFeedSource`.
3. Register it in `SourcesServiceCollectionExtensions.AddHarvesterSources`.
4. Drop fixture HTMLs under
   `tests/LinkHarvester.Sources.Tests/Fixtures/<id>/` and write snapshot
   tests.

## Tests

```bash
dotnet test
```

23 tests cover quality scoring, title normalization, ZT homepage and
article parsing.
