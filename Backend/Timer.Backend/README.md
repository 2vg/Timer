# Timer.Backend

`Timer.Backend` owns Timer's database. It is an ASP.NET Core 10 host over the
strongly typed SQLSugar storage in `Timer.Backend.Storage`. Its HTTP API is
read-only; game servers use its MagicOnion gRPC API, which is on by default
(a read-only replica turns `WriteApi:Enabled` off). Game servers never connect
to the database themselves.

## First setup

For fresh-database initialization and game/backend configuration together,
follow the [repository setup guide](../../README.md#timer-setup).

Build a Release once (`dotnet build Backend/Timer.Backend/Timer.Backend.csproj -c Release`),
or take a release build, then make `appsettings.Production.json` from
`appsettings.example.json` without overwriting an existing file. This local
configuration is ignored by Git because it may contain the SQL password. Only
`TimerBackend:Database:Type` and `ConnectionString` are needed; everything else
defaults:

- it listens on `127.0.0.1:5081` (HTTP/1 read API) and `127.0.0.1:5082`
  (HTTP/2 gRPC for game servers), and serves writes only on 5082;
- every start creates the tables and columns a new version adds (additive;
  an up-to-date database needs no DDL rights);
- game servers register their styles' `score_factor`s, so `WriteApi:StyleFactors`
  is only needed to override them;
- run times are kept in seconds (`Time`) and in 64 Hz ticks (`surf_runs.Ticks`,
  `surf_player_best_runs.BestTicks`), which read back exactly where a MySQL FLOAT
  doesn't. Every start fills ticks in for older runs and brings them in line with
  a hand-edited `Time`, so edit `Time` and restart;
- dates are unix milliseconds, UTC (`surf_runs.Date` and the `…UnixMilliseconds`
  columns); only the internal submission and score-recalc queues keep SQL
  timestamps. Each start moves older timestamp columns over.

For an existing master SQL database, run the migration below before starting
the backend against it. A game server on the same host needs nothing in
`{CS2}/game/sharp/configs/timer.jsonc`: an empty or missing `backend:endpoint`
means `http://127.0.0.1:5082`. Otherwise:

```jsonc
"backend": {
  "endpoint": "http://10.0.0.2:5082"
}
```

The plugin's other backend settings go there too, in snake_case
(`rpc_deadline_milliseconds`, `batch_size`, ...). ModSharp's `core.json` is not
read for them.

An endpoint alone is enough with the default `WriteApi:Authentication:Mode=None`; no server ID or TLS proxy is
required. With `ApiKey` authentication add the server's key as `"api_key"` next to `endpoint`. Submission IDs are globally unique GUIDs, so
several game servers can send the same map/style/track to one backend.
For a separate game-server host, replace the loopback addresses on the gRPC
listener and plugin endpoint with addresses on your private network. Start the
backend with `dotnet run --no-build --no-restore -c Release --project Backend/Timer.Backend/Timer.Backend.csproj`.

## Update an earlier backend test bundle

This revision adds nullable `surf_players.JoinedAtUtc`, alongside the earlier
`surf_score_recalc_outbox.PendingSinceUtc` addition. Stop all writers and workers,
keep a restorable backup, configure the existing database,
and run the new binary's additive `migrate` command before restarting them:

```sh
dotnet run --no-build --no-restore -c Release --project Backend/Timer.Backend/Timer.Backend.csproj -- migrate
```

For a database already migrated by an earlier backend bundle, do not repeat the
master date conversion. The migration preserves existing Outbox rows. Until old
pending work completes, health falls back to its row's `CreatedAtUtc`, which can
conservatively overestimate its age. New pending cycles get their own timestamp.
Write and worker readiness reject a schema missing these columns.

The player migration initializes a missing `JoinedAtUtc` from the existing
`UpdatedAt` once. This preserves the best timestamp currently available; it cannot
recover a first-join date that an older version already overwrote. New profiles
persist their creation time separately, and renames/points changes retain it.

Migration also resets invalid stored map/player playtime (negative, non-finite,
or outside the integer-microsecond read contract) to zero, keeps play counts, and
logs affected row counts. The original duration cannot be recovered from corrupt
values. Normal writes reject invalid deltas and atomically reject accumulated
time/count overflow. These repairs run only through migration/schema initialization,
not on read-only replicas.

## Upgrade an existing master SQL database

This is a one-shot upgrade for installations already using the published master
MySQL/MariaDB or PostgreSQL schema. It does not import LiteDB or move historical
runs to another database. Before the additive backend migration, a dedicated
operator command converts only `surf_runs.Date` from the master SQL date-time
field to a UTC Unix-milliseconds `BIGINT`; it does not convert other date-time
columns. The master `surf_runs` table has neither `ServerId` nor `SubmissionId`.
The new `surf_run_submissions` Inbox owns the unique `SubmissionId` and stores
the resulting `RunId`. The migration targets the published master SQL schema;
earlier backend test bundles use the additive update described below.

1. Stop **all** game servers and every backend/worker that can write this
   database. Take and verify a restorable database backup. Do not restart an
   old binary after this upgrade, and do not start the new normal SQL storage
   before the dedicated date conversion: either CodeFirst mapping can otherwise
   attempt an unsafe schema change while the table is still temporal.
2. Configure `TimerBackend:Database:Type` and
   `TimerBackend:Database:ConnectionString` for the existing database, using
   a temporary migration account allowed to create/alter tables and indexes.
   Supply the connection string in an environment-specific configuration or
   environment variable, not a shell command line or committed file.
3. Restore dependencies and build the exact release before the maintenance
   window (`dotnet build Backend/Timer.Backend/Timer.Backend.csproj -c Release`). From
   the repository root, first run the dedicated date conversion. It refuses to
   start unless the explicit backup acknowledgement is supplied:

   ```sh
   dotnet run --no-build --no-restore -c Release --project Backend/Timer.Backend/Timer.Backend.csproj -- convert-run-dates --backup-confirmed
   ```

   Equivalent wrappers are `pwsh -File scripts/convert-master-run-dates.ps1 -BackupConfirmed`
   and `sh scripts/convert-master-run-dates.sh --backup-confirmed`. They accept
   no database credentials as arguments. The conversion checks the exact master
   `surf_runs` column shape, adds a nullable typed temporary `BIGINT`, backfills
   it in resumable batches, verifies every converted value against the retained
   SQL date-time value, promotes it to `Date`, recreates the affected recent-run
   index, verifies it again, and only then drops the temporary backup column.
   Master values are interpreted as UTC clock values (matching prior writes);
   sub-millisecond precision is intentionally truncated because the target unit
   is Unix milliseconds.
   If a MySQL DDL step is interrupted, leave writers stopped and rerun the same
   command; the temporary/backup columns encode its safe resume state.

4. Only after `convert-run-dates` succeeds, run the additive backend migration:

   ```sh
   dotnet run --no-build --no-restore -c Release --project Backend/Timer.Backend/Timer.Backend.csproj -- migrate
   ```

   Equivalent wrappers are `pwsh -File scripts/migrate-master-sql.ps1`
   and `sh scripts/migrate-master-sql.sh`; both propagate the command's exit
   code and never accept credentials as arguments.

   As part of this command, the two published-master score columns
   `surf_players.Points` and `surf_player_track_scores.Points` are widened from
   signed `INT` to non-null signed `BIGINT`. This is required because a valid
   tier-26 board can exceed the signed-INT range while remaining inside the
   application's `uint` score contract. The migration accepts only the exact
   published table shapes and either their original signed `INT` or already
   migrated signed `BIGINT` columns. Before and after each physical change it
   checks metadata, row count, minimum/maximum values, and an ordered typed
   `(Id, Points)` checksum; it also verifies the published player and
   track-score indexes remain present. It refuses nullable, unsigned, or any
   other unexpected score type rather than guessing how to convert it.

   Exit code 0 means schema and historical map/run row counts were checked;
   failure exits nonzero. This command does not bind an HTTP port or run a
   score worker. It can be rerun after an interrupted additive migration: an
   already widened column is revalidated, so do not resume normal writers until
   it succeeds. Inspect any failure and the backup first; MySQL DDL is not one
   atomic transaction.
5. Start the new backend with `AllowReadRepair=false`. It creates the tables
   newer than this migration. Upgrade the
   game-server binaries, delete their `sharp/modules/Timer.RequestManager`, and
   replace the `database` and `score_write` sections of each
   `sharp/configs/timer.jsonc` with `backend`. Game servers no longer need any
   database account. Keep database DDL privileges off ordinary backend accounts.

The date conversion and additive migration use only typed SQLSugar queries,
writes, and schema-maintenance APIs; neither contains hand-written SQL. The
additive command uses SQLSugar CodeFirst only for the three zero-default map
statistics and the two new Inbox/Outbox tables. It uses typed metadata plus a
typed `UpdateColumn` operation for the two existing `Points` columns, and also
performs the existing conditional replay SteamId-to-BIGINT migration and seeds
`surf_player_best_runs` per map from historical runs using the storage layer's
bounded batches and skip-unchanged logic. It is not a full score-ruleset audit
or an exact projection verifier. Test both commands on a copy of the actual
MySQL/PostgreSQL installation database before the maintenance window.

## Run locally

Copy `appsettings.example.json` to an environment-specific configuration file,
or provide the same keys through environment variables or command-line
configuration. Do not commit database credentials.

```powershell
dotnet run --project Backend/Timer.Backend/Timer.Backend.csproj
```

Without `Kestrel` settings it listens on loopback 5081/5082. Configure the
listeners below to serve game servers on other hosts, on a private interface;
the default no-key deployment depends on the operator's network isolation. Logs are written to standard
output/error for service-manager or container collection; the backend does not depend on a
writable Windows EventLog source.

The defaults are an HTTP/1 read port and a separate HTTP/2 h2c port for gRPC,
the same as configuring:

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": { "Url": "http://127.0.0.1:5081", "Protocols": "Http1" },
      "Grpc": { "Url": "http://127.0.0.1:5082", "Protocols": "Http2" }
    }
  }
}
```

An unsecured endpoint that allows both HTTP/1 and HTTP/2 cannot negotiate the
protocol; gRPC requires the explicit `Http2` setting here. The example binds
both ports to loopback; deployment and network access policy are operator-owned.

## Configuration

```json
{
  "TimerBackend": {
    "Database": {
      "Type": "postgresql",
      "ConnectionString": "Host=127.0.0.1;Database=timer;Username=timer;Password=change_me"
    }
  }
}
```

The optional settings below default to what a single backend serving game servers needs.

- `Database:Type` accepts `mysql`/`mariadb` or
  `postgresql`/`postgres`/`pgsql`.
- `Database:ConnectionString` may instead be supplied as
  `ConnectionStrings:TimerBackend`.
- Every start runs the additive SQLSugar CodeFirst schema initialization and
  index check, creating what a new version adds. SQLSugar only alters what's
  missing, so a read-only account works against an up-to-date database. The old
  `InitializeSchema` switch is ignored, with a warning.
- `AllowReadRepair` is `false` by default. When enabled, leaderboard and
  player-record reads may populate the historical `surf_player_best_runs`
  projection. This requires write credentials and is intended only as a
  temporary migration aid. Keep it disabled on ordinary replicas.
- `EnableOutboxWorker` is `false` by default, but `WriteApi:Enabled=true`
  automatically starts the score-recalculation worker on that write replica.
  Enable the switch explicitly only for a dedicated worker role. Workers require
  read/write access to the score-recalculation Outbox, best-run, score, and
  player tables. The SQL lease makes multiple enabled workers safe, but a
  dedicated worker deployment keeps API connection budgets predictable; if write
  replicas also serve workers, include both workloads in their connection budget.
- `WriteApi:Enabled` is `true` by default. It serves the game servers' gRPC
  API: `ITimerWriteServiceV1` for run submissions and `ITimerStorageServiceV1`
  for everything else they read and write. Ruleset v1 applies.
- `WriteApi:StyleFactors` is optional. Game servers register each style's
  `score_factor` from their `timer-styles.jsonc` (kept in `surf_style_factors`),
  and until one has, only style 0 at factor 1.0 is accepted. Configured factors
  replace the registered ones entirely; an omitted style is then disabled.
- `WriteApi:LocalPorts` lists the local listener ports that serve write RPCs,
  e.g. `[5082]`, which is the default with the default listeners. Kestrel routes every endpoint on every listener, so without it
  the write service would also be reachable on the read port whenever that port speaks
  HTTP/2 (for example HTTPS with the default protocols). The check uses the
  connection's local port, not the client-supplied host header; calls on other
  listeners get `Unimplemented`. When you configure your own `Kestrel:Endpoints` or `urls`, the default no longer
  applies: an enabled write API with empty `LocalPorts` refuses to start. Set it either as a JSON array or as a single
  value (`TimerBackend__WriteApi__LocalPorts=5082`), not both. It only applies to
  TCP listeners: a Unix-socket or named-pipe listener has no local port, so set
  `WriteApi:AllowAllListeners=true` instead if the gRPC proxy connects that way.
- `WriteApi:AllowedNetworks` (optional) lists the peer addresses or CIDR networks, e.g. `["10.0.0.0/8", "203.0.113.7"]`,
  that may call write RPCs; others get `PermissionDenied`. It checks the direct TCP peer, or the forwarded client
  address only for proxies listed in `Runtime:ForwardedHeaders`, so behind an unlisted gRPC proxy list the proxy. A Unix-socket peer has no address and never matches. Empty
  allows any peer.
- `WriteApi:Authentication:Mode` is `None` (default) or `ApiKey`. `None` keeps today's behaviour: anyone who reaches
  the write listener is trusted, so restrict it by firewall or `AllowedNetworks`; the host logs a warning when it is
  served on a non-loopback address with neither. `ApiKey` requires every call to send `authorization: Bearer <key>`
  (missing or unknown keys get `Unauthenticated`) and gives each game server only the scopes it needs:

  ```jsonc
  "Authentication": {
    "Mode": "ApiKey",
    "Clients": [
      { "Name": "eu1", "KeySha256": "<64 hex chars>", "Scopes": ["Game"] },
      { "Name": "main", "KeySha256": "<64 hex chars>", "Scopes": ["Game", "Admin"] }
    ]
  }
  ```

  `Game` covers submissions, profiles, stats, settings, reads and replay links. `Admin` additionally allows deleting
  runs, wiping players, removing a map's records, setting tiers or ranked status, recalculating scores and saving
  zones (the in-game admin commands); a `Game`-only server gets `PermissionDenied` for those, and when it registers
  style factors it can add missing styles but not change an existing factor. Admin calls are logged with the client
  name. Only the key's SHA-256 is configured; the game server sets the key itself as `backend.api_key` in
  `timer.jsonc`. Generate a key and its hash with:

  ```sh
  key=$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '='); echo "key: $key"; printf %s "$key" | sha256sum
  ```

  ```powershell
  $key = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_')
  "key: $key"; [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($key)))
  ```

  Keys are printable ASCII up to 256 characters. Over plain `http://` they cross the network unencrypted (the host
  warns for non-loopback h2c listeners), so serve the write listener over HTTPS when it leaves the host.
