using LiteNetLib.Utils;

namespace KSP2MultiplayerRedux.Networking
{
    /// <summary>
    /// Packet type IDs for the multiplayer protocol.
    /// First byte of every message identifies the type.
    /// </summary>
    public enum PacketType : byte
    {
        PlayerJoin = 1,
        PlayerList = 2,
        PlayerLeave = 3,
        PlayerPing = 4,
        WarpVoteRequest = 10,
        WarpVoteResponse = 11,
        WarpVoteResult = 12,
        WarpApply = 13,
        SaveDataChunk = 20,
        SaveDataComplete = 21,
        SaveRequest = 23,      // Server asks host to upload save
        VesselTransform = 30,  // Legacy: raw world-space transform (deprecated)
        VesselState = 31,      // New: orbital element sync
        VesselDestroyed = 32,
        VesselRecovered = 33,
        TechUnlock = 40,
        ScienceEarned = 41,
        ChatMessage = 50,
        TimeSync = 60,          // Server -> clients: authoritative universe time
        UTReport = 61,          // Client -> server: seed/report universe time
        VesselControlRequest = 70,  // Client -> server: request control lock on a vessel
        VesselControlUpdate = 71,   // Server -> clients: current controller of a vessel
        VesselControlRelease = 72,  // Client -> server: release control lock
    }

    // ── Player Packets ──────────────────────────────────────────

