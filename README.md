# KSP2 Multiplayer Redux

Real-time multiplayer for **Kerbal Space Program 2 (Redux)**. A shared universe where every
player flies their own vessels, sees everyone else's craft and orbits live, and shares science,
tech-tree progress and time warp. Inspired by LunaMultiplayer / DarkMultiPlayer for KSP1.

> **Status: early alpha.** The full stack (join flow, vessel replication, control locks, time
> sync, science sync, warp voting) is implemented, but real-world testing is still in progress.
> Expect rough edges. Bug reports with a `Player.log` attached are the most useful thing you
> can contribute — see [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).

## What it does

| Feature | Behaviour |
|---|---|
| **Shared universe** | Everyone plays in one campaign hosted by a dedicated server. |
| **Join at the Space Center** | Joining players land at the KSC with no active vessel — you never get dropped into someone else's cockpit. |
| **Own your vessel** | Server-granted control locks: only the pilot of a craft can fly it. Nobody can hijack yours. |
| **Real-time replication** | Vessel state is broadcast at 20 Hz (50 Hz under thrust) and smoothly interpolated on other clients. Orbiting craft sync by Keplerian elements so map-view orbit lines are exact. |
| **Vessels persist** | The server keeps every launched vessel; they stay in orbit when their pilot logs off and are sent to late joiners. |
| **Shared clock** | A server-authoritative universe time keeps every client's orbits in phase. |
| **Science & tech** | Tech-tree unlocks and submitted research reports sync to all players. |
| **Warp voting** | Increasing time warp requires every connected player to approve. Dropping to 1× is always instant. |
| **Player list & chat** | In-game player roster with ping; nameplates over remote vessels. |

## Requirements

- Kerbal Space Program 2 with the **Redux** community patch and **SpaceWarp 2** installed.
- One person runs the **dedicated server** (`.NET 6 runtime`; Windows or Linux). It is tiny and can
  run on the same PC as a client.
- All players must run the **same mod version** — the connection is refused on protocol mismatch.

## Install (players)

1. Download `KSP2MultiplayerRedux_Release.zip` from the Releases page.
2. Extract it so you end up with `<KSP2 install>\mods\KSP2MultiplayerRedux\` containing
   `KSP2MultiplayerRedux.dll` and `swinfo.json`.
3. If you are updating, **delete the old `KSP2MultiplayerRedux` folder first**.

## Play

1. Have the host start the server (see [docs/SERVER.md](docs/SERVER.md)) and share their IP.
2. Launch KSP2, start or load **any** campaign, and go to the Space Center.
3. Press **Ctrl + M** (or click the *Multiplayer* app-bar button) to open the multiplayer window.
4. Enter the server IP, port (`7777` by default) and a display name, then **Join**.
5. The shared universe loads and you arrive at the KSC. Build, launch, fly.

The first player to join an empty server seeds the universe from their campaign.

## Host a server

See [docs/SERVER.md](docs/SERVER.md). Short version:

```text
KSP2MultiplayerServer.exe --port 7777 --max 8
```

## Build from source

See [docs/BUILDING.md](docs/BUILDING.md).

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — how the sync systems work and why.
- [docs/PROTOCOL.md](docs/PROTOCOL.md) — packet reference.
- [docs/SERVER.md](docs/SERVER.md) — hosting, config, console commands, persistence.
- [docs/BUILDING.md](docs/BUILDING.md) — toolchain, project layout, release process.
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) — log markers and known issues.
- [CHANGELOG.md](CHANGELOG.md)

## Known limitations

- Remote vessels are simulated **on rails** (packed). You can see and fly alongside them, but
  physical collision/docking with another player's craft is not yet supported.
- Science and tech sync is live; the periodic shared save is the eventual source of truth.
- Time warp is a single shared timeline (no per-player subspaces).

## License

MIT — see [LICENSE](LICENSE). Bundles [LiteNetLib](https://github.com/RevenantX/LiteNetLib)
(MIT); see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