- `WriteApi:ReplayUrlPrefixes` (optional) lists the replay store URLs game servers may link, e.g.
  `["https://replays.example.com/"]`. Every game server later downloads the linked file, so a replay URL must always be
  `http`/`https`, at most 1024 characters and without credentials; with prefixes configured it must also start with
  one of them. Set it to your `replay.storage_base_url`.

With all mutation switches disabled, request handling uses typed SQLSugar reads only,
so the backend can run with a database role limited to `SELECT` on the Timer
tables.

## Backend-only score administration

Score policy is intentionally not exposed as an HTTP or gRPC API: game servers'
`!set_tier` and `timer_recalc_scores` reach the same operations over gRPC, but
always under this instance's factors: configured, or else the ones game servers
registered. Run these one-shot
commands locally on a host with the existing write-capable database
configuration; they create a short-lived storage scope,
do not bind a web port, and do not start a score worker. The normal worker picks
up the durable Outbox work after the command exits. They require the already
migrated Inbox/Outbox schema and idempotency index; unlike `migrate`, they never
bootstrap missing tables.

```sh
# Change the main-track tier for one existing map.
dotnet run --no-build --no-restore -c Release --project Backend/Timer.Backend/Timer.Backend.csproj -- set-tier surf_example 3

# Apply the currently configured WriteApi:StyleFactors to one map or every map.
dotnet run --no-build --no-restore -c Release --project Backend/Timer.Backend/Timer.Backend.csproj -- recalc-scores surf_example
dotnet run --no-build --no-restore -c Release --project Backend/Timer.Backend/Timer.Backend.csproj -- recalc-scores all
```