    public struct PlayerJoinPacket
    {
        public int PeerId;
        public string SteamName;
        public string PlayerIdString;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.PlayerJoin);
            writer.Put(PeerId);
            writer.Put(SteamName ?? "Unknown");
            writer.Put(PlayerIdString ?? "Unknown");
        }

        public static PlayerJoinPacket Deserialize(NetDataReader reader)
        {
            return new PlayerJoinPacket 
            { 
                PeerId = reader.GetInt(), 
                SteamName = reader.GetString(),
                PlayerIdString = reader.GetString()
            };
        }
    }

    /// <summary>Sent by server to a new client with the full player roster.</summary>
    public struct PlayerListPacket
    {
        public PlayerEntry[] Players;

        public struct PlayerEntry
        {
            public int PeerId;
            public string SteamName;
            public bool IsHost;
            public int Ping;
            public bool IsAdmin;
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.PlayerList);
            writer.Put(Players?.Length ?? 0);
            if (Players != null)
            {
                foreach (var p in Players)
                {
                    writer.Put(p.PeerId);
                    writer.Put(p.SteamName ?? "Unknown");
                    writer.Put(p.IsHost);
                    writer.Put(p.Ping);
                    writer.Put(p.IsAdmin);
                }
            }
        }

        public static PlayerListPacket Deserialize(NetDataReader reader)
        {
            int count = reader.GetInt();
            var players = new PlayerEntry[count];
            for (int i = 0; i < count; i++)
            {
                players[i] = new PlayerEntry
                {
                    PeerId = reader.GetInt(),
                    SteamName = reader.GetString(),
                    IsHost = reader.GetBool(),
                    Ping = reader.GetInt(),
                    IsAdmin = reader.GetBool()
                };
            }
            return new PlayerListPacket { Players = players };
        }
    }

    /// <summary>Broadcast by server when a player disconnects.</summary>
    public struct PlayerLeavePacket
    {
        public int PeerId;
        public string SteamName;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.PlayerLeave);
            writer.Put(PeerId);
            writer.Put(SteamName ?? "Unknown");
        }

        public static PlayerLeavePacket Deserialize(NetDataReader reader)
        {
            return new PlayerLeavePacket
            {
                PeerId = reader.GetInt(),
                SteamName = reader.GetString()
            };
        }
    }

    /// <summary>Broadcast by server periodically with all player pings.</summary>
    public struct PlayerPingPacket
    {
        public PingEntry[] Pings;

        public struct PingEntry
        {
            public int PeerId;
            public int PingMs;
        }

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.PlayerPing);
            writer.Put(Pings?.Length ?? 0);
            if (Pings != null)
            {
                foreach (var p in Pings)
                {
                    writer.Put(p.PeerId);
                    writer.Put(p.PingMs);
                }
            }
        }

        public static PlayerPingPacket Deserialize(NetDataReader reader)
        {
            int count = reader.GetInt();
            var pings = new PingEntry[count];
            for (int i = 0; i < count; i++)
            {
                pings[i] = new PingEntry
                {
                    PeerId = reader.GetInt(),
                    PingMs = reader.GetInt()
                };
            }
            return new PlayerPingPacket { Pings = pings };
        }
    }

    // ── Warp Voting Packets ─────────────────────────────────────

    /// <summary>Sent by client to request a warp rate change. Server rebroadcasts to all.</summary>
    public struct WarpVoteRequestPacket
    {
        public int RequesterPeerId;
        public string RequesterName;
        public int TargetWarpRate;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.WarpVoteRequest);
            writer.Put(RequesterPeerId);
            writer.Put(RequesterName ?? "Unknown");
            writer.Put(TargetWarpRate);
        }

        public static WarpVoteRequestPacket Deserialize(NetDataReader reader)
        {
            return new WarpVoteRequestPacket
            {
                RequesterPeerId = reader.GetInt(),
                RequesterName = reader.GetString(),
                TargetWarpRate = reader.GetInt()
            };
        }
    }

    /// <summary>Sent by each client to vote on a pending warp request.</summary>
    public struct WarpVoteResponsePacket
    {
        public int VoterPeerId;
        public bool Approved;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.WarpVoteResponse);
            writer.Put(VoterPeerId);
            writer.Put(Approved);
        }

        public static WarpVoteResponsePacket Deserialize(NetDataReader reader)
        {
            return new WarpVoteResponsePacket
            {
                VoterPeerId = reader.GetInt(),
                Approved = reader.GetBool()
            };
        }
    }

    /// <summary>Broadcast by server when warp vote concludes.</summary>
    public struct WarpVoteResultPacket
    {
        public bool Passed;
        public int TargetWarpRate;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.WarpVoteResult);
            writer.Put(Passed);
            writer.Put(TargetWarpRate);
        }

        public static WarpVoteResultPacket Deserialize(NetDataReader reader)
        {
            return new WarpVoteResultPacket
            {
                Passed = reader.GetBool(),
                TargetWarpRate = reader.GetInt()
            };
        }
    }

    /// <summary>Broadcast by server to force-apply a warp rate on all clients.</summary>
    public struct WarpApplyPacket
    {
        public int WarpRateIndex;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.WarpApply);
            writer.Put(WarpRateIndex);
        }

        public static WarpApplyPacket Deserialize(NetDataReader reader)
        {
            return new WarpApplyPacket { WarpRateIndex = reader.GetInt() };
        }
    }

    // ── Generic Large Payload Streaming Packets ──────────────────────────────────

    public enum PayloadType : byte
    {
        SaveData = 0,
        VesselSpawn = 1
    }
    /// <summary>Carries one chunk of a large byte array.
    /// TransferId uniquely identifies one transfer so concurrent transfers can't interleave chunks.</summary>
    public struct SaveDataChunkPacket
    {
        public int TransferId;
        public int TargetPeerId;
        public PayloadType Type;
        public string VesselGuid;   // For VesselSpawn payloads; "" otherwise
        public int ChunkIndex;
        public int TotalChunks;
        public byte[] Payload;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.SaveDataChunk);
            writer.Put(TransferId);
            writer.Put(TargetPeerId);
            writer.Put((byte)Type);
            writer.Put(VesselGuid ?? "");
            writer.Put(ChunkIndex);
            writer.Put(TotalChunks);
            writer.Put(Payload.Length);
            writer.Put(Payload);
        }

        public static SaveDataChunkPacket Deserialize(NetDataReader reader)
        {
            int transferId = reader.GetInt();
            int targetPeerId = reader.GetInt();
            PayloadType type = (PayloadType)reader.GetByte();
            string vesselGuid = reader.GetString();
            int chunkIndex = reader.GetInt();
            int totalChunks = reader.GetInt();
            int len = reader.GetInt();
            byte[] payload = new byte[len];
            reader.GetBytes(payload, len);
            return new SaveDataChunkPacket { TransferId = transferId, TargetPeerId = targetPeerId, Type = type, VesselGuid = vesselGuid, ChunkIndex = chunkIndex, TotalChunks = totalChunks, Payload = payload };
        }
    }

    /// <summary>Signals that all chunks have been sent.</summary>
    public struct SaveDataCompletePacket
    {
        public int TransferId;
        public int TargetPeerId;
        public PayloadType Type;
        public string VesselGuid;
        public int TotalChunks;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.SaveDataComplete);
            writer.Put(TransferId);
            writer.Put(TargetPeerId);
            writer.Put((byte)Type);
            writer.Put(VesselGuid ?? "");
            writer.Put(TotalChunks);
        }

        public static SaveDataCompletePacket Deserialize(NetDataReader reader)
        {
            return new SaveDataCompletePacket { TransferId = reader.GetInt(), TargetPeerId = reader.GetInt(), Type = (PayloadType)reader.GetByte(), VesselGuid = reader.GetString(), TotalChunks = reader.GetInt() };
        }
    }

    // ── Sync Packets ────────────────────────────────────────────

    /// <summary>Legacy: Real-time physics state for one vessel using world-space coords (deprecated).</summary>
    public struct VesselTransformPacket
    {
        public string VesselGuid;
        public string OwnerName;
        public float PosX, PosY, PosZ;
        public float RotX, RotY, RotZ, RotW;
        public float VelX, VelY, VelZ;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.VesselTransform);
            writer.Put(VesselGuid ?? "");
            writer.Put(OwnerName ?? "");
            writer.Put(PosX); writer.Put(PosY); writer.Put(PosZ);
            writer.Put(RotX); writer.Put(RotY); writer.Put(RotZ); writer.Put(RotW);
            writer.Put(VelX); writer.Put(VelY); writer.Put(VelZ);
        }
        public static VesselTransformPacket Deserialize(NetDataReader reader)
        {
            var p = new VesselTransformPacket();
            p.VesselGuid = reader.GetString();
            p.OwnerName = reader.GetString();
            p.PosX = reader.GetFloat(); p.PosY = reader.GetFloat(); p.PosZ = reader.GetFloat();
            p.RotX = reader.GetFloat(); p.RotY = reader.GetFloat(); p.RotZ = reader.GetFloat(); p.RotW = reader.GetFloat();
            p.VelX = reader.GetFloat(); p.VelY = reader.GetFloat(); p.VelZ = reader.GetFloat();
            return p;
        }
    }

    /// <summary>New: orbital element sync for one vessel. Uses Keplerian elements for floating-origin safety.</summary>
    public struct VesselStatePacket
    {
        public string VesselGuid;
        public string VesselName;
        public string OwnerName;
        public string ReferenceBodyName;
        public byte Situation;
        // Keplerian orbital elements (double precision)
        public double Inclination;
        public double Eccentricity;
        public double SemiMajorAxis;
        public double LongitudeOfAscendingNode;
        public double ArgumentOfPeriapsis;
        public double MeanAnomalyAtEpoch;
        public double Epoch;
        // Rotation quaternion (double precision to match KSP2's QuaternionD)
        public double RotX, RotY, RotZ, RotW;
        // World position (for nearby visual sync during launch/docking)
        public double PosX, PosY, PosZ;
        // Velocity (for interpolation)
        public double VelX, VelY, VelZ;
        // For landed vessels (body-relative)
        public double Latitude, Longitude, AltitudeFromTerrain;
        // Flags
        public bool IsUnderThrust;
        // Sender's synced universe time when this state was captured (for interpolation)
        public double TimeStamp;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.VesselState);
            writer.Put(VesselGuid ?? "");
            writer.Put(VesselName ?? "");
            writer.Put(OwnerName ?? "");
            writer.Put(ReferenceBodyName ?? "Kerbin");
            writer.Put(Situation);
            writer.Put(Inclination);
            writer.Put(Eccentricity);
            writer.Put(SemiMajorAxis);
            writer.Put(LongitudeOfAscendingNode);
            writer.Put(ArgumentOfPeriapsis);
            writer.Put(MeanAnomalyAtEpoch);
            writer.Put(Epoch);
            writer.Put(RotX); writer.Put(RotY); writer.Put(RotZ); writer.Put(RotW);
            writer.Put(PosX); writer.Put(PosY); writer.Put(PosZ);
            writer.Put(VelX); writer.Put(VelY); writer.Put(VelZ);
            writer.Put(Latitude); writer.Put(Longitude); writer.Put(AltitudeFromTerrain);
            writer.Put(IsUnderThrust);
            writer.Put(TimeStamp);
        }

        public static VesselStatePacket Deserialize(NetDataReader reader)
        {
            var p = new VesselStatePacket();
            p.VesselGuid = reader.GetString();
            p.VesselName = reader.GetString();
            p.OwnerName = reader.GetString();
            p.ReferenceBodyName = reader.GetString();
            p.Situation = reader.GetByte();
            p.Inclination = reader.GetDouble();
            p.Eccentricity = reader.GetDouble();
            p.SemiMajorAxis = reader.GetDouble();
            p.LongitudeOfAscendingNode = reader.GetDouble();
            p.ArgumentOfPeriapsis = reader.GetDouble();
            p.MeanAnomalyAtEpoch = reader.GetDouble();
            p.Epoch = reader.GetDouble();
            p.RotX = reader.GetDouble(); p.RotY = reader.GetDouble(); p.RotZ = reader.GetDouble(); p.RotW = reader.GetDouble();
            p.PosX = reader.GetDouble(); p.PosY = reader.GetDouble(); p.PosZ = reader.GetDouble();
            p.VelX = reader.GetDouble(); p.VelY = reader.GetDouble(); p.VelZ = reader.GetDouble();
            p.Latitude = reader.GetDouble(); p.Longitude = reader.GetDouble(); p.AltitudeFromTerrain = reader.GetDouble();
            p.IsUnderThrust = reader.GetBool();
            p.TimeStamp = reader.GetDouble();
            return p;
        }
    }

    /// <summary>Broadcast when a vessel is destroyed.</summary>
    public struct VesselDestroyedPacket
    {
        public string VesselGuid;
        public string OwnerName;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.VesselDestroyed);
            writer.Put(VesselGuid ?? "");
            writer.Put(OwnerName ?? "");
        }

        public static VesselDestroyedPacket Deserialize(NetDataReader reader)
        {
            return new VesselDestroyedPacket
            {
                VesselGuid = reader.GetString(),
                OwnerName = reader.GetString()
            };
        }
    }

    /// <summary>Broadcast when a vessel is recovered.</summary>
    public struct VesselRecoveredPacket
    {
        public string VesselGuid;
        public string OwnerName;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.VesselRecovered);
            writer.Put(VesselGuid ?? "");
            writer.Put(OwnerName ?? "");
        }

        public static VesselRecoveredPacket Deserialize(NetDataReader reader)
        {
            return new VesselRecoveredPacket
            {
                VesselGuid = reader.GetString(),
                OwnerName = reader.GetString()
            };
        }
    }

    /// <summary>In-game chat message.</summary>
    public struct ChatMessagePacket
    {
        public string SenderName;
        public string Text;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.ChatMessage);
            writer.Put(SenderName ?? "");
            writer.Put(Text ?? "");
        }

        public static ChatMessagePacket Deserialize(NetDataReader reader)
        {
            return new ChatMessagePacket
            {
                SenderName = reader.GetString(),
                Text = reader.GetString()
            };
        }
    }

    /// <summary>Sync a tech node unlock to all clients.</summary>
    public struct TechUnlockPacket
    {
        public string NodeId;
        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.TechUnlock);
            writer.Put(NodeId ?? "");
        }
        public static TechUnlockPacket Deserialize(NetDataReader reader)
        {
            return new TechUnlockPacket { NodeId = reader.GetString() };
        }
    }

    /// <summary>Sync science points earned with full experiment context.</summary>
    public struct ScienceEarnedPacket
    {
        public float Amount;
        public string ExperimentID;
        public string ResearchLocationID;
        public byte ReportType;   // KSP.Game.Science.ScienceReportType: 1=DataType, 2=SampleType

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.ScienceEarned);
            writer.Put(Amount);
            writer.Put(ExperimentID ?? "");
            writer.Put(ResearchLocationID ?? "");
            writer.Put(ReportType);
        }
        public static ScienceEarnedPacket Deserialize(NetDataReader reader)
        {
            return new ScienceEarnedPacket
            {
                Amount = reader.GetFloat(),
                ExperimentID = reader.GetString(),
                ResearchLocationID = reader.GetString(),
                ReportType = reader.GetByte()
            };
        }
    }

    // ── Time Sync Packets ───────────────────────────────────────

    /// <summary>Server -> clients: authoritative universe time. Broadcast ~2Hz, Sequenced.</summary>
    public struct TimeSyncPacket
    {
        public double ServerUT;
        public int WarpRateIndex;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.TimeSync);
            writer.Put(ServerUT);
            writer.Put(WarpRateIndex);
        }

        public static TimeSyncPacket Deserialize(NetDataReader reader)
        {
            return new TimeSyncPacket { ServerUT = reader.GetDouble(), WarpRateIndex = reader.GetInt() };
        }
    }

    /// <summary>Client -> server: reports local universe time (seeds the server clock).</summary>
    public struct UTReportPacket
    {
        public double UT;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.UTReport);
            writer.Put(UT);
        }

        public static UTReportPacket Deserialize(NetDataReader reader)
        {
            return new UTReportPacket { UT = reader.GetDouble() };
        }
    }

    // ── Vessel Control Lock Packets ─────────────────────────────

    /// <summary>Client -> server: request control lock on a vessel.</summary>
    public struct VesselControlRequestPacket
    {
        public string VesselGuid;
        public string PlayerId;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.VesselControlRequest);
            writer.Put(VesselGuid ?? "");
            writer.Put(PlayerId ?? "");
        }

        public static VesselControlRequestPacket Deserialize(NetDataReader reader)
        {
            return new VesselControlRequestPacket { VesselGuid = reader.GetString(), PlayerId = reader.GetString() };
        }
    }

    /// <summary>Server -> clients: who controls a vessel. ControllerPeerId == -1 means unlocked.</summary>
    public struct VesselControlUpdatePacket
    {
        public string VesselGuid;
        public int ControllerPeerId;
        public string ControllerPlayerId;
        public string ControllerName;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.VesselControlUpdate);
            writer.Put(VesselGuid ?? "");
            writer.Put(ControllerPeerId);
            writer.Put(ControllerPlayerId ?? "");
            writer.Put(ControllerName ?? "");
        }

        public static VesselControlUpdatePacket Deserialize(NetDataReader reader)
        {
            return new VesselControlUpdatePacket
            {
                VesselGuid = reader.GetString(),
                ControllerPeerId = reader.GetInt(),
                ControllerPlayerId = reader.GetString(),
                ControllerName = reader.GetString()
            };
        }
    }

    /// <summary>Client -> server: release control lock on a vessel.</summary>
    public struct VesselControlReleasePacket
    {
        public string VesselGuid;
        public string PlayerId;

        public void Serialize(NetDataWriter writer)
        {
            writer.Put((byte)PacketType.VesselControlRelease);
            writer.Put(VesselGuid ?? "");
            writer.Put(PlayerId ?? "");
        }

        public static VesselControlReleasePacket Deserialize(NetDataReader reader)
        {
            return new VesselControlReleasePacket { VesselGuid = reader.GetString(), PlayerId = reader.GetString() };
        }
    }
}
