# Link Harvester — agent notes

## Deploy access

`flyctl` is installed at `~/.fly/bin/flyctl` (also on `$PATH` as `fly`).
The deployed app is **`link-harvester`** in region **`bom`** (Mumbai), reachable at
`https://link-harvester.fly.dev`. The user's primary use is via web/phone, the
NAS is in the UAE, the user is in France.

## Auth (for hitting deployed `/api/*` endpoints)

Admin login is cookie-based (`harvester.auth`). Credentials live in
`.claude-local/credentials.env` (gitignored). The agent should source
this file and POST to `/api/auth/login` to mint a cookie, then reuse
the cookie jar for subsequent `/api/*` calls — do NOT keep prompting
the user for the password.

```bash
source .claude-local/credentials.env
curl -s -c /tmp/lh.cookies -X POST https://link-harvester.fly.dev/api/auth/login \
  -H 'content-type: application/json' \
  -d "{\"username\":\"$HARVESTER_ADMIN_USER\",\"password\":\"$HARVESTER_ADMIN_PASSWORD\"}"
# Then for protected endpoints:
curl -s -b /tmp/lh.cookies -X POST https://link-harvester.fly.dev/api/diag/cards/rebuild
```

Useful commands the agent should reach for on its own without asking:

- `flyctl status -a link-harvester` — machine state, current version
- `flyctl logs -a link-harvester` — live tail; or `flyctl logs -a link-harvester --no-tail` for the last batch
- `flyctl ssh console -a link-harvester -C "<cmd>"` — run a one-shot command inside the machine (e.g. `ls /data`, `sqlite3 /data/linkharvester.db ".tables"`)
- `flyctl releases -a link-harvester` — deploy history
- `flyctl machine status <id> -a link-harvester` — per-machine detail

The HTTP app itself is auth-cookie protected, so any `curl` against
`/api/*` from this environment will get 401 — debugging must go through
`flyctl ssh console` for in-machine SQL / file inspection rather than
direct HTTP probes.

When the user reports a deploy issue ("it's slow", "X is broken"), the
default first step is `flyctl logs -a link-harvester --no-tail | tail -100`
and `flyctl status -a link-harvester` — not "I can't reach the deployed
machine".

## Persistent data

- SQLite lives at `/data/linkharvester.db` inside the machine (Fly volume
  `harvester_data`, 3 GB).
- DataProtection key ring at `/data/dp-keys/` — needed to decrypt
  `*Encrypted` settings columns.

## Repo conventions

- Branch naming: `claude/<topic>-C9cvc` for agent-driven work.
- Tests: `dotnet test LinkHarvester.sln`. CI gate.
- Migrations: `dotnet ef migrations add <Name> -p src/LinkHarvester.Persistence -s src/LinkHarvester.Api`.
  **Never** use `--no-build` — it generates migrations against stale
  compiled DLLs and silently drops entity columns from the snapshot.