`set-tier` accepts tiers 1 through 255, changes only the main-track tier, and
queues every affected configured main-track board in the same map-locked SQL
transaction. Before committing, it rejects a tier whose rank-one score on an
affected board cannot fit the persisted unsigned score field. A player's total
across boards is not a reason to reject: the score worker caps a total that
would exceed the unsigned range at its maximum and logs a warning. A failed
queue write rolls the tier change back with the transaction.

`recalc-scores` applies the same per-board check to every board it requeues.
With `all`, a map that fails the check is skipped and named in the output
(exit code 1) while every other map is still requeued.

Use `recalc-scores` after changing `WriteApi:StyleFactors` (or after repairing a
score worker failure). It requeues all known score boards for its target using
the factors from the configuration used to launch the command and reactivates
compatible dead-lettered Outbox rows without waiting for a new PB/WR. Those
boards also re-total every player on them, which repairs totals that older
versions left stale; a PB/WR recalculation re-totals only the players whose
score on the board changed. A style
omitted from `StyleFactors` is deliberately left untouched and reported as
skipped; configure that style explicitly with factor `0` and rerun the command
when the desired policy is to remove its scores. The administrative commands
use the configured `WriteApi:StyleFactors`, or else the factors game servers
registered, and refuse to run with neither: the implicit style-0 factor 1.0 does
not apply to them.

