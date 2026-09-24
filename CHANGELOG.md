# Changelog

## 0.2.5 — first public alpha

Networking / build
- LiteNetLib is now merged into the mod DLL (ILRepack). Fixes networking never initialising because
  the game runtime does not load sibling DLLs from the mod folder.
- Protocol version 3. Connection key embeds the protocol version; mismatched clients are rejected.

Join flow
- Joining players land at the Kerbal Space Center with no active vessel. The received save's
  `StartingGameState`/`HistoricalGameState` and active-vessel fields are rewritten before load,
  with a `TransitionToKSC` fallback.
- First player on an empty server seeds the universe (previously rejected).

Server
- Per-vessel universe store: launched vessels and their last state persist on disk and are sent to
  late joiners; vessels survive their pilot disconnecting.
- Server-authoritative universe time (seeded from the first client, persisted, broadcast at 2 Hz).
- Per-vessel control locks keyed by player ID.
- Chunked transfers carry a transfer ID + vessel GUID; concurrent transfers no longer corrupt.
- Console commands: `list`, `vessels`, `save-status`, `kick`, `quit`.

Vessel sync
- 20 Hz / 50 Hz-under-thrust broadcast; prev/next interpolation buffer with 100 ms delay.
- Surface/atmospheric vessels sync in the rotating body frame (was inertial → tens of km off).
- Remote vessels packed (`AtRest`/`Orbital`) so local physics doesn't fight the network.
- View objects for remote vessels are instantiated within 15 km via four escalating game APIs, each
  verified by part count, with full diagnostics in the log.
- All sync suspended outside flight scenes; remote spawns deferred until in flight (fixes remote
  craft injected into the VAB and crashes on revert).

Campaign sync
- Tech-tree and research-report sync rewritten as state diffing (message-name matching never
  matched the real `TechResearchedMessage`). Echo-loop guard.

## 0.0.x — internal prototypes

Save-relay multiplayer, warp voting, initial Keplerian vessel sync.
