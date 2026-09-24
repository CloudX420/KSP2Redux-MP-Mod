using System;
using LiteNetLib;
using LiteNetLib.Utils;

namespace KSP2MultiplayerRedux.Networking
{
    public class ClientConnection
    {
        private const string ConnectionKey = Constants.CONNECTION_KEY;

        private readonly string _ip;
        private readonly int _port;
        private readonly string _localPlayerName;

        private NetManager _client;
        private EventBasedNetListener _listener;
        private NetPeer _serverPeer;

        public bool IsConnected => _serverPeer != null && _serverPeer.ConnectionState == ConnectionState.Connected;

        /// <summary>Round-trip time to the server in milliseconds (0 if not connected).</summary>
        public int PingMs => _serverPeer?.Ping ?? 0;

        private static readonly Random _transferIdRng = new Random();

        // ── Events ──────────────────────────────────────────────────

        public event Action<PlayerListPacket> OnPlayerListReceived;
        public event Action<PlayerJoinPacket> OnPlayerJoined;
        public event Action<int, string> OnPlayerLeft;
        public event Action<PlayerPingPacket> OnPingUpdated;

        public event Action<WarpVoteRequestPacket> OnWarpVoteRequested;
        public event Action<WarpVoteResultPacket> OnWarpVoteResult;
        public event Action<WarpApplyPacket> OnWarpApply;

        public event Action<VesselTransformPacket> OnVesselTransformReceived;
        public event Action<VesselStatePacket> OnVesselStateReceived;
        public event Action<VesselDestroyedPacket> OnVesselDestroyedReceived;
        public event Action<VesselRecoveredPacket> OnVesselRecoveredReceived;
        public event Action<SaveDataChunkPacket> OnSaveChunkReceived;
        public event Action<SaveDataCompletePacket> OnSaveComplete;
        public event Action<TechUnlockPacket> OnTechUnlockReceived;
        public event Action<ScienceEarnedPacket> OnScienceEarnedReceived;

        public event Action OnSaveRequested;

        public event Action<TimeSyncPacket> OnTimeSyncReceived;
        public event Action<VesselControlUpdatePacket> OnVesselControlUpdateReceived;

        public event Action OnDisconnected;

        // ── Constructor ─────────────────────────────────────────────

        public ClientConnection(string ip, int port, string localPlayerName)
        {
            _ip = ip;
            _port = port;
            _localPlayerName = localPlayerName;

            _listener = new EventBasedNetListener();
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;

            _client = new NetManager(_listener);
            _client.Start();
            _client.Connect(_ip, _port, ConnectionKey);

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] Attempting connection to {ip}:{port} as '{localPlayerName}'");
        }

        // ── Lifecycle ───────────────────────────────────────────────

        public void Update()
        {
            _client?.PollEvents();
        }