## v1 routes

| Route | Purpose |
| --- | --- |
| `GET /health/live` | Process liveness; does not query SQL. |
| `GET /health/ready` | Typed SQL readiness query. Read-only roles probe `surf_maps`; write and dedicated-worker roles probe every table the write path uses. The full table/index metadata check (Inbox `SubmissionId` unique index, Outbox indexes) runs at startup and is re-verified at most every 5 minutes, by one probe at a time. With `AllowReadRepair` enabled, readiness also fails if the database principal cannot update or insert into `surf_player_best_runs` (the insert is always rolled back and is re-checked at most every 5 minutes). |
| `GET /health/worker` | Worker scan freshness, pending work age and persisted dead letters; HTTP 503 when degraded. |
| `GET /api/v1/maps` | Sorted canonical map names. |
| `GET /api/v1/maps/{mapName}` | Existing map profile; never creates a map row. |
| `GET /api/v1/maps/{mapName}/leaderboard` | Main records; optional `style`, `track`, and `limit`. |
| `GET /api/v1/maps/{mapName}/stage-leaderboard` | Stage records; optional `style`, `track`, `stage`, and `limit`. |
| `GET /api/v1/records/{runId}/checkpoints` | Checkpoint telemetry for a run. |
| `GET /api/v1/players/{steamId}/maps/{mapName}/records` | Bounded player main records; optional `limit`. |
| `GET /api/v1/players/{steamId}/maps/{mapName}/stage-records` | Bounded player stage records; optional `limit`. |
| `GET /api/v1/players/{steamId}/points-rank` | Points rank and ranked population. |
| `GET /api/v1/players/{steamId}/maps/{mapName}/stats` | Player play time and play count. |

