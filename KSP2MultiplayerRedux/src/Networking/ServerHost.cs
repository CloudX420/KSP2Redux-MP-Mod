using System;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib;
using LiteNetLib.Utils;

namespace KSP2MultiplayerRedux.Networking
{
    /// <summary>
    /// Authoritative game server built on LiteNetLib.
    /// Manages player connections, packet routing, and periodic ping broadcasts.
    /// </summary>
    public class ServerHost
    {
        private const string ConnectionKey = Constants.CONNECTION_KEY;

        private readonly int _port;
        private NetManager _server;
        private EventBasedNetListener _listener;

        private float _lastPingBroadcast;
        private const float PingBroadcastInterval = 2f;

        // ── Public State ────────────────────────────────────────────

        /// <summary>All players that have completed the join handshake (sent PlayerJoinPacket).</summary>
        public Dictionary<int, PlayerInfo> ConnectedPlayers { get; } = new Dictionary<int, PlayerInfo>();

        /// <summary>
        /// PeerId of the first peer that connected, typically the host's own loopback client.
        /// -1 means the host itself (no peer yet).
        /// </summary>
        public int HostPeerId { get; private set; } = -1;

        public bool IsRunning => _server != null && _server.IsRunning;

        // ── Events ──────────────────────────────────────────────────

        public event Action<int, string> OnPlayerJoined;
        public event Action<int, string> OnPlayerLeft;
        public event Action<int, bool> OnWarpVoteReceived;

        // ── Vessel Control Locks (listen-server authoritative) ─────
        private class VesselLock
        {
            public int PeerId;
            public string PlayerId;
            public string PlayerName;
        }
        private readonly Dictionary<string, VesselLock> _vesselLocks = new Dictionary<string, VesselLock>();
        
        public event Action<VesselTransformPacket> OnVesselTransformReceived;
        public event Action<TechUnlockPacket> OnTechUnlockReceived;
        public event Action<ScienceEarnedPacket> OnScienceEarnedReceived;
        public event Action<int> OnSaveTransferComplete;

        // ── Constructor ─────────────────────────────────────────────

        public ServerHost(int port)
        {
            _port = port;
        }

        // ── Lifecycle ───────────────────────────────────────────────

        public void Start()
        {
            _listener = new EventBasedNetListener();
            _server = new NetManager(_listener);

            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;

            _server.Start(_port);
            _lastPingBroadcast = 0f;

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ServerHost] Started on port {_port}");
        }

        public void Update()
        {
            if (_server == null || !_server.IsRunning) return;

            _server.PollEvents();

            // Periodic ping broadcast
            float now = UnityEngine.Time.time;
            if (now - _lastPingBroadcast >= PingBroadcastInterval)
            {
                _lastPingBroadcast = now;
                BroadcastPing();
            }
        }

        public void Stop()
        {
            if (_server != null)
            {
                _server.Stop();
                _server = null;
                _listener = null;
            }
            ConnectedPlayers.Clear();
            HostPeerId = -1;

            KSP2MultiplayerReduxPlugin.Logger.LogInfo("[ServerHost] Stopped");
        }

        // ── Sending ─────────────────────────────────────────────────

