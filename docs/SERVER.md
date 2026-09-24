# Dedicated server

The server is a small standalone .NET console app. It does **not** run the game — it holds the
authoritative universe (save, vessels, clock, locks) and relays state between clients.

## Requirements

- .NET 6 runtime (Windows or Linux). Download: <https://dotnet.microsoft.com/download/dotnet/6.0>
- UDP port open/forwarded (default **7777**) if players connect over the internet.

## Run

```text
KSP2MultiplayerServer.exe [--port 7777] [--max 8]
```

| Flag | Default | Meaning |
|---|---|---|
| `-p`, `--port` | 7777 | UDP port to listen on |
| `-m`, `--max` | 8 | Maximum simultaneous players |

On Linux: `dotnet KSP2MultiplayerServer.dll --port 7777`.

## First run / seeding the universe

An empty server has no universe. The **first player to join seeds it**: the server asks that
client to upload their current campaign, caches it, and from then on every joiner receives it.
Start that first client from the campaign you want to share.

## Console commands

| Command | Effect |
|---|---|
| `list` / `players` | Connected players with ping |
| `vessels` | Every vessel in the server store, with lock holder |
| `save-status` | Cached save size/time, universe time, warp index, lock count |
| `kick <peerId>` | Disconnect a player |
| `quit` / `stop` | Persist state and shut down |

## Configuration — `server_config.json`

Created next to the exe on first run.

```json
{
  "AdminSteamIDs": [ "12345678901234567" ]
}
```

`AdminSteamIDs` are Steam IDs (or device IDs when Steam is unavailable) shown with an admin badge
in the player list.

## Persistence — `Saves\`

Written every 60 s and on shutdown, next to the exe:

| Path | Contents |
|---|---|
| `Saves\MultiplayerCache.json` | The shared campaign save (refreshed from a random client every 30 s) |
| `Saves\UniverseTime.txt` | Authoritative universe time |
| `Saves\Vessels\<guid>.vessel` | Serialized assembly of each launched vessel |
| `Saves\Vessels\<guid>.state` | Last known state (orbit/position) of each vessel |

Delete the `Saves\` folder while the server is stopped to start a fresh universe.

## Version compatibility

The connection key embeds a **protocol version**. Clients whose protocol differs from the server's
are rejected at handshake (they see a connection failure rather than a silent desync). Keep the mod
and server on the same release.

## Firewall / NAT

Only **UDP** traffic on the chosen port is used. Forward it on your router for internet play, or
use a VPN/LAN tool. There is no built-in server browser; share your IP directly.