All record lists are capped at 5,000 rows. Leaderboards emit weak `ETag`
validators and a 15-second public cache directive. Filters, ordering, and row
limits are applied in SQL; map enumeration is a single projected query. The
host enables Brotli/Gzip response compression for the framework's default MIME
types, including JSON. ASP.NET Core intentionally leaves compression over a
Kestrel-terminated HTTPS connection disabled; configure it at the trusted
reverse proxy after reviewing BREACH exposure.

The transport contract lives in `Timer.Backend.Contracts`. IDs are unsigned
decimal strings, durations are integer microseconds, and timestamps are UTC.
Record `runDate` values are numeric Unix milliseconds; no database date-time
columns are converted as part of this wire-format change.

Run submissions accept completion times from `1000-01-01T00:00:00Z` (inclusive)
to `9999-12-31T23:59:59.500Z` (exclusive), the shared storage range for Inbox and
checkpoint date-time columns. Values outside this range return `InvalidArgument`
before any SQL runs, consistently across both database providers.

## Storage and gRPC boundary

The SQLSugar store lives in `Timer.Backend.Storage`, which only the backend
references. It still exposes ModSharp types in its metadata, so the backend
carries `Sharp.Shared.dll` as a runtime dependency.

The write foundation consists of a separately packaged, numeric-keyed
MagicOnion v1 contract, a transport-neutral `TimerBackendWriteStorage` facade,
and the `surf_run_submissions` idempotency Inbox. A submission transaction owns
the Inbox reservation, run, checkpoint batch, best-run projection, and (for a
main PB/WR) score-recalculation Outbox generation. Exact retries return the
stored result; reuse of the same submission ID with another canonical
payload is rejected. Existing installations are cold-seeded per board before
attempt resolution, while the steady-state path is covered by the existing
seed cache.

