# Wire protocol

Transport: LiteNetLib over UDP. Every message starts with a single `PacketType` byte followed by
LiteNetLib `NetDataWriter` fields in the order listed. Strings are LiteNetLib length-prefixed UTF-8.

**Protocol version** is embedded in the connection key (`KSP2_MP_REDUX_proto<N>`, see
`Constants.cs` in both projects). Bump it whenever any packet layout changes; mismatched clients
are rejected at handshake. The two `PacketType` enums (mod and server) must stay identical.

Delivery: **RO** = ReliableOrdered, **Seq** = Sequenced (latest wins), **U** = Unreliable.

## Session

| ID | Packet | Dir | Delivery | Fields |
|---|---|---|---|---|
| 1 | `PlayerJoin` | C→S, S→C | RO | `int PeerId`, `string Name`, `string PlayerId` |
| 2 | `PlayerList` | S→C | RO | `int count`, then per player: `int PeerId`, `string Name`, `bool IsHost`, `int Ping`, `bool IsAdmin` |
| 3 | `PlayerLeave` | S→C | RO | `int PeerId`, `string Name` |
| 4 | `PlayerPing` | S→C | RO | `int count`, then `int PeerId`, `int PingMs` |
| 50 | `ChatMessage` | relay | RO | `string Sender`, `string Text` |

## Time warp

| ID | Packet | Dir | Delivery | Fields |
|---|---|---|---|---|
| 10 | `WarpVoteRequest` | C→S, S→C | RO | `int RequesterPeerId`, `string RequesterName`, `int TargetWarpRate` |
| 11 | `WarpVoteResponse` | C→S | RO | `int VoterPeerId`, `bool Approved` |
| 12 | `WarpVoteResult` | S→C | RO | `bool Passed`, `int TargetWarpRate` |
| 13 | `WarpApply` | S→C | RO | `int WarpRateIndex` |

Rate index 0 (1×) is applied immediately without a vote. Votes time out after 30 s (denied).

## Chunked transfers

Used for the campaign save and for vessel assemblies. `TransferId` is random per transfer so
concurrent transfers can't interleave; `PayloadType` 0 = SaveData, 1 = VesselSpawn.

| ID | Packet | Delivery | Fields |
|---|---|---|---|
| 20 | `SaveDataChunk` | RO | `int TransferId`, `int TargetPeerId`, `byte PayloadType`, `string VesselGuid`, `int ChunkIndex`, `int TotalChunks`, `int Length`, `byte[] Data` |
| 21 | `SaveDataComplete` | RO | `int TransferId`, `int TargetPeerId`, `byte PayloadType`, `string VesselGuid`, `int TotalChunks` |
| 23 | `SaveRequest` | RO | *(none)* — server asks a client to upload its save |

Chunks are 32 KiB. Receivers discard incomplete transfers rather than loading truncated data.

## Vessels

| ID | Packet | Dir | Delivery | Fields |
|---|---|---|---|---|
| 30 | `VesselTransform` | relay | U | legacy world-space transform — deprecated |
| 31 | `VesselState` | C→S→C | Seq | `string VesselGuid`, `string VesselName`, `string OwnerName`, `string ReferenceBody`, `byte Situation`, 7×`double` Keplerian (inc, ecc, SMA, LAN, argPe, meanAnomAtEpoch, epoch), 4×`double` rotation, 3×`double` position, 3×`double` velocity, 3×`double` lat/lon/altTerrain, `bool IsUnderThrust`, `double TimeStamp` |
| 32 | `VesselDestroyed` | relay | RO | `string VesselGuid`, `string OwnerName` |
| 33 | `VesselRecovered` | relay | RO | `string VesselGuid`, `string OwnerName` |

The server stores the latest raw `VesselState` per GUID (it only parses the leading GUID) and
replays it to late joiners.

## Campaign

| ID | Packet | Dir | Delivery | Fields |
|---|---|---|---|---|
| 40 | `TechUnlock` | relay | RO | `string NodeId` |
| 41 | `ScienceEarned` | relay | RO | `float Amount`, `string ExperimentID`, `string ResearchLocationID`, `byte ReportType` (1 = data, 2 = sample) |

## Time sync

| ID | Packet | Dir | Delivery | Fields |
|---|---|---|---|---|
| 60 | `TimeSync` | S→C | Seq | `double ServerUT`, `int WarpRateIndex` |
| 61 | `UTReport` | C→S | Seq | `double UT` |

## Control locks

| ID | Packet | Dir | Delivery | Fields |
|---|---|---|---|---|
| 70 | `VesselControlRequest` | C→S | RO | `string VesselGuid`, `string PlayerId` |
| 71 | `VesselControlUpdate` | S→C | RO | `string VesselGuid`, `int ControllerPeerId` (−1 = unlocked), `string ControllerPlayerId`, `string ControllerName` |
| 72 | `VesselControlRelease` | C→S | RO | `string VesselGuid`, `string PlayerId` |

## Unknown packets

The server relays any unrecognised type to all other peers with the delivery method it arrived
with, so newer client features degrade gracefully against an older server.
