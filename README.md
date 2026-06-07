# Blaster.NET

Blaster.NET is a .NET 10 port of AlliedModders Blaster for querying Valve game servers and collecting aggregated stats.

It includes:

- **Blaster.Valve**: Valve master/server query protocol support (SteamKit2 + A2S)
- **Blaster.Batch**: concurrent worker/batch processing
- **Blaster.CLI**: command-line server query tool (JSON output)
- **Blaster.AmStats**: stats collector that writes aggregates to an existing MySQL database
- **Blaster.Tests**: unit and integration tests

## Differences from the Golang version

- Ported from Go to C#,
- Switched from old master server to GMS server query with SteamKit
- Added support for more games / mods
- Updated CS:GO to its new app id and re-enabled it for querying
- Filtered out SDR "fakeip" servers from querying
- Added `valve_game_id` and `last_seen` columns to AmStats schema

## Requirements

- .NET SDK 10
- Network access to Steam/Valve query endpoints
- For `Blaster.AmStats`: reachable MySQL database with the expected schema already created

## Build and test

```bash
dotnet build
dotnet test
```

Run only integration tests:

```bash
dotnet test --filter "Category=Integration"
```

## Standalone executables

Build optimized, self-contained Linux executables (no .NET runtime required):

```bash
# AmStats with trimming and ReadyToRun optimization
dotnet publish src/Blaster.AmStats -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishReadyToRun=true \
  -p:DebugType=none -p:DebugSymbols=false

# CLI with trimming and ReadyToRun optimization
dotnet publish src/Blaster.CLI -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishReadyToRun=true \
  -p:DebugType=none -p:DebugSymbols=false
```

For Steam-backed integration tests, set credentials via environment variables first:

```bash
set BLASTER_TEST_STEAM_USERNAME=your_steam_username
set BLASTER_TEST_STEAM_PASSWORD=your_steam_password
dotnet test --filter "Category=Integration"
```

## CLI usage (`Blaster.CLI`)

Run help:

```bash
dotnet run --project src/Blaster.CLI -- --help
```

Basic query:

```bash
dotnet run --project src/Blaster.CLI -- --appids 240 --steam-username myuser --steam-password mypass
```

Multiple app IDs:

```bash
dotnet run --project src/Blaster.CLI -- --appids 240 440 730
```

Output modes:

```bash
dotnet run --project src/Blaster.CLI -- --appids 240 --format list
dotnet run --project src/Blaster.CLI -- --appids 240 --format map
dotnet run --project src/Blaster.CLI -- --appids 240 --format lines
```

Performance/behavior flags:

```bash
dotnet run --project src/Blaster.CLI -- --appids 240 --concurrency 20
dotnet run --project src/Blaster.CLI -- --appids 240 --no-info
dotnet run --project src/Blaster.CLI -- --appids 240 --no-rules
```

Using environment variables instead of CLI secrets:

```bash
set BLASTER_STEAM_USERNAME=myuser
set BLASTER_STEAM_PASSWORD=mypass
dotnet run --project src/Blaster.CLI -- --appids 240 --format list
```

### CLI options

| Option | Description |
|---|---|
| `--appids <IDS...>` | Required. One or more Valve app IDs |
| `--format <list\|map\|lines>` | Output format (default: `list`) |
| `--steam-username <U>` | Steam username (or `BLASTER_STEAM_USERNAME`) |
| `--steam-password <P>` | Steam password (or `BLASTER_STEAM_PASSWORD`) |
| `--log-level <LEVEL>` | Log level: `trace`, `debug`, `info`, `warn`, `error`, `critical` (default: `info`) |
| `--no-info` | Skip A2S_INFO queries |
| `--no-rules` | Skip A2S_RULES queries |
| `--concurrency <N>` | Max concurrent server queries (default: `20`) |
| `--help` | Show help |

## Stats collector usage (`Blaster.AmStats`)

Run help:

```bash
dotnet run --project src/Blaster.AmStats -- --help
```

Run collection:

```bash
dotnet run --project src/Blaster.AmStats -- --game hl1 --config config.yml --steam-username myuser --steam-password mypass
dotnet run --project src/Blaster.AmStats -- --game hl2 --config config.yml
```

Enable debug logging:

```bash
dotnet run --project src/Blaster.AmStats -- --game hl1 --config config.yml --log-level debug
```

### AmStats options

| Option | Description |
|---|---|
| `--game <hl1\|hl2>` | Required. Game to collect stats for |
| `--config <PATH>` | Config file path (default: `config.yml`) |
| `--steam-username <U>` | Steam username (overrides config/env) |
| `--steam-password <P>` | Steam password (overrides config/env) |
| `--log-level <LEVEL>` | Log level: `trace`, `debug`, `info`, `warn`, `error`, `critical` (default: `info`) |
| `--help` | Show help |

### Config file (`config.yml`)

Create a YAML file like:

```yaml
database:
  host: localhost
  username: root
  password: YourPassword
  dbname: blaster_stats
steam:
  username: myuser
  password: mypass
```

`--game` maps to:

- `hl1` -> game id `1`
- `hl2` -> game id `2`

Steam credentials for runtime can come from:

1. CLI args (`--steam-username`, `--steam-password`)
2. Config (`steam.username`, `steam.password`) for AmStats
3. Environment (`BLASTER_STEAM_USERNAME`, `BLASTER_STEAM_PASSWORD`)