        public void SendToAll(byte[] data)
        {
            if (_server == null || !_server.IsRunning) return;
            foreach (var peer in _server.ConnectedPeerList)
            {
                peer.Send(data, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendToPeer(int peerId, byte[] data, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered)
        {
            if (_server == null || !_server.IsRunning) return;
            foreach (var peer in _server.ConnectedPeerList)
            {
                if (peer.Id == peerId)
                {
                    peer.Send(data, deliveryMethod);
                    return;
                }
            }
        }

        public void SendToAllExcept(int excludePeerId, byte[] data, DeliveryMethod deliveryMethod = DeliveryMethod.ReliableOrdered)
        {
            if (_server == null || !_server.IsRunning) return;
            foreach (var peer in _server.ConnectedPeerList)
            {
                if (peer.Id != excludePeerId)
                {
                    peer.Send(data, deliveryMethod);
                }
            }
        }

        public byte[] SerializePacket(Action<NetDataWriter> writeAction)
        {
            var writer = new NetDataWriter();
            writeAction(writer);
            return writer.CopyData();
        }

        // ── Save Transfer ───────────────────────────────────────────

        private static readonly Random _transferIdRng = new Random();

        public void StartLargePayloadTransfer(int targetPeerId, PayloadType type, byte[] payloadData, string vesselGuid = "")
        {
            const int ChunkSize = 32768; // 32KB
            int totalChunks = (int)Math.Ceiling((double)payloadData.Length / ChunkSize);
            int transferId = _transferIdRng.Next();

            for (int i = 0; i < totalChunks; i++)
            {
                int offset = i * ChunkSize;
                int size = Math.Min(ChunkSize, payloadData.Length - offset);
                byte[] chunkPayload = new byte[size];
                Array.Copy(payloadData, offset, chunkPayload, 0, size);

                var chunkPacket = new SaveDataChunkPacket
                {
                    TransferId = transferId,
                    TargetPeerId = targetPeerId,
                    Type = type,
                    VesselGuid = vesselGuid,
                    ChunkIndex = i,
                    TotalChunks = totalChunks,
                    Payload = chunkPayload
                };

                byte[] data = SerializePacket(w => chunkPacket.Serialize(w));
                SendToPeer(targetPeerId, data);
            }

            var completePacket = new SaveDataCompletePacket { TransferId = transferId, TargetPeerId = targetPeerId, Type = type, VesselGuid = vesselGuid, TotalChunks = totalChunks };
            byte[] completeData = SerializePacket(w => completePacket.Serialize(w));
            SendToPeer(targetPeerId, completeData);

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ServerHost] Save transfer to peer {targetPeerId}: {totalChunks} chunks ({payloadData.Length} bytes)");
            OnSaveTransferComplete?.Invoke(targetPeerId);
        }

        // ── Listener Callbacks ──────────────────────────────────────

        private void OnConnectionRequest(ConnectionRequest request)
        {
            if (request.Data.GetString() == ConnectionKey)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ServerHost] Connection request accepted from {request.RemoteEndPoint}");
                request.Accept();
            }
            else
            {
                KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[ServerHost] Connection request rejected from {request.RemoteEndPoint} (bad key)");
                request.Reject();
            }
        }

