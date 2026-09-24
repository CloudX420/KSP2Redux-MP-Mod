using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;

namespace KSP2MultiplayerServer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "KSP2 Multiplayer Redux — Dedicated Server";
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("╔══════════════════════════════════════════════════╗");
            Console.WriteLine("║   KSP2 Multiplayer Redux — Dedicated Server     ║");
            Console.WriteLine($"║   v{Constants.VERSION,-44} ║");
            Console.WriteLine("╚══════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();

            int port = 7777;
            int maxPlayers = 8;

            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "-p" || args[i] == "--port") && i + 1 < args.Length)
                    int.TryParse(args[++i], out port);
                if ((args[i] == "-m" || args[i] == "--max") && i + 1 < args.Length)
                    int.TryParse(args[++i], out maxPlayers);
                if (args[i] == "-h" || args[i] == "--help")
                {
                    Console.WriteLine("Usage: KSP2MultiplayerServer [options]");
                    Console.WriteLine("  -p, --port <port>    Port to listen on (default: 7777)");
                    Console.WriteLine("  -m, --max <count>    Max players (default: 8)");
                    return;
                }
            }

            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server_config.json");
            var config = ServerConfig.Load(configPath);
            Console.WriteLine($"Loaded config from {configPath}. Admin IDs: {config.AdminSteamIDs.Count}");

            var server = new DedicatedServer(port, maxPlayers, config);
            server.Start();

            Console.WriteLine($"Server listening on port {port} (max {maxPlayers} players)");
            Console.WriteLine("Commands: list, kick <id>, save-status, vessels, quit");
            Console.WriteLine();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                server.Stop();
            };

            while (server.IsRunning)
            {
                server.Update();

                if (Console.KeyAvailable)
                {
                    string input = Console.ReadLine()?.Trim().ToLower() ?? "";
                    if (input == "list" || input == "players")
                        server.PrintPlayers();
                    else if (input == "save-status")
                        server.PrintSaveStatus();
                    else if (input == "vessels")
                        server.PrintVessels();
                    else if (input.StartsWith("kick "))
                    {
                        if (int.TryParse(input.Substring(5).Trim(), out int kickId))
                            server.KickPlayer(kickId);
                        else
                            Console.WriteLine("Usage: kick <peerId>");
                    }
                    else if (input == "quit" || input == "stop" || input == "exit")
                        server.Stop();
                    else if (!string.IsNullOrEmpty(input))
                        Console.WriteLine("Commands: list, kick <id>, save-status, vessels, quit");
                }

                Thread.Sleep(5);
            }

            Console.WriteLine("Server shut down.");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Packet Type IDs — must match the KSP2 mod's PacketTypes.cs
    // ═══════════════════════════════════════════════════════════════
    enum PacketType : byte
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
        SaveRequest = 23,
        VesselTransform = 30,
        VesselState = 31,
        VesselDestroyed = 32,
        VesselRecovered = 33,
        TechUnlock = 40,
        ScienceEarned = 41,
        ChatMessage = 50,
        TimeSync = 60,
        UTReport = 61,
        VesselControlRequest = 70,
        VesselControlUpdate = 71,
        VesselControlRelease = 72,
    }

    class PlayerInfo
    {
        public int PeerId;
        public string SteamName = "Unknown";
        public string PlayerId = "";   // SteamID / device id — the canonical identity
        public int Ping;
        public bool IsHost;
        public bool IsAdmin;
    }

    /// <summary>Everything the server retains about one vessel so it can rebuild the
    /// universe for late joiners and keep vessels alive when their pilot disconnects.</summary>
    class VesselRecord
    {
        public string Guid = "";
        public byte[] SpawnPayload;      // serialized assembly JSON (may be null if from base save)
        public byte[] LastStateRaw;      // raw VesselState packet body (after type byte)
        public string OwnerPlayerId = "";
        public DateTime LastUpdate = DateTime.UtcNow;
    }

    class VesselLock
    {
        public int PeerId;
        public string PlayerId = "";
        public string PlayerName = "";
    }

    class IncomingTransfer
    {
        public byte Type;
        public string VesselGuid = "";
        public int SenderPeerId;
        public int TotalChunks;
        public readonly Dictionary<int, byte[]> Chunks = new();
    }

    // ═══════════════════════════════════════════════════════════════
    // Dedicated Server: authoritative universe (save + vessels + time + locks)
    // ═══════════════════════════════════════════════════════════════
    class DedicatedServer
    {
        private const string ConnectionKey = Constants.CONNECTION_KEY;

        // KSP2 warp rate table — must match TimeSyncManager.WarpRates in the mod
        private static readonly double[] WarpRates = { 1, 2, 4, 10, 50, 100, 1000, 10_000, 100_000, 1_000_000, 10_000_000 };

        private readonly int _port;
        private readonly int _maxPlayers;
        private readonly ServerConfig _config;
        private readonly Random _rng = new();

        private NetManager _server;
        private EventBasedNetListener _listener;

        private readonly Dictionary<int, PlayerInfo> _players = new();
        private readonly Dictionary<int, NetPeer> _peers = new();

        // ── Save Cache ──────────────────────────────────────────
        private byte[] _cachedSave = null;
        private DateTime _cachedSaveTime = DateTime.MinValue;

        // ── Vessel Store (per-vessel universe state) ────────────
        private readonly Dictionary<string, VesselRecord> _vessels = new();

        // ── Control Locks ───────────────────────────────────────
        private readonly Dictionary<string, VesselLock> _vesselLocks = new();

        // ── Universe Time (authoritative clock) ─────────────────
        private double _universeTime;
        private bool _utSeeded;
        private int _currentWarpIndex;
        private DateTime _lastUtAdvance = DateTime.UtcNow;
        private DateTime _lastTimeSyncBroadcast = DateTime.UtcNow;
        private const double TimeSyncIntervalSeconds = 0.5;

        // ── Incoming chunked transfers keyed by (senderPeer, transferId) ──
        private readonly Dictionary<(int peer, int transferId), IncomingTransfer> _incomingTransfers = new();

        // ── Warp Voting ─────────────────────────────────────────
        private bool _voteActive;
        private int _voteTargetRate;
        private DateTime _voteStartTime;
        private readonly Dictionary<int, bool> _votes = new();
        private const double VoteTimeoutSeconds = 30.0;

        // ── Timers ──────────────────────────────────────────────
        private DateTime _lastPingBroadcast = DateTime.UtcNow;
        private const double PingBroadcastIntervalSeconds = 2.0;
        private DateTime _lastAutoSaveRequest = DateTime.UtcNow;
        private const double AutoSaveIntervalSeconds = 30.0;
        private DateTime _lastDiskFlush = DateTime.UtcNow;
        private const double DiskFlushIntervalSeconds = 60.0;

        // Queue for players waiting for a fresh save
        private readonly Dictionary<NetPeer, DateTime> _pendingJoinPeers = new();

        public bool IsRunning => _server != null && _server.IsRunning;

        private string BaseDir => AppDomain.CurrentDomain.BaseDirectory;
        private string SaveDir => Path.Combine(BaseDir, "Saves");
        private string VesselDir => Path.Combine(SaveDir, "Vessels");

        public DedicatedServer(int port, int maxPlayers, ServerConfig config)
        {
            _port = port;
            _maxPlayers = maxPlayers;
            _config = config;
        }

        public void Start()
        {
            _listener = new EventBasedNetListener();
            _server = new NetManager(_listener);

            _listener.ConnectionRequestEvent += OnConnectionRequest;
            _listener.PeerConnectedEvent += OnPeerConnected;
            _listener.PeerDisconnectedEvent += OnPeerDisconnected;
            _listener.NetworkReceiveEvent += OnNetworkReceive;

            _server.Start(System.Net.IPAddress.Any, System.Net.IPAddress.IPv6None, _port);
            Log("Server started successfully.", ConsoleColor.Green);

            LoadPersistedState();
        }

        public void Update()
        {
            if (_server == null || !_server.IsRunning) return;

            _server.PollEvents();

            AdvanceUniverseTime();

            if (_utSeeded && (DateTime.UtcNow - _lastTimeSyncBroadcast).TotalSeconds >= TimeSyncIntervalSeconds)
            {
                _lastTimeSyncBroadcast = DateTime.UtcNow;
                BroadcastTimeSync();
            }

            if (_voteActive && (DateTime.UtcNow - _voteStartTime).TotalSeconds > VoteTimeoutSeconds)
            {
                Log("Warp vote timed out — denying.", ConsoleColor.Yellow);
                BroadcastWarpVoteResult(false);
                _voteActive = false;
                _votes.Clear();
            }

            if ((DateTime.UtcNow - _lastPingBroadcast).TotalSeconds >= PingBroadcastIntervalSeconds)
            {
                _lastPingBroadcast = DateTime.UtcNow;
                BroadcastPing();
            }

            if ((DateTime.UtcNow - _lastAutoSaveRequest).TotalSeconds >= AutoSaveIntervalSeconds)
            {
                _lastAutoSaveRequest = DateTime.UtcNow;
                if (_peers.Count > 0)
                {
                    var peerList = _peers.Values.ToList();
                    var randomPeer = peerList[_rng.Next(peerList.Count)];

                    var writer = new NetDataWriter();
                    writer.Put((byte)PacketType.SaveRequest);
                    randomPeer.Send(writer.CopyData(), DeliveryMethod.ReliableOrdered);
                    Log($"[AUTO-SAVE] Requested background save from {GetPlayerName(randomPeer.Id)}", ConsoleColor.DarkCyan);
                }
            }

            if ((DateTime.UtcNow - _lastDiskFlush).TotalSeconds >= DiskFlushIntervalSeconds)
            {
                _lastDiskFlush = DateTime.UtcNow;
                PersistState();
            }

            // Check pending join timeouts (5 seconds) — fall back to cached save
            var timedOutPeers = _pendingJoinPeers.Where(kv => (DateTime.UtcNow - kv.Value).TotalSeconds > 5).Select(kv => kv.Key).ToList();
            foreach (var peer in timedOutPeers)
            {
                Log($"[JOIN] JIT save timeout for {GetPlayerName(peer.Id)} — falling back to cached save.", ConsoleColor.Yellow);
                if (_peers.ContainsKey(peer.Id))
                    SendUniverseToPeer(peer);
                _pendingJoinPeers.Remove(peer);
            }
        }

        public void Stop()
        {
            if (_server != null && _server.IsRunning)
            {
                Log("Stopping server...", ConsoleColor.Yellow);
                PersistState();
                _server.Stop();
            }
        }

        // ── Universe Time ───────────────────────────────────────

        private void AdvanceUniverseTime()
        {
            var now = DateTime.UtcNow;
            double elapsed = (now - _lastUtAdvance).TotalSeconds;
            _lastUtAdvance = now;

            if (!_utSeeded) return;

            double mult = WarpRates[Math.Clamp(_currentWarpIndex, 0, WarpRates.Length - 1)];
            _universeTime += elapsed * mult;
        }

        private void BroadcastTimeSync()
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.TimeSync);
            writer.Put(_universeTime);
            writer.Put(_currentWarpIndex);
            byte[] data = writer.CopyData();
            foreach (var peer in _peers.Values)
                peer.Send(data, DeliveryMethod.Sequenced);
        }

        private void HandleUTReport(NetPeer peer, NetPacketReader reader)
        {
            double ut = reader.GetDouble();
            if (!_utSeeded && ut > 0)
            {
                _universeTime = ut;
                _utSeeded = true;
                _lastUtAdvance = DateTime.UtcNow;
                Log($"[TIME] Universe clock seeded from {GetPlayerName(peer.Id)}: UT = {ut:F1}s", ConsoleColor.Green);
            }
        }

        // ── Connection Handlers ─────────────────────────────────

        private void OnConnectionRequest(ConnectionRequest request)
        {
            Log($"[CONNECT] Connection request from {request.RemoteEndPoint}", ConsoleColor.Gray);

            if (_players.Count >= _maxPlayers)
            {
                Log($"[CONNECT] REJECTED — server full ({_players.Count}/{_maxPlayers})", ConsoleColor.Red);
                request.Reject();
                return;
            }

            request.AcceptIfKey(ConnectionKey);
        }

        private void OnPeerConnected(NetPeer peer)
        {
            _peers[peer.Id] = peer;
            Log($"[CONNECT] Peer connected: id={peer.Id}, endpoint={peer.EndPoint}", ConsoleColor.Green);
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            string name = "Unknown";
            if (_players.TryGetValue(peer.Id, out var info))
            {
                name = info.SteamName;
                _players.Remove(peer.Id);
            }
            _peers.Remove(peer.Id);
            _pendingJoinPeers.Remove(peer);

            // Drop any half-finished transfers from this peer
            foreach (var key in _incomingTransfers.Keys.Where(k => k.peer == peer.Id).ToList())
                _incomingTransfers.Remove(key);

            Log($"[DISCONNECT] Player '{name}' (id={peer.Id}) disconnected. Reason: {disconnectInfo.Reason}", ConsoleColor.Yellow);

            // Release all control locks held by this peer.
            // Their vessels PERSIST in the vessel store — they stay in orbit for everyone.
            var released = _vesselLocks.Where(kv => kv.Value.PeerId == peer.Id).Select(kv => kv.Key).ToList();
            foreach (var guid in released)
            {
                _vesselLocks.Remove(guid);
                BroadcastControlUpdate(guid, -1, "", "");
                Log($"[LOCK] Released {guid} (owner disconnected — vessel persists)", ConsoleColor.DarkYellow);
            }

            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.PlayerLeave);
            writer.Put(peer.Id);
            writer.Put(name);
            SendToAll(writer.CopyData());

            if (_voteActive && _votes.ContainsKey(peer.Id))
            {
                _votes.Remove(peer.Id);
                EvaluateVote();
            }

            BroadcastPlayerList();
            Log($"[ROSTER] {_players.Count} player(s) remaining.", ConsoleColor.Gray);
        }

        // ── Packet Handling ─────────────────────────────────────

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            if (reader.AvailableBytes < 1)
            {
                reader.Recycle();
                return;
            }

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

                    case PacketType.UTReport:
                        HandleUTReport(peer, reader);
                        break;

                    case PacketType.VesselControlRequest:
                        HandleControlRequest(peer, reader);
                        break;
                    case PacketType.VesselControlRelease:
                        HandleControlRelease(peer, reader);
                        break;

                    case PacketType.SaveDataChunk:
                        HandleChunk(peer, reader);
                        break;
                    case PacketType.SaveDataComplete:
                        HandleChunkComplete(peer, reader);
                        break;

                    case PacketType.VesselState:
                        HandleVesselState(peer, reader);
                        break;

                    case PacketType.VesselTransform:
                        RelayToAllExcept(peer, packetType, reader, DeliveryMethod.Sequenced);
                        break;

                    case PacketType.VesselDestroyed:
                    case PacketType.VesselRecovered:
                        HandleVesselRemoved(peer, packetType, reader);
                        break;

                    case PacketType.TechUnlock:
                        RelayToAllExcept(peer, packetType, reader, DeliveryMethod.ReliableOrdered);
                        Log($"[TECH] {GetPlayerName(peer.Id)} unlocked a tech node — relayed", ConsoleColor.Magenta);
                        break;

                    case PacketType.ScienceEarned:
                        RelayToAllExcept(peer, packetType, reader, DeliveryMethod.ReliableOrdered);
                        Log($"[SCIENCE] {GetPlayerName(peer.Id)} earned science — relayed", ConsoleColor.Magenta);
                        break;

                    case PacketType.ChatMessage:
                        RelayToAllExcept(peer, packetType, reader, DeliveryMethod.ReliableOrdered);
                        break;

                    default:
                        RelayToAllExcept(peer, packetType, reader, deliveryMethod);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Handling packet {packetType} from peer {peer.Id}: {ex.Message}", ConsoleColor.Red);
            }
            finally
            {
                reader.Recycle();
            }
        }

        // ── Vessel State (store + relay) ────────────────────────

        private void HandleVesselState(NetPeer peer, NetPacketReader reader)
        {
            byte[] remaining = reader.GetRemainingBytes();

            // Peek the vessel guid (first field) so we can store the latest state per vessel
            try
            {
                var peekReader = new NetDataReader(remaining);
                string guid = peekReader.GetString();
                if (!string.IsNullOrEmpty(guid))
                {
                    if (!_vessels.TryGetValue(guid, out var record))
                    {
                        record = new VesselRecord { Guid = guid };
                        _vessels[guid] = record;
                    }
                    record.LastStateRaw = remaining;
                    record.LastUpdate = DateTime.UtcNow;
                    if (_players.TryGetValue(peer.Id, out var info))
                        record.OwnerPlayerId = info.PlayerId;
                }
            }
            catch { /* malformed packet — still relay */ }

            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.VesselState);
            writer.Put(remaining);
            byte[] data = writer.CopyData();
            foreach (var kvp in _peers)
            {
                if (kvp.Key != peer.Id)
                    kvp.Value.Send(data, DeliveryMethod.Sequenced);
            }
        }

        private void HandleVesselRemoved(NetPeer peer, PacketType type, NetPacketReader reader)
        {
            byte[] remaining = reader.GetRemainingBytes();

            try
            {
                var peekReader = new NetDataReader(remaining);
                string guid = peekReader.GetString();
                if (!string.IsNullOrEmpty(guid) && _vessels.Remove(guid))
                {
                    _vesselLocks.Remove(guid);
                    DeletePersistedVessel(guid);
                    Log($"[VESSEL] {guid} removed from store ({type})", ConsoleColor.Yellow);
                }
            }
            catch { }

            var writer = new NetDataWriter();
            writer.Put((byte)type);
            writer.Put(remaining);
            byte[] data = writer.CopyData();
            foreach (var kvp in _peers)
            {
                if (kvp.Key != peer.Id)
                    kvp.Value.Send(data, DeliveryMethod.ReliableOrdered);
            }
        }

        // ── Control Locks ───────────────────────────────────────

        private void HandleControlRequest(NetPeer peer, NetPacketReader reader)
        {
            string vesselGuid = reader.GetString();
            string playerId = reader.GetString();
            if (string.IsNullOrEmpty(vesselGuid)) return;

            if (_vesselLocks.TryGetValue(vesselGuid, out var existing) &&
                !string.Equals(existing.PlayerId, playerId, StringComparison.Ordinal))
            {
                // Denied — tell the requester who holds it
                SendControlUpdateTo(peer, vesselGuid, existing.PeerId, existing.PlayerId, existing.PlayerName);
                return;
            }

            string name = GetPlayerName(peer.Id);
            _vesselLocks[vesselGuid] = new VesselLock { PeerId = peer.Id, PlayerId = playerId, PlayerName = name };
            BroadcastControlUpdate(vesselGuid, peer.Id, playerId, name);
            Log($"[LOCK] {name} granted control of {vesselGuid}", ConsoleColor.DarkCyan);
        }

        private void HandleControlRelease(NetPeer peer, NetPacketReader reader)
        {
            string vesselGuid = reader.GetString();
            string playerId = reader.GetString();

            if (_vesselLocks.TryGetValue(vesselGuid, out var existing) &&
                string.Equals(existing.PlayerId, playerId, StringComparison.Ordinal))
            {
                _vesselLocks.Remove(vesselGuid);
                BroadcastControlUpdate(vesselGuid, -1, "", "");
                Log($"[LOCK] {GetPlayerName(peer.Id)} released control of {vesselGuid}", ConsoleColor.DarkCyan);
            }
        }

        private void BroadcastControlUpdate(string vesselGuid, int peerId, string playerId, string playerName)
        {
            var writer = BuildControlUpdate(vesselGuid, peerId, playerId, playerName);
            SendToAll(writer.CopyData());
        }

        private void SendControlUpdateTo(NetPeer peer, string vesselGuid, int peerId, string playerId, string playerName)
        {
            var writer = BuildControlUpdate(vesselGuid, peerId, playerId, playerName);
            peer.Send(writer.CopyData(), DeliveryMethod.ReliableOrdered);
        }

        private NetDataWriter BuildControlUpdate(string vesselGuid, int peerId, string playerId, string playerName)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.VesselControlUpdate);
            writer.Put(vesselGuid);
            writer.Put(peerId);
            writer.Put(playerId);
            writer.Put(playerName);
            return writer;
        }

        // ── Player Join ─────────────────────────────────────────

        private void HandlePlayerJoin(NetPeer peer, NetPacketReader reader)
        {
            int sentPeerId = reader.GetInt();
            string steamName = reader.GetString();
            string playerIdString = reader.GetString();

            bool isAdmin = _config.AdminSteamIDs.Contains(playerIdString);

            var playerInfo = new PlayerInfo
            {
                PeerId = peer.Id,
                SteamName = steamName,
                PlayerId = playerIdString,
                IsHost = false,
                Ping = peer.Ping,
                IsAdmin = isAdmin
            };
            _players[peer.Id] = playerInfo;

            Log(isAdmin
                ? $"[JOIN] ★ ADMIN joined: {steamName} (id={peer.Id}, playerId={playerIdString})"
                : $"[JOIN] Player joined: {steamName} (id={peer.Id}, playerId={playerIdString})",
                isAdmin ? ConsoleColor.Magenta : ConsoleColor.Green);

            // Notify all OTHER peers about the new player
            var joinWriter = new NetDataWriter();
            joinWriter.Put((byte)PacketType.PlayerJoin);
            joinWriter.Put(peer.Id);
            joinWriter.Put(steamName);
            joinWriter.Put(playerIdString);
            foreach (var kvp in _peers)
            {
                if (kvp.Key != peer.Id)
                    kvp.Value.Send(joinWriter.CopyData(), DeliveryMethod.ReliableOrdered);
            }

            SendPlayerListTo(peer);
            BroadcastPlayerList();

            // ── Universe bootstrap for the joiner ──
            if (_cachedSave == null || _cachedSave.Length == 0)
            {
                if (_players.Count == 1)
                {
                    // First player on an empty server: they seed the universe.
                    Log($"[SAVE] No cached save — requesting initial universe from {steamName}.", ConsoleColor.Cyan);
                    var writer = new NetDataWriter();
                    writer.Put((byte)PacketType.SaveRequest);
                    peer.Send(writer.CopyData(), DeliveryMethod.ReliableOrdered);
                }
                else
                {
                    // No save yet but other players exist — queue them for the next upload
                    _pendingJoinPeers[peer] = DateTime.UtcNow;
                    Log($"[JOIN] No cached save yet — {steamName} queued until first upload completes.", ConsoleColor.Yellow);
                }
                Log($"[ROSTER] {_players.Count} player(s) connected.", ConsoleColor.Gray);
                return;
            }

            // Just-In-Time save: if others are playing, get a fresh save first
            if (_players.Count > 1)
            {
                var uploader = _peers.Values.FirstOrDefault(p => p.Id != peer.Id);
                if (uploader != null)
                {
                    _pendingJoinPeers[peer] = DateTime.UtcNow;
                    Log($"[JOIN] {steamName} queued; requesting fresh save from {GetPlayerName(uploader.Id)}...", ConsoleColor.Cyan);
                    var writer = new NetDataWriter();
                    writer.Put((byte)PacketType.SaveRequest);
                    uploader.Send(writer.CopyData(), DeliveryMethod.ReliableOrdered);

                    Log($"[ROSTER] {_players.Count} player(s) connected.", ConsoleColor.Gray);
                    return;
                }
            }

            SendUniverseToPeer(peer);
            Log($"[ROSTER] {_players.Count} player(s) connected.", ConsoleColor.Gray);
        }

        // ── Chunked Transfers (save uploads + vessel spawns) ────

        private void HandleChunk(NetPeer peer, NetPacketReader reader)
        {
            int transferId = reader.GetInt();
            int targetPeerId = reader.GetInt();
            byte type = reader.GetByte();
            string vesselGuid = reader.GetString();
            int chunkIndex = reader.GetInt();
            int totalChunks = reader.GetInt();
            int payloadLen = reader.GetInt();
            byte[] payload = new byte[payloadLen];
            reader.GetBytes(payload, payloadLen);

            var key = (peer.Id, transferId);
            if (!_incomingTransfers.TryGetValue(key, out var transfer))
            {
                transfer = new IncomingTransfer { Type = type, VesselGuid = vesselGuid, SenderPeerId = peer.Id, TotalChunks = totalChunks };
                _incomingTransfers[key] = transfer;
            }
            transfer.Chunks[chunkIndex] = payload;

            // VesselSpawn chunks are relayed live so other clients build the vessel promptly
            if (type == 1)
            {
                var writer = new NetDataWriter();
                writer.Put((byte)PacketType.SaveDataChunk);
                writer.Put(transferId);
                writer.Put(targetPeerId);
                writer.Put(type);
                writer.Put(vesselGuid);
                writer.Put(chunkIndex);
                writer.Put(totalChunks);
                writer.Put(payloadLen);
                writer.Put(payload);
                byte[] data = writer.CopyData();
                foreach (var kvp in _peers)
                {
                    if (kvp.Key != peer.Id) kvp.Value.Send(data, DeliveryMethod.ReliableOrdered);
                }
            }
        }

        private void HandleChunkComplete(NetPeer peer, NetPacketReader reader)
        {
            int transferId = reader.GetInt();
            int targetPeerId = reader.GetInt();
            byte type = reader.GetByte();
            string vesselGuid = reader.GetString();
            int totalChunks = reader.GetInt();

            var key = (peer.Id, transferId);
            _incomingTransfers.TryGetValue(key, out var transfer);
            _incomingTransfers.Remove(key);

            byte[] assembled = null;
            if (transfer != null && transfer.Chunks.Count >= totalChunks)
            {
                int totalSize = transfer.Chunks.Values.Sum(b => b.Length);
                assembled = new byte[totalSize];
                int offset = 0;
                for (int i = 0; i < totalChunks; i++)
                {
                    if (transfer.Chunks.TryGetValue(i, out var chunk))
                    {
                        Array.Copy(chunk, 0, assembled, offset, chunk.Length);
                        offset += chunk.Length;
                    }
                }
            }

            if (type == 1) // VesselSpawn
            {
                // Relay the completion marker
                var writer = new NetDataWriter();
                writer.Put((byte)PacketType.SaveDataComplete);
                writer.Put(transferId);
                writer.Put(targetPeerId);
                writer.Put(type);
                writer.Put(vesselGuid);
                writer.Put(totalChunks);
                byte[] data = writer.CopyData();
                foreach (var kvp in _peers)
                {
                    if (kvp.Key != peer.Id) kvp.Value.Send(data, DeliveryMethod.ReliableOrdered);
                }

                // Store the assembly so late joiners get this vessel
                if (assembled != null && !string.IsNullOrEmpty(vesselGuid))
                {
                    if (!_vessels.TryGetValue(vesselGuid, out var record))
                    {
                        record = new VesselRecord { Guid = vesselGuid };
                        _vessels[vesselGuid] = record;
                    }
                    record.SpawnPayload = assembled;
                    record.LastUpdate = DateTime.UtcNow;
                    if (_players.TryGetValue(peer.Id, out var info))
                        record.OwnerPlayerId = info.PlayerId;
                    Log($"[VESSEL] Stored spawn payload for {vesselGuid} ({assembled.Length} bytes) — {_vessels.Count} vessel(s) in store", ConsoleColor.Green);
                }
                return;
            }

            // SaveData upload
            if (assembled == null)
            {
                Log($"[SAVE] ERROR: incomplete save upload from {GetPlayerName(peer.Id)} ({transfer?.Chunks.Count ?? 0}/{totalChunks} chunks) — discarded.", ConsoleColor.Red);
                return;
            }

            _cachedSave = assembled;
            _cachedSaveTime = DateTime.Now;
            Log($"[SAVE] ✓ Save cached: {assembled.Length} bytes ({totalChunks} chunks) at {_cachedSaveTime:HH:mm:ss}", ConsoleColor.Green);

            PersistState();

            // Flush pending joins with the fresh universe
            if (_pendingJoinPeers.Count > 0)
            {
                Log($"[SAVE] Flushing universe to {_pendingJoinPeers.Count} pending joiner(s).", ConsoleColor.Cyan);
                foreach (var p in _pendingJoinPeers.Keys.ToList())
                {
                    if (_peers.ContainsKey(p.Id))
                        SendUniverseToPeer(p);
                }
                _pendingJoinPeers.Clear();
            }
        }

        // ── Universe bootstrap: save + vessels + states + locks ─

        private void SendUniverseToPeer(NetPeer peer)
        {
            if (_cachedSave == null || _cachedSave.Length == 0)
            {
                Log($"[SAVE] ERROR: no cached save to send to peer {peer.Id}!", ConsoleColor.Red);
                return;
            }

            // 1. Base save
            SendChunkedPayload(peer, 0, "", _cachedSave);

            // 2. Every stored vessel assembly (vessels launched since the save, or persisted)
            int vesselCount = 0;
            foreach (var record in _vessels.Values)
            {
                if (record.SpawnPayload != null)
                {
                    SendChunkedPayload(peer, 1, record.Guid, record.SpawnPayload);
                    vesselCount++;
                }
            }

            // 3. Latest known state for each vessel (positions/orbits)
            int stateCount = 0;
            foreach (var record in _vessels.Values)
            {
                if (record.LastStateRaw != null)
                {
                    var writer = new NetDataWriter();
                    writer.Put((byte)PacketType.VesselState);
                    writer.Put(record.LastStateRaw);
                    peer.Send(writer.CopyData(), DeliveryMethod.ReliableOrdered);
                    stateCount++;
                }
            }

            // 4. Current control locks so the joiner knows what's taken
            foreach (var kv in _vesselLocks)
                SendControlUpdateTo(peer, kv.Key, kv.Value.PeerId, kv.Value.PlayerId, kv.Value.PlayerName);

            Log($"[JOIN] ✓ Universe sent to {GetPlayerName(peer.Id)}: save {_cachedSave.Length}B + {vesselCount} vessel(s) + {stateCount} state(s) + {_vesselLocks.Count} lock(s)", ConsoleColor.Green);
        }

        private void SendChunkedPayload(NetPeer peer, byte payloadType, string vesselGuid, byte[] payload)
        {
            const int ChunkSize = 32768;
            int totalChunks = (int)Math.Ceiling((double)payload.Length / ChunkSize);
            int transferId = _rng.Next();

            for (int i = 0; i < totalChunks; i++)
            {
                int chunkOffset = i * ChunkSize;
                int size = Math.Min(ChunkSize, payload.Length - chunkOffset);
                byte[] chunkPayload = new byte[size];
                Array.Copy(payload, chunkOffset, chunkPayload, 0, size);

                var writer = new NetDataWriter();
                writer.Put((byte)PacketType.SaveDataChunk);
                writer.Put(transferId);
                writer.Put(peer.Id);
                writer.Put(payloadType);
                writer.Put(vesselGuid ?? "");
                writer.Put(i);
                writer.Put(totalChunks);
                writer.Put(size);
                writer.Put(chunkPayload);
                peer.Send(writer.CopyData(), DeliveryMethod.ReliableOrdered);
            }

            var completeWriter = new NetDataWriter();
            completeWriter.Put((byte)PacketType.SaveDataComplete);
            completeWriter.Put(transferId);
            completeWriter.Put(peer.Id);
            completeWriter.Put(payloadType);
            completeWriter.Put(vesselGuid ?? "");
            completeWriter.Put(totalChunks);
            peer.Send(completeWriter.CopyData(), DeliveryMethod.ReliableOrdered);
        }

        // ── Persistence ─────────────────────────────────────────

        private void LoadPersistedState()
        {
            try
            {
                string savePath = Path.Combine(SaveDir, "MultiplayerCache.json");
                if (File.Exists(savePath))
                {
                    _cachedSave = File.ReadAllBytes(savePath);
                    _cachedSaveTime = File.GetLastWriteTime(savePath);
                    Log($"[SAVE] Loaded cached save from disk ({_cachedSave.Length} bytes, {_cachedSaveTime:HH:mm:ss})", ConsoleColor.Green);
                }

                string utPath = Path.Combine(SaveDir, "UniverseTime.txt");
                if (File.Exists(utPath) && double.TryParse(File.ReadAllText(utPath).Trim(), out double ut))
                {
                    _universeTime = ut;
                    _utSeeded = true;
                    Log($"[TIME] Restored universe time from disk: UT = {ut:F1}s", ConsoleColor.Green);
                }

                if (Directory.Exists(VesselDir))
                {
                    foreach (var file in Directory.GetFiles(VesselDir, "*.vessel"))
                    {
                        string guid = Path.GetFileNameWithoutExtension(file);
                        _vessels[guid] = new VesselRecord
                        {
                            Guid = guid,
                            SpawnPayload = File.ReadAllBytes(file),
                            LastUpdate = File.GetLastWriteTimeUtc(file)
                        };
                        string statePath = Path.Combine(VesselDir, guid + ".state");
                        if (File.Exists(statePath))
                            _vessels[guid].LastStateRaw = File.ReadAllBytes(statePath);
                    }
                    if (_vessels.Count > 0)
                        Log($"[VESSEL] Restored {_vessels.Count} vessel(s) from disk.", ConsoleColor.Green);
                }
            }
            catch (Exception ex)
            {
                Log($"[LOAD] Failed to load persisted state: {ex.Message}", ConsoleColor.Red);
            }
        }

        private void PersistState()
        {
            try
            {
                Directory.CreateDirectory(SaveDir);
                Directory.CreateDirectory(VesselDir);

                if (_cachedSave != null)
                    File.WriteAllBytes(Path.Combine(SaveDir, "MultiplayerCache.json"), _cachedSave);

                if (_utSeeded)
                    File.WriteAllText(Path.Combine(SaveDir, "UniverseTime.txt"), _universeTime.ToString("F3"));

                foreach (var record in _vessels.Values)
                {
                    if (record.SpawnPayload != null)
                        File.WriteAllBytes(Path.Combine(VesselDir, record.Guid + ".vessel"), record.SpawnPayload);
                    if (record.LastStateRaw != null)
                        File.WriteAllBytes(Path.Combine(VesselDir, record.Guid + ".state"), record.LastStateRaw);
                }
            }
            catch (Exception ex)
            {
                Log($"[SAVE] Failed to persist state: {ex.Message}", ConsoleColor.Red);
            }
        }

        private void DeletePersistedVessel(string guid)
        {
            try
            {
                // Guard against path traversal from a malicious guid
                if (guid.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
                string vesselPath = Path.Combine(VesselDir, guid + ".vessel");
                string statePath = Path.Combine(VesselDir, guid + ".state");
                if (File.Exists(vesselPath)) File.Delete(vesselPath);
                if (File.Exists(statePath)) File.Delete(statePath);
            }
            catch { }
        }

        // ── Warp Voting ─────────────────────────────────────────

        private void HandleWarpVoteRequest(NetPeer peer, NetPacketReader reader)
        {
            int requesterPeerId = reader.GetInt();
            string requesterName = reader.GetString();
            int targetWarpRate = reader.GetInt();

            // Dropping to 1x is always allowed instantly
            if (targetWarpRate <= 0)
            {
                BroadcastWarpApply(0);
                BroadcastWarpVoteResult(true);
                _voteActive = false;
                _votes.Clear();
                return;
            }

            if (_voteActive)
            {
                Log($"[WARP] Vote already active — ignoring request from {requesterName}", ConsoleColor.DarkYellow);
                return;
            }

            if (_players.Count <= 1)
            {
                Log($"[WARP] Solo player warp — auto-applying rate {targetWarpRate}", ConsoleColor.Cyan);
                BroadcastWarpApply(targetWarpRate);
                BroadcastWarpVoteResult(true);
                return;
            }

            _voteActive = true;
            _voteTargetRate = targetWarpRate;
            _voteStartTime = DateTime.UtcNow;
            _votes.Clear();

            Log($"[WARP] Vote started by {requesterName}: rate={targetWarpRate}, {_players.Count} voters", ConsoleColor.Cyan);

            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.WarpVoteRequest);
            writer.Put(peer.Id);
            writer.Put(requesterName);
            writer.Put(targetWarpRate);
            SendToAll(writer.CopyData());
        }

        private void HandleWarpVoteResponse(NetPeer peer, NetPacketReader reader)
        {
            int voterPeerId = reader.GetInt();
            bool approved = reader.GetBool();

            if (!_voteActive) return;

            string voterName = GetPlayerName(peer.Id);
            _votes[peer.Id] = approved;

            Log($"[WARP] Vote from {voterName}: {(approved ? "APPROVE ✓" : "DENY ✗")}", approved ? ConsoleColor.Green : ConsoleColor.Red);
            EvaluateVote();
        }

        private void EvaluateVote()
        {
            if (!_voteActive) return;

            if (_votes.Values.Any(v => !v))
            {
                Log($"[WARP] Vote DENIED — at least one player voted no", ConsoleColor.Red);
                BroadcastWarpVoteResult(false);
                _voteActive = false;
                _votes.Clear();
                return;
            }

            if (_votes.Count >= _players.Count)
            {
                Log($"[WARP] Vote PASSED — all {_players.Count} players approved", ConsoleColor.Green);
                BroadcastWarpApply(_voteTargetRate);
                BroadcastWarpVoteResult(true);
                _voteActive = false;
                _votes.Clear();
            }
        }

        // ── Relay Helper ────────────────────────────────────────

        private void RelayToAllExcept(NetPeer sender, PacketType type, NetPacketReader reader, DeliveryMethod delivery)
        {
            byte[] remaining = reader.GetRemainingBytes();
            var writer = new NetDataWriter();
            writer.Put((byte)type);
            writer.Put(remaining);
            byte[] data = writer.CopyData();

            foreach (var kvp in _peers)
            {
                if (kvp.Key != sender.Id)
                    kvp.Value.Send(data, delivery);
            }
        }

        // ── Broadcasting ────────────────────────────────────────

        private void SendToAll(byte[] data)
        {
            foreach (var peer in _peers.Values)
                peer.Send(data, DeliveryMethod.ReliableOrdered);
        }

        private void SendPlayerListTo(NetPeer peer)
        {
            var writer = BuildPlayerList();
            peer.Send(writer.CopyData(), DeliveryMethod.ReliableOrdered);
        }

        private void BroadcastPlayerList()
        {
            var writer = BuildPlayerList();
            SendToAll(writer.CopyData());
        }

        private NetDataWriter BuildPlayerList()
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.PlayerList);
            writer.Put(_players.Count);
            foreach (var p in _players.Values)
            {
                writer.Put(p.PeerId);
                writer.Put(p.SteamName);
                writer.Put(p.IsHost);
                writer.Put(_peers.TryGetValue(p.PeerId, out var pp) ? pp.Ping : 0);
                writer.Put(p.IsAdmin);
            }
            return writer;
        }

        private void BroadcastPing()
        {
            if (_players.Count == 0) return;

            foreach (var kvp in _peers)
            {
                if (_players.TryGetValue(kvp.Key, out var info))
                    info.Ping = kvp.Value.Ping;
            }

            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.PlayerPing);
            writer.Put(_players.Count);
            foreach (var p in _players.Values)
            {
                writer.Put(p.PeerId);
                writer.Put(p.Ping);
            }
            SendToAll(writer.CopyData());
        }

        private void BroadcastWarpVoteResult(bool passed)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.WarpVoteResult);
            writer.Put(passed);
            writer.Put(_voteTargetRate);
            SendToAll(writer.CopyData());
        }

        private void BroadcastWarpApply(int warpRateIndex)
        {
            _currentWarpIndex = Math.Clamp(warpRateIndex, 0, WarpRates.Length - 1);

            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.WarpApply);
            writer.Put(warpRateIndex);
            SendToAll(writer.CopyData());
        }

        // ── Helpers ─────────────────────────────────────────────

        private string GetPlayerName(int peerId)
        {
            return _players.TryGetValue(peerId, out var info) ? info.SteamName : $"Peer#{peerId}";
        }

        // ── Console Commands ────────────────────────────────────

        public void PrintPlayers()
        {
            if (_players.Count == 0)
            {
                Console.WriteLine("  No players connected.");
                return;
            }

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"  Connected Players ({_players.Count}/{_maxPlayers}):");
            Console.WriteLine("  ─────────────────────────────────────────");
            foreach (var p in _players.Values)
            {
                int ping = _peers.TryGetValue(p.PeerId, out var pp) ? pp.Ping : 0;
                Console.ForegroundColor = ping < 50 ? ConsoleColor.Green : ping < 150 ? ConsoleColor.Yellow : ConsoleColor.Red;
                Console.WriteLine($"  [{p.PeerId}] {p.SteamName}{(p.IsAdmin ? " [ADMIN]" : "")}  — {ping}ms");
            }
            Console.ResetColor();
        }

        public void PrintSaveStatus()
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  ─── Universe Status ───");
            if (_cachedSave != null && _cachedSave.Length > 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✓ Save cached: {_cachedSave.Length:N0} bytes (at {_cachedSaveTime:yyyy-MM-dd HH:mm:ss})");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ✗ No save cached.");
            }
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"  Universe time: {(_utSeeded ? $"{_universeTime:F1}s (warp index {_currentWarpIndex})" : "not seeded")}");
            Console.WriteLine($"  Vessels in store: {_vessels.Count}, active locks: {_vesselLocks.Count}");
            Console.ResetColor();
        }

        public void PrintVessels()
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"  ─── Vessel Store ({_vessels.Count}) ───");
            foreach (var v in _vessels.Values)
            {
                bool locked = _vesselLocks.TryGetValue(v.Guid, out var l);
                Console.WriteLine($"  {v.Guid}  spawn:{(v.SpawnPayload != null ? $"{v.SpawnPayload.Length}B" : "—")}  state:{(v.LastStateRaw != null ? "✓" : "—")}  {(locked ? $"LOCKED by {l.PlayerName}" : "")}");
            }
            Console.ResetColor();
        }

        public void KickPlayer(int peerId)
        {
            if (_peers.TryGetValue(peerId, out var peer))
            {
                Log($"[ADMIN] Kicking player: {GetPlayerName(peerId)} (id={peerId})", ConsoleColor.Yellow);
                peer.Disconnect();
            }
            else
            {
                Console.WriteLine($"  No peer with id {peerId}");
            }
        }

        // ── Logging ─────────────────────────────────────────────

        private void Log(string message, ConsoleColor color = ConsoleColor.Gray)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }
}
