# Architecture

Two projects:

- **`KSP2MultiplayerRedux/`** — the client mod (SpaceWarp 2 plugin, `netstandard2.1`).
- **`KSP2MultiplayerServer/`** — the dedicated server (`net6.0` console app).

Both use [LiteNetLib](https://github.com/RevenantX/LiteNetLib) (reliable UDP). The server is
**authoritative** for the universe; clients are authoritative only for the vessel they hold a
control lock on. The design follows LunaMultiplayer's model: one shared universe, join at the
Space Center, per-vessel locks, on-rails replication of everyone else's craft.

## Client components

All live under `src/` and are created by `KSP2MultiplayerReduxPlugin` as `DontDestroyOnLoad`
MonoBehaviours.

| Component | Responsibility |
|---|---|
| `Networking/NetworkManager` | Owns the `ClientConnection` (and `ServerHost` in listen mode). Chunked transfers, save upload/download, join-time save scrubbing, warp-vote bookkeeping, events for everything else. |
| `Networking/ClientConnection` | LiteNetLib client; packet (de)serialization; raises typed events. |
| `Networking/ServerHost` | Optional in-process listen server (host + client in one game). The dedicated server is the recommended path. |
| `Networking/PacketTypes` | Wire format for every packet. See [PROTOCOL.md](PROTOCOL.md). |
| `Sync/VesselSyncManager` | Broadcasts our controlled vessel; buffers/interpolates remote vessels; control locks; physics packing; view-object loading. |
| `Sync/VesselSpawnManager` | Serializes newly launched vessels and injects remote vessel assemblies (deferred until in a flight scene). |
| `Sync/VesselLifecycleManager` | Destroy/recover propagation; tracks the current `GameState`. |
| `Sync/TimeSyncManager` | Slews local universe time toward the server clock. |
| `Sync/TimeWarpSyncManager` | Detects local warp changes and routes them through voting. |
| `Sync/CampaignSyncManager` | Diffs tech-tree unlocks and submitted research reports; applies remote ones. |
| `UI/*` | Multiplayer window (Ctrl+M), player list, warp-vote panel. |

## Join flow

1. Client connects (key = `KSP2_MP_REDUX_proto<N>`), sends `PlayerJoin` with display name and
   player ID (Steam ID, or a device ID fallback).
2. Server replies with the roster, then bootstraps the universe:
   1. Cached campaign save (chunked). If other players are online it first asks one of them for a
      fresh save (just-in-time), falling back to the cache after 5 s.
   2. Every stored vessel assembly.
   3. Each vessel's last known state.
   4. Current control locks.
3. The client **scrubs the save before loading it** (`NetworkManager.ScrubSaveForJoin`): the save's
   `StartingGameState`/`HistoricalGameState` are forced to `KerbalSpaceCenter` and the active-vessel
   fields cleared, so the joiner lands at the KSC instead of inside the uploader's flight. A fallback
   calls the game's own `TransitionToKSC` if it still ends up in a flight scene.

## Vessel replication

**Sending.** Every frame `VesselSyncManager` looks at the active vessel. It requests a control lock
from the server for it and broadcasts `VesselState` at 20 Hz (50 Hz while thrusting) as long as no
*other* player holds the lock. The packet carries situation, Keplerian elements, body-relative
position/rotation, lat/lon/alt and a timestamp.

**Coordinate frames (important).** Surface, pre-launch and atmospheric vessels are synced in the
body's **rotating** frame (`transform.bodyFrame`) so a parked craft has constant coordinates
regardless of planet rotation or clock skew. Orbiting/escaping vessels use Keplerian elements for
position and the inertial frame for attitude. Using the inertial frame for surface vessels placed
them tens of km away — don't.

**Receiving.** States are keyed by vessel GUID (the server preserves GUIDs across injection; a
mapping table handles the case where the game reassigns one). Each remote vessel keeps a
prev/next buffer and is rendered ~100 ms in the past with position lerp + rotation slerp; orbital
elements are pushed to the solver once per packet. Packets for vessels that haven't been injected
yet are queued and retried.

**Physics.** Remote vessels are put in a packed physics mode (`AtRest` on the surface, `Orbital`
otherwise) so local physics never fights the network. Consequence: no collisions with remote craft.

**Rendering.** Injected vessels exist only as simulation models; the game does not build their 3D
view automatically. Within 15 km of the active vessel the mod instantiates the view itself, trying
four game APIs in order (the game's proximity loader, `UniverseView.InstantiateViewObjectAsync`,
`SimulationObjectModel.InstantiateViewObjectAsync`, synchronous instantiate) and **verifying** the
result has parts before accepting it. Views are destroyed again beyond 22.5 km. Every step logs a
`[VesselSync]` line so failures are diagnosable from `Player.log`.

## Control locks

`VesselControlRequest` → server grants if the vessel is unlocked or already held by the same player
ID → broadcasts `VesselControlUpdate` to everyone. Locks are released on vessel switch, disconnect,
or explicit release. Clients ignore incoming state for vessels they control and never request a
vessel someone else holds. Identity is the **player ID**, not the display name.

## Time sync

The dedicated server owns a universe clock: seeded from the first client's `UTReport` (or restored
from disk), advanced by wall-clock × the current warp multiplier, broadcast at 2 Hz as `TimeSync`.
Clients compensate for half-RTT, hard-set on drift ≥ 5 s and slew smaller drift. A listen-server
host broadcasts its own game clock instead.

## Science / tech sync

Message-name matching proved unreliable, so `CampaignSyncManager` polls once per second and
**diffs state**: every tech node ID against `ScienceManager.IsNodeUnlocked`, and the list from
`GetSubmittedResearchReports()` by report key. New items are broadcast; remote items are recorded
in the known-set *before* being applied so they don't echo back. The first poll seeds a silent
baseline.

## Scene gating

All vessel injection, physics and view work is suspended unless the game is in a flight-capable
state (`FlightView`, `Map3DView`, `TrackingStation`, `Launchpad`, `Runway`). This keeps remote
craft out of the VAB and avoids touching vessels mid-revert. Incoming spawn payloads are queued
until a flight scene is entered.

## Dependency bundling

SpaceWarp 2 loads only the manifest's `main_assembly`; the game's runtime does not probe the mod
folder for other DLLs. `LiteNetLib.dll` is therefore **merged into `KSP2MultiplayerRedux.dll`** by
ILRepack on Release builds (`ILRepack.targets`). Ship a single DLL.
