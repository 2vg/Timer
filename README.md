# Timer

A surf/bhop timer for CS2 on ModSharp. Requires **.NET 10**, ModSharp **2.1.159**+
and [Timer.Backend](#backend), which owns the MySQL/MariaDB or PostgreSQL database.

## Install

```sh
dotnet publish Plugin/Timer/Timer.csproj -c Release -p:Platform=x64 -p:CIBuild=true -o sharp/modules/Timer
dotnet publish Plugin/Timer.Shared/Timer.Shared.csproj -c Release -p:Platform=x64 -p:CIBuild=true -o sharp/shared/Timer.Shared
dotnet publish Plugin/Timer.Localization/Timer.Localization.csproj -c Release -p:Platform=x64 -p:CIBuild=true -o sharp/modules/Timer.Localization
dotnet publish Plugin/Timer.MapChooser/Timer.MapChooser.csproj -c Release -p:Platform=x64 -p:CIBuild=true -o sharp/modules/Timer.MapChooser
```

Merge `sharp/` into `{CS2}/game/sharp/`, keeping your configs. Deploy everything
from one build; a new Timer.Shared needs a full server restart. Localization
(needs ModSharp's LocalizerManager) and MapChooser are optional.

Release zips ship configs as `*.jsonc.example`. On a fresh install:

```sh
cd sharp/configs && for f in *.example; do [ -e "${f%.example}" ] || cp "$f" "${f%.example}"; done
```

### Client addon

Copy `panorama/` (the HUD) and `particles/` (zone outlines) into a workshop
addon, compile with `resourcecompiler -f -i <file>` or the addon tools, publish
it, and make clients download it (e.g. MultiAddonManager). If the layout isn't at
`panorama/layout/custom_game/surftimer/hud.vxml_c`, set `timer_hud_layout`.

## Backend

Copy [appsettings.example.json](Backend/Timer.Backend/appsettings.example.json)
to `appsettings.Production.json` and set `TimerBackend:Database:Type` and
`ConnectionString`. It creates its tables on startup.

```sh
dotnet run -c Release --no-launch-profile --project Backend/Timer.Backend/Timer.Backend.csproj -- --environment Production
```

It serves HTTP on `127.0.0.1:5081` (`/health/ready`) and gRPC on `127.0.0.1:5082`.
Start it before the game server. A game server on another host sets
`backend.endpoint` in `timer.jsonc`. By default gRPC has no authentication: keep it on a
private network, or enable per-server API keys (`backend.api_key`). Upgrading an existing database, and the rest:
[Backend documentation](Backend/Timer.Backend/README.md).

## Configuration

`sharp/configs/`, each documented in its comments:

| File | |
| --- | --- |
| `timer.jsonc` | Backend address, chat prefix, replay store. |
| `timer-styles.jsonc` | Styles, with bhoptimer's options (HSW, backwards, gravity, timescale...). Keep each style's `id`. |
| `timer-gamemodes.jsonc` | Surf/bhop by map prefix, cfgs, start-zone limits, airaccelerate. |
| `timer-zones.jsonc` | Zone outline colours, flat types, width. |
| `timer-ranks.jsonc` | Rank titles, chat tags, scoreboard. |
| `timer-sounds.jsonc` | Finish sounds: random picks, rank sounds, your own `.vsndevts`. |
| `timer-replay.jsonc` | Replay bots; `"type": 1` is the central bot `!replay` uses. |

Per-map overrides go in `sharp/data/surftimer/map_configs/<map>.json`: `cmds`,
`max_velocity`, and per track `enter_speed_limit`, `exit_speed_limit`,
`max_jumps`, `require_checkpoints`. Cvars start with `timer_`.

### Replay storage (optional)

Set `replay.storage_base_url` in `timer.jsonc` to an HTTP store with
PUT/GET/DELETE; `tools/ReplayStorageServer` is a reference one (port 5080).
Failed uploads are retried. Slower runs are kept per `timer_replay_slower_runs`;
`timer_replay_keep_all_runs 1` keeps every run's replay for good.

## Commands

- **Timer:** `!r`, `!b<n>`, `!s <n>`, `!end`, `!stop`, `!pause`, `!nc`, `!style`
- **Records:** `!wr [map]`, `!pb`, `!rank`, `!cpr`, `!recent`, `!profile [name|SteamID]`, `!ptop`, `!replay`
- **Practice:** `saveloc`, `loc`, `prevloc`, `nextloc`, `clearloc` (bindable)
- **HUD and misc:** `!hud`, `!showkeys`, `!ssj`, `!mapinfo`, `!spec`, `!specs`, `!hide`, `!footsteps`, `!stopsound`, `!sounds`, `!showzones`, `!country`
- **Map chooser:** `!rtv`, `!nominate`, `!nextmap`, `!timeleft`
- **Admin:** `!zone` (`timer:zone`), `!set_tier` / `!set_ranked` (`timer:tier`), `!wipeplayer` (`timer:records`)

Admin permissions come from ModSharp's AdminManager (`timer:*` grants all).

## Extending

Register these `Timer.Shared` interfaces from your own ModSharp module:

- `IPermissionProvider`: who may control another player's central replay.
- `ICountryProvider`: the country shown on profiles (none built in).
- `ILocalizationProvider`: replaces Timer.Localization.

## Localization

Timer.Localization serves each player's game language from
`locales/surftimer.json` (English, Simplified Chinese). Put your own texts in
`sharp/locales/surftimer.custom.json` (same format, wins) and run
`ms_locales_reload`. Chat texts carry their colours as tags like `[green]`
(`[red]{2}` colours that value); change or drop them freely.