Receipt timestamps are stored in whole seconds, which every supported column type
(including existing MySQL DATETIME columns) keeps exactly, so the first
acknowledgement matches exact retries and status lookups without a read-back
inside the map-locked transaction.

Administrative record wipes retain submission receipts. An exact retry returns
the original acknowledgement even if that run was later deleted; it never
recreates the wiped record. A receipt proves the original commit, not continued
existence of the run. Intentionally resubmitted runs need a new submission ID.

When `WriteApi:Enabled=true`, the host maps `ITimerWriteServiceV1` and
`ITimerStorageServiceV1`. With the default `Authentication:Mode=None` anyone who can reach the write port can submit
data, so the operator is responsible for restricting network access (firewall or `WriteApi:AllowedNetworks`), or
should switch to `ApiKey`. The backend still
owns `StyleFactor` and the accepted ruleset, never accepting client-supplied
score policy. Incoming gRPC messages are limited to 64 KiB and responses to
64 MiB (a whole map's leaderboards), and detailed framework errors remain
disabled. The sender confirms ambiguous
outcomes by replaying the original payload and submission ID: a status lookup
alone cannot prove that an existing row contains the same payload.

The operator may use direct h2c, direct HTTPS, or a gRPC-capable reverse proxy.
A database migration role must provision the Inbox and indexes before
ordinary write replicas start. A replica with `WriteApi.Enabled=true` or `EnableOutboxWorker=true` repeats
this schema check during startup and fails before exposing the write endpoint if
required tables or indexes are missing or malformed. Migration and write readiness
inspect the actual index table, key columns and uniqueness for map/player identities,
best runs, track scores and Inbox/Outbox keys, as well as the required worker indexes.
A matching index name alone is insufficient. PostgreSQL checks also reject partial,
expression, invalid, unready or deferred indexes; MySQL checks reject prefix keys.
Existing malformed indexes are reported for repair rather than silently replaced.
PostgreSQL schema checks require a single unquoted `Search Path` schema (default `public`).
The readiness probe reports the same failure until the schema is repaired. Read-only replicas
keep the lightweight map-table readiness probe and do not require the two write
tables. Maps must already exist; login uses
`EnsurePlayerProfileAsync` to create/update the player over the backend
channel, while a run transaction deliberately rejects a missing player rather than
implicitly creating one. A generic versioned `RunCommitted` event Outbox remains
a future slice. Direct Kestrel h2c and TLS are covered by opt-in loopback tests; the deployment-specific reverse proxy still needs its own smoke test.

## Request deadlines and worker monitoring

Both REST queries and write RPCs have a server-side request budget of 15 seconds.
Client disconnects and RPC deadlines also cancel the typed database commands.
A REST deadline returns HTTP 504 with `request_timeout`; a server RPC deadline
returns `DeadlineExceeded`. Each backend operation owns an independent database
context, so concurrent callers cannot share an active transaction or a cancelled
command token. Cancellation before commit rolls the transaction back. Once commit
has started its outcome is resolved independently of the request token; a lost
reply must be retried with the original payload and `SubmissionId`.

The `TimerBackend:Runtime` settings are:

| Setting | Default | Allowed seconds | Purpose |
| --- | --- | --- | --- |
| `RequestTimeoutSeconds` | 15 | 1–300 | REST query and write RPC budget. |
| `WorkerShutdownTimeoutSeconds` | 15 | 1–120 | Maximum host wait for an active worker to stop. |
| `WorkerMaxLagSeconds` | 300 | 1–86400 | Maximum unresolved work age or time since a successful scan before worker health degrades. |

Invalid values fail startup. Health endpoints use a five-second request budget.
These are cooperative cancellation budgets: provider connection establishment,
schema metadata checks, and commit/rollback can still take their own provider
timeouts. Configure connection timeouts and the service manager's shutdown grace
period to fit the deployment. Database contexts remain alive until active work
finishes, even if the host's worker wait expires. Planned cancellation attempts to
release outstanding leases without consuming retries; if SQL is unavailable,
the persisted lease expiry allows another worker to recover.

Read API protection, also under `TimerBackend:Runtime`:

| Setting | Default | Purpose |
| --- | --- | --- |
| `ReadConcurrencyLimit` | 32 | Database-backed REST reads (leaderboard cache misses, other queries, `/health/ready`, `/health/worker`) running at once. Anonymous traffic can never hold more of the connection pool that game-server writes also use. |
| `ReadQueueLimit` | 1024 | Reads waiting for a slot (a cache expiry sends every concurrent reader of that board at once); beyond it the backend answers 429 `too_many_requests`. |
| `ClientRequestsPerSecond` | 0 (off) | Per-client-IP token bucket for `/api` requests that miss the output cache. Leave it off behind a proxy until `ForwardedHeaders` is set, or every user shares the proxy's address. |
| `ClientRequestBurst` | 2 × rate | Requests a client may burst above the rate. |
| `ForwardedHeaders:KnownProxies` / `ForwardedHeaders:KnownNetworks` | none | Proxies (addresses / CIDR networks) whose `X-Forwarded-For`/`-Proto` is trusted. With none, forwarded headers are ignored. This also changes the peer address `WriteApi:AllowedNetworks` sees for calls through those proxies. |
| `InternalHealthPorts` | all listeners | Local ports that serve `/health/ready` and `/health/worker` (404 elsewhere); `/health/live` stays public. |

Output-cache hits never touch these limits. The leaderboard cache ignores `Authorization` and `Host` headers, keys
on the compression encoding the client accepts (`br`, `gzip` or none) and stores compressed bodies, so a hit is never
recompressed. Non-canonical query values (`limit=05`, `style=00`) and map names with surrounding whitespace get 400
instead of separate cache entries. Kestrel aborts responses read slower than 4 KiB/s after a 5 s grace; cap
connections with the standard `Kestrel:Limits:MaxConcurrentConnections` if needed. At startup the host warns when
the database is not on loopback and the connection string does not set `SslMode=VerifyFull`/`VerifyCA`. Slow-SQL and
SQL-error logs contain statements and parameter counts, never parameter values.

Poll `GET /health/worker` on each worker-enabled instance. It returns HTTP 503
when any persisted dead letter exists, unresolved work is older than the configured
limit, or the local worker has stopped scanning successfully. Its JSON includes
`pendingCount`, `deadLetterCount`, `oldestPendingAgeSeconds`, and
`lastSuccessfulScanUtc`. A read-only instance returns `status: "disabled"`
without querying the Outbox. A healthy enabled instance returns
`status: "healthy"`.

Pending age starts when a board becomes pending and clears only after successful
completion. New PBs, retry backoff and administrative requeues preserve this age,
so continuously failing busy boards still trigger the lag alert. The score rows,
player totals and Outbox completion commit in the same map-locked transaction.
Completion checks the generation, rather than the current lease owner; an expired
claim cannot discard a completed calculation or force waiting workers to repeat it.
Workers inspect up to 32 candidates but claim each item only when ready to execute it.
A slow batch head therefore cannot consume retries or lease lifetime for untouched
boards; each claim gets its own current timestamp.
Keep worker host clocks synchronized for lease scheduling and lag measurements.

Configure the deployment's monitor to alert on a non-200 worker response and
unexpected `disabled` status for a worker role. Keep `/health/live` as the process
liveness probe and `/health/ready` as SQL/schema readiness; a dead letter needs
inspection and requeue through the existing score-administration CLI, not an
automatic restart loop. Write-serving and dedicated-worker roles both fail
startup, before starting their worker, if their required schema or indexes are
missing.

## Replay upload consistency

The plugin uploads replay files straight to the replay store, then records
their URLs through `ITimerStorageServiceV1.SaveReplayUrlAsync`. It uses a
unique object key for every main/stage upload attempt. Failed or interrupted
retries cannot overwrite or remove an object already referenced by a successful
replay row. Existing replay URLs stay valid.

When saving the URL fails, the plugin retains the object because the metadata
transaction may already have committed. It deletes an unused upload only after
the backend definitively confirms its run is gone. Failed uploads and replaced
versions may leave unreferenced objects. Reconcile these against SQL replay URLs
with uploads stopped before deleting them; an eager delete on an uncertain SQL
result can destroy an acknowledged replay.

## Production acceptance

Create two **disposable** loopback databases with `test` in their names and
supply their connection strings as `TIMER_TEST_MYSQL` (`Server`/`Database`)
and `TIMER_TEST_POSTGRES` (`Host`/`Database`). The gate replaces these schemas
with representative master data, runs the actual conversion/migration CLI, and
then checks storage, backend transports, and plugin behavior:

```powershell
pwsh -File scripts/test-backend-acceptance.ps1 -DisposableDatabases
```

Backend source bundles omit the game plugin; pass `-BackendOnly` to run the migration, storage and backend phases. See the source bundle README for setup.

Use `-NoRestore` only when dependencies are already restored. Windows TLS tests
require a full user profile with access to the test certificate key. The gate
enables both migration suites and TLS tests and fails if any selected test is
skipped. TRX reports and counts are saved under `artifacts/backend-acceptance`.

Coverage includes real MySQL/PostgreSQL lock contention and cancellation,
independent concurrent reads, whole-submission rollback and same-ID retry,
worker lease release and recovery after restart, dead-letter health reporting,
HTTP and RPC deadlines, bounded shutdown, h2c and TLS HTTP/2 round trips, and
discarded-reply retries. This gate does not measure capacity on the deployment's
data volume or verify its reverse proxy and monitoring configuration. Rehearse
migration on a copy of that installation and observe a canary under its expected
load before moving all writers.