        private void OnPeerConnected(NetPeer peer)
        {
            if (HostPeerId == -1)
            {
                HostPeerId = peer.Id;
            }
            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ServerHost] Peer connected: id={peer.Id}, endpoint={peer.EndPoint}");
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            string steamName = "Unknown";
            if (ConnectedPlayers.TryGetValue(peer.Id, out var info))
            {
                steamName = info.SteamName;
                ConnectedPlayers.Remove(peer.Id);
            }

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ServerHost] Peer disconnected: id={peer.Id}, name={steamName}, reason={disconnectInfo.Reason}");

            var leavePacket = new PlayerLeavePacket { PeerId = peer.Id, SteamName = steamName };
            byte[] data = SerializePacket(w => leavePacket.Serialize(w));
            SendToAll(data);

            ReleaseLocksForPeer(peer.Id);
            OnPlayerLeft?.Invoke(peer.Id, steamName);
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            if (reader.AvailableBytes < 1) return;

            var packetType = (PacketType)reader.GetByte();

            try
            {
                switch (packetType)
                {
                    case PacketType.PlayerJoin:
                        HandlePlayerJoin(peer, reader);
                        break;
                    case PacketType.WarpVoteRequest:
                        HandleWarpVoteRequest(peer, reader);
                        break;
                    case PacketType.WarpVoteResponse:
                        HandleWarpVoteResponse(peer, reader);
                        break;
                    case PacketType.VesselTransform:
                        var vPacket = VesselTransformPacket.Deserialize(reader);
                        byte[] vData = SerializePacket(w => vPacket.Serialize(w));
                        SendToAllExcept(peer.Id, vData, DeliveryMethod.Unreliable);
                        OnVesselTransformReceived?.Invoke(vPacket);
                        break;
                    case PacketType.VesselState:
                        RelayRemaining(peer, packetType, reader, DeliveryMethod.Sequenced);
                        break;
                    case PacketType.VesselDestroyed:
                    case PacketType.VesselRecovered:
                    case PacketType.ChatMessage:
                    case PacketType.SaveDataChunk:
                    case PacketType.SaveDataComplete:
                        RelayRemaining(peer, packetType, reader, DeliveryMethod.ReliableOrdered);
                        break;
                    case PacketType.UTReport:
                        // Listen server: the host's own game clock is authoritative — ignore reports.
                        break;
                    case PacketType.VesselControlRequest:
                        HandleControlRequest(peer, VesselControlRequestPacket.Deserialize(reader));
                        break;
                    case PacketType.VesselControlRelease:
                        HandleControlRelease(peer, VesselControlReleasePacket.Deserialize(reader));
                        break;
                    case PacketType.TechUnlock:
                        var tPacket = TechUnlockPacket.Deserialize(reader);
                        byte[] tData = SerializePacket(w => tPacket.Serialize(w));
                        SendToAllExcept(peer.Id, tData);
                        OnTechUnlockReceived?.Invoke(tPacket);
                        break;
                    case PacketType.ScienceEarned:
                        var sPacket = ScienceEarnedPacket.Deserialize(reader);
                        byte[] sData = SerializePacket(w => sPacket.Serialize(w));
                        SendToAllExcept(peer.Id, sData);
                        OnScienceEarnedReceived?.Invoke(sPacket);
                        break;
                    default:
                        // Forward-compatible: relay unknown packets so newer sync features still flow in listen mode.
                        RelayRemaining(peer, packetType, reader, deliveryMethod);
                        break;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }

        // ── Packet Handlers ─────────────────────────────────────────

        private void HandlePlayerJoin(NetPeer peer, NetPacketReader reader)
        {
            var packet = PlayerJoinPacket.Deserialize(reader);
            string steamName = packet.SteamName;

            var playerInfo = new PlayerInfo(peer.Id, steamName, isHost: peer.Id == HostPeerId);
            ConnectedPlayers[peer.Id] = playerInfo;

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ServerHost] Player joined: id={peer.Id}, name={steamName}");

            var joinPacket = new PlayerJoinPacket { SteamName = steamName };
            byte[] joinData = SerializePacket(w => joinPacket.Serialize(w));
            SendToAllExcept(peer.Id, joinData);

            SendPlayerList(peer);

            OnPlayerJoined?.Invoke(peer.Id, steamName);
        }

        private void HandleWarpVoteRequest(NetPeer peer, NetPacketReader reader)
        {
            var packet = WarpVoteRequestPacket.Deserialize(reader);

            packet.RequesterPeerId = peer.Id;
            if (ConnectedPlayers.TryGetValue(peer.Id, out var info))
            {
                packet.RequesterName = info.SteamName;
            }

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ServerHost] WarpVoteRequest from peer {peer.Id} for rate {packet.TargetWarpRate}");

            byte[] data = SerializePacket(w => packet.Serialize(w));
            SendToAll(data);
        }

        private void HandleWarpVoteResponse(NetPeer peer, NetPacketReader reader)
        {
            var packet = WarpVoteResponsePacket.Deserialize(reader);

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ServerHost] WarpVoteResponse from peer {peer.Id}: approved={packet.Approved}");

            OnWarpVoteReceived?.Invoke(peer.Id, packet.Approved);
        }

        // ── Relay / Locks / Time ────────────────────────────────────

        private void RelayRemaining(NetPeer sender, PacketType type, NetPacketReader reader, DeliveryMethod delivery)
        {
            byte[] remaining = reader.GetRemainingBytes();
            var writer = new NetDataWriter();
            writer.Put((byte)type);
            writer.Put(remaining);
            SendToAllExcept(sender.Id, writer.CopyData(), delivery);
        }

        private void HandleControlRequest(NetPeer peer, VesselControlRequestPacket packet)
        {
            if (string.IsNullOrEmpty(packet.VesselGuid)) return;

            if (_vesselLocks.TryGetValue(packet.VesselGuid, out var existing) &&
                !string.Equals(existing.PlayerId, packet.PlayerId, StringComparison.Ordinal))
            {
                // Denied — re-broadcast current holder so the requester learns who owns it
                BroadcastControlUpdate(packet.VesselGuid, existing.PeerId, existing.PlayerId, existing.PlayerName);
                return;
            }

            string name = ConnectedPlayers.TryGetValue(peer.Id, out var info) ? info.SteamName : "Unknown";
            _vesselLocks[packet.VesselGuid] = new VesselLock { PeerId = peer.Id, PlayerId = packet.PlayerId, PlayerName = name };
            BroadcastControlUpdate(packet.VesselGuid, peer.Id, packet.PlayerId, name);
        }

        private void HandleControlRelease(NetPeer peer, VesselControlReleasePacket packet)
        {
            if (_vesselLocks.TryGetValue(packet.VesselGuid, out var existing) &&
                string.Equals(existing.PlayerId, packet.PlayerId, StringComparison.Ordinal))
            {
                _vesselLocks.Remove(packet.VesselGuid);
                BroadcastControlUpdate(packet.VesselGuid, -1, "", "");
            }
        }

        private void BroadcastControlUpdate(string vesselGuid, int peerId, string playerId, string playerName)
        {
            var packet = new VesselControlUpdatePacket
            {
                VesselGuid = vesselGuid,
                ControllerPeerId = peerId,
                ControllerPlayerId = playerId,
                ControllerName = playerName
            };
            SendToAll(SerializePacket(w => packet.Serialize(w)));
        }

        private void ReleaseLocksForPeer(int peerId)
        {
            var released = _vesselLocks.Where(kv => kv.Value.PeerId == peerId).Select(kv => kv.Key).ToList();
            foreach (var guid in released)
            {
                _vesselLocks.Remove(guid);
                BroadcastControlUpdate(guid, -1, "", "");
            }
        }

        /// <summary>Broadcast the host's authoritative universe time (listen-server mode).</summary>
        public void BroadcastTimeSync(double universeTime, int warpRateIndex)
        {
            var packet = new TimeSyncPacket { ServerUT = universeTime, WarpRateIndex = warpRateIndex };
            byte[] data = SerializePacket(w => packet.Serialize(w));
            if (_server == null || !_server.IsRunning) return;
            foreach (var peer in _server.ConnectedPeerList)
                peer.Send(data, DeliveryMethod.Sequenced);
        }

        // ── Helpers ─────────────────────────────────────────────────

        private void SendPlayerList(NetPeer targetPeer)
        {
            var entries = ConnectedPlayers.Values.Select(p => new PlayerListPacket.PlayerEntry
            {
                PeerId = p.PeerId,
                SteamName = p.SteamName,
                IsHost = p.IsHost,
                Ping = p.Ping
            }).ToArray();

            var listPacket = new PlayerListPacket { Players = entries };
            byte[] data = SerializePacket(w => listPacket.Serialize(w));
            targetPeer.Send(data, DeliveryMethod.ReliableOrdered);
        }

        private void BroadcastPing()
        {
            if (ConnectedPlayers.Count == 0) return;

            foreach (var peer in _server.ConnectedPeerList)
            {
                if (ConnectedPlayers.TryGetValue(peer.Id, out var info))
                {
                    info.Ping = peer.Ping;
                }
            }

            var pingEntries = ConnectedPlayers.Values.Select(p => new PlayerPingPacket.PingEntry
            {
                PeerId = p.PeerId,
                PingMs = p.Ping
            }).ToArray();

            var pingPacket = new PlayerPingPacket { Pings = pingEntries };
            byte[] data = SerializePacket(w => pingPacket.Serialize(w));
            SendToAll(data);
        }
    }
}