        public void Disconnect()
        {
            if (_client != null)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogInfo("[ClientConnection] Disconnecting...");
                _client.Stop();
                _client = null;
                _listener = null;
                _serverPeer = null;
            }
        }

        // ── Sending ─────────────────────────────────────────────────

        public void Send(byte[] data)
        {
            if (IsConnected)
            {
                _serverPeer.Send(data, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendUnreliable(byte[] data)
        {
            if (IsConnected)
            {
                _serverPeer.Send(data, DeliveryMethod.Unreliable);
            }
        }

        public void SendSequenced(byte[] data)
        {
            if (IsConnected)
            {
                _serverPeer.Send(data, DeliveryMethod.Sequenced);
            }
        }

        public void StartLargePayloadTransfer(int targetPeerId, PayloadType type, byte[] payloadData, string vesselGuid = "")
        {
            if (!IsConnected) return;

            const int ChunkSize = 32768; // 32KB
            int totalChunks = (int)Math.Ceiling((double)payloadData.Length / ChunkSize);
            int transferId = _transferIdRng.Next();

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] Starting {type} transfer #{transferId}: {payloadData.Length} bytes, {totalChunks} chunks, targetPeer={targetPeerId}");

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
                Send(data);

                if (i % 10 == 0 || i == totalChunks - 1)
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] Sent {type} chunk {i + 1}/{totalChunks}");
            }

            var completePacket = new SaveDataCompletePacket { TransferId = transferId, TargetPeerId = targetPeerId, Type = type, VesselGuid = vesselGuid, TotalChunks = totalChunks };
            byte[] completeData = SerializePacket(w => completePacket.Serialize(w));
            Send(completeData);

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] ✓ {type} transfer complete! {totalChunks} chunks ({payloadData.Length} bytes) sent.");
        }

        public byte[] SerializePacket(Action<NetDataWriter> writeAction)
        {
            var writer = new NetDataWriter();
            writeAction(writer);
            return writer.CopyData();
        }

        // ── Listener Callbacks ──────────────────────────────────────

        private void OnPeerConnected(NetPeer peer)
        {
            _serverPeer = peer;
            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] ✓ Connected to server! peerId={peer.Id}, endpoint={peer.EndPoint}");

            var joinPacket = new PlayerJoinPacket 
            { 
                PeerId = -1, 
                SteamName = NetworkManager.Instance.LocalPlayerName,
                PlayerIdString = NetworkManager.Instance.LocalPlayerIdString
            };
            byte[] data = SerializePacket(w => joinPacket.Serialize(w));
            Send(data);
            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] Sent PlayerJoin packet (name='{NetworkManager.Instance.LocalPlayerName}', id='{NetworkManager.Instance.LocalPlayerIdString}')");
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            _serverPeer = null;
            KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[ClientConnection] Disconnected from server: {disconnectInfo.Reason}");
            OnDisconnected?.Invoke();
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
                    {
                        var packet = PlayerJoinPacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << PlayerJoin: {packet.SteamName} (peerId={packet.PeerId})");
                        OnPlayerJoined?.Invoke(packet);
                        break;
                    }
                    case PacketType.PlayerList:
                    {
                        var packet = PlayerListPacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << PlayerList: {packet.Players?.Length ?? 0} players");
                        OnPlayerListReceived?.Invoke(packet);
                        break;
                    }
                    case PacketType.PlayerLeave:
                    {
                        var packet = PlayerLeavePacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << PlayerLeave: {packet.SteamName} (peerId={packet.PeerId})");
                        OnPlayerLeft?.Invoke(packet.PeerId, packet.SteamName);
                        break;
                    }
                    case PacketType.PlayerPing:
                    {
                        var packet = PlayerPingPacket.Deserialize(reader);
                        OnPingUpdated?.Invoke(packet);
                        break;
                    }
                    case PacketType.WarpVoteRequest:
                    {
                        var packet = WarpVoteRequestPacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << WarpVoteRequest from {packet.RequesterName}, rate={packet.TargetWarpRate}");
                        OnWarpVoteRequested?.Invoke(packet);
                        break;
                    }
                    case PacketType.WarpVoteResult:
                    {
                        var packet = WarpVoteResultPacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << WarpVoteResult: passed={packet.Passed}");
                        OnWarpVoteResult?.Invoke(packet);
                        break;
                    }
                    case PacketType.WarpApply:
                    {
                        var packet = WarpApplyPacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << WarpApply: rate={packet.WarpRateIndex}");
                        OnWarpApply?.Invoke(packet);
                        break;
                    }
                    case PacketType.VesselTransform:
                    {
                        var packet = VesselTransformPacket.Deserialize(reader);
                        OnVesselTransformReceived?.Invoke(packet);
                        break;
                    }
                    case PacketType.SaveDataChunk:
                    {
                        var packet = SaveDataChunkPacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << SaveDataChunk: {packet.ChunkIndex + 1}/{packet.TotalChunks} ({packet.Payload?.Length ?? 0} bytes)");
                        OnSaveChunkReceived?.Invoke(packet);
                        break;
                    }
                    case PacketType.SaveDataComplete:
                    {
                        var packet = SaveDataCompletePacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << SaveDataComplete: {packet.TotalChunks} total chunks");
                        OnSaveComplete?.Invoke(packet);
                        break;
                    }
                    case PacketType.SaveRequest:
                    {
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo("[ClientConnection] << SaveRequest — server is asking us to upload our save!");
                        OnSaveRequested?.Invoke();
                        break;
                    }
                    case PacketType.TechUnlock:
                    {
                        var packet = TechUnlockPacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << TechUnlock: {packet.NodeId}");
                        OnTechUnlockReceived?.Invoke(packet);
                        break;
                    }
                    case PacketType.ScienceEarned:
                    {
                        var packet = ScienceEarnedPacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << ScienceEarned: {packet.Amount}");
                        OnScienceEarnedReceived?.Invoke(packet);
                        break;
                    }
                    case PacketType.VesselState:
                    {
                        var packet = VesselStatePacket.Deserialize(reader);
                        OnVesselStateReceived?.Invoke(packet);
                        break;
                    }
                    case PacketType.VesselDestroyed:
                    {
                        var packet = VesselDestroyedPacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << VesselDestroyed: {packet.VesselGuid}");
                        OnVesselDestroyedReceived?.Invoke(packet);
                        break;
                    }
                    case PacketType.VesselRecovered:
                    {
                        var packet = VesselRecoveredPacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << VesselRecovered: {packet.VesselGuid}");
                        OnVesselRecoveredReceived?.Invoke(packet);
                        break;
                    }
                    case PacketType.TimeSync:
                    {
                        var packet = TimeSyncPacket.Deserialize(reader);
                        OnTimeSyncReceived?.Invoke(packet);
                        break;
                    }
                    case PacketType.VesselControlUpdate:
                    {
                        var packet = VesselControlUpdatePacket.Deserialize(reader);
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[ClientConnection] << VesselControlUpdate: {packet.VesselGuid} -> {(packet.ControllerPeerId == -1 ? "UNLOCKED" : packet.ControllerName)}");
                        OnVesselControlUpdateReceived?.Invoke(packet);
                        break;
                    }
                    default:
                        KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[ClientConnection] << UNKNOWN packet type: {(byte)packetType}");
                        break;
                }
            }
            finally
            {
                reader.Recycle();
            }
        }
    }
}
