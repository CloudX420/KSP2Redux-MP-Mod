<div align="center">

# 🚀 KSP2 Multiplayer Redux

**Real-time multiplayer for Kerbal Space Program 2 (Redux)**

One shared universe. Fly your own rockets. Watch your friends' orbits live.

[![Release](https://img.shields.io/github/v/release/CloudX420/KSP2Redux-MP-Mod?include_prereleases&style=flat-square&color=orange)](https://github.com/CloudX420/KSP2Redux-MP-Mod/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
[![Status: Alpha](https://img.shields.io/badge/status-early%20alpha-red?style=flat-square)](#-status)
[![Issues](https://img.shields.io/github/issues/CloudX420/KSP2Redux-MP-Mod?style=flat-square)](https://github.com/CloudX420/KSP2Redux-MP-Mod/issues)

[**Install**](#-install) · [**Play**](#-play) · [**Host a server**](#-host-a-server) · [**Docs**](#-documentation) · [**Report a bug**](#-reporting-bugs)

</div>

---

## ✨ What it does

Inspired by **LunaMultiplayer** and **DarkMultiPlayer** for KSP1, this mod turns a KSP2 Redux
campaign into a persistent shared universe hosted by a lightweight dedicated server.

| | Feature | Details |
|:--:|---|---|
| 🌍 | **Shared universe** | Everyone plays in one campaign. Launch, land, and leave craft that everyone else can see. |
| 🏠 | **Join at the Space Center** | New players spawn at the KSC with no active vessel — nobody gets dropped into someone else's cockpit. |
| 🔒 | **Your vessel is yours** | Server-granted control locks mean only the pilot can fly a craft. No hijacking. |
| 📡 | **Real-time replication** | Vessel state streams at **20 Hz** (50 Hz under thrust) and is smoothly interpolated. Orbits sync by Keplerian elements, so map-view orbit lines are exact. |
| 🛰️ | **Persistent vessels** | The server remembers every launched craft. Log off and your station stays in orbit; late joiners see everything. |
| ⏱️ | **Shared clock** | A server-authoritative universe time keeps every client's orbits in phase. |
| 🔬 | **Shared progress** | Tech-tree unlocks and science reports sync to every player. |
| ⏩ | **Warp voting** | Speeding up time needs everyone's approval. Dropping to 1× is always instant. |
| 👥 | **Player list & nameplates** | See who's online, their ping, and floating names over their ships. |

---

## ⚠️ Status

> **Early alpha — actively being tested.** The full stack is implemented, but expect rough edges
> and please read [Known limitations](#-known-limitations). Bug reports with a `Player.log`
> attached are the single most valuable contribution right now.

---

## 📦 Requirements

- **Kerbal Space Program 2** with the **Redux** community patch and **SpaceWarp 2**
- One player hosts the **dedicated server** — a tiny console app needing only the
  [.NET 6 runtime](https://dotnet.microsoft.com/download/dotnet/6.0). It can run on the same PC you play on.
- Everyone on the **same mod version** — the connection is refused on a version mismatch.

---

## 📥 Install

1. Grab `KSP2MultiplayerRedux_Release.zip` from the [**Releases**](https://github.com/CloudX420/KSP2Redux-MP-Mod/releases) page.
2. Extract it so you have:
   ```
   <KSP2 install>\mods\KSP2MultiplayerRedux\
       ├─ KSP2MultiplayerRedux.dll
       ├─ swinfo.json
       └─ assets\
   ```
3. Updating? **Delete the old `KSP2MultiplayerRedux` folder first.**

---

## 🎮 Play

1. The host starts the server and shares their IP ([how](#-host-a-server)).
2. Launch KSP2, load **any** campaign and go to the **Space Center**.
3. Press **`Ctrl + M`** — or click the **Multiplayer** app-bar button.
4. Enter the IP, port (`7777` by default) and a display name → **Join**.
5. The shared universe loads and you arrive at the KSC. Build something and fly.

> 💡 The **first** player to join an empty server seeds the universe from their own campaign.

---

## 🖥️ Host a server

Download `KSP2MultiplayerServer_Release.zip` from [Releases](https://github.com/CloudX420/KSP2Redux-MP-Mod/releases), extract, and run:

```text
KSP2MultiplayerServer.exe --port 7777 --max 8
```

Forward **UDP 7777** on your router for internet play. Console commands: `list`, `vessels`,
`save-status`, `kick <id>`, `quit`. Everything else — config, persistence, Linux hosting —
is in [docs/SERVER.md](docs/SERVER.md).

---

## 📚 Documentation

| Doc | What's inside |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | How the sync systems work and the design decisions behind them |
| [PROTOCOL.md](docs/PROTOCOL.md) | Every network packet, field by field |
| [SERVER.md](docs/SERVER.md) | Hosting, configuration, console, persistence, NAT |
| [BUILDING.md](docs/BUILDING.md) | Toolchain, project layout, release process |
| [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | Log-line reference for diagnosing problems |
| [CHANGELOG.md](CHANGELOG.md) | Release history |

---

## 🚧 Known limitations

- Remote vessels are simulated **on rails** (packed). You can fly in formation and watch them,
  but physical collisions and docking with another player's craft aren't supported yet.
- Science/tech sync is live; the periodic shared save is the eventual source of truth.
- Time warp is a single shared timeline — no per-player subspaces.
- The in-game listen-server mode is less tested than the dedicated server. Use the server.

---

## 🐛 Reporting bugs

1. Reproduce it once more with the **latest release**.
2. Grab your log:
   ```
   %USERPROFILE%\AppData\LocalLow\Intercept Games\Kerbal Space Program 2\Player.log
   ```
3. Check [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) — the log lines it lists usually pinpoint the cause.
4. [Open an issue](https://github.com/CloudX420/KSP2Redux-MP-Mod/issues) with the log attached and what you expected to happen.

---

## 🛠️ Building from source

```powershell
$env:KSP2DIR = "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program 2"
cd KSP2MultiplayerRedux;  .\build_and_deploy.ps1     # mod  → deploys to your game + zips
cd ..\KSP2MultiplayerServer; .\build_server.ps1     # server → dist\ + zip
```

Full details in [docs/BUILDING.md](docs/BUILDING.md).

---

## 🤝 Contributing

Issues and pull requests are welcome. If you're changing anything on the wire, read
[PROTOCOL.md](docs/PROTOCOL.md) first and bump the protocol version in **both** `Constants.cs` files.

## 📄 License

MIT — see [LICENSE](LICENSE). Bundles [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (MIT);
see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

<div align="center">
<sub>Not affiliated with or endorsed by the publishers of Kerbal Space Program 2.</sub>
</div>
