using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LiteNetLib.Utils;
using UnityEngine;
using KSP.Game;
using KSP.IO;
using System.Reflection;

namespace KSP2MultiplayerRedux.Networking
{
    public class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Instance { get; private set; }

        public ServerHost Server { get; private set; }
        public ClientConnection Client { get; private set; }

        public bool IsServer => Server != null && Server.IsRunning;
        public bool IsClient => Client != null && Client.IsConnected;

        public string LocalPlayerName { get; set; }
        public string LocalPlayerIdString { get; private set; } = "Unknown";

        public Dictionary<int, PlayerInfo> Players { get; private set; } = new Dictionary<int, PlayerInfo>();

        public event Action<PlayerInfo> OnPlayerJoined;
        public event Action<PlayerInfo> OnPlayerLeft;
        public event Action OnPlayerListUpdated;
        
        public event Action<WarpVoteRequestPacket> OnWarpVoteRequested;
        public event Action<bool> OnWarpVoteCompleted;
        public event Action<int> OnWarpApply;

        public event Action<VesselTransformPacket> OnVesselTransformReceived;
        public event Action<VesselStatePacket> OnVesselStateReceived;
        public event Action<VesselDestroyedPacket> OnVesselDestroyedReceived;
        public event Action<VesselRecoveredPacket> OnVesselRecoveredReceived;
        public event Action<TechUnlockPacket> OnTechUnlockReceived;
        public event Action<ScienceEarnedPacket> OnScienceEarnedReceived;
        public event Action OnSaveLoadStarted;
        public event Action OnSaveLoadComplete;
        
        public event Action<string> OnRemoteTechUnlock;
        public event Action<float> OnRemoteScienceEarned;
        
        public event Action<byte[]> OnVesselSpawnPayloadReceived;

        public event Action<TimeSyncPacket> OnTimeSyncReceived;
        public event Action<VesselControlUpdatePacket> OnVesselControlUpdate;

        public void StartLargePayloadTransfer(PayloadType type, byte[] payloadData, string vesselGuid = "")
        {
            if (Client != null) Client.StartLargePayloadTransfer(-1, type, payloadData, vesselGuid);
            else if (Server != null) Server.StartLargePayloadTransfer(-1, type, payloadData, vesselGuid);
        }

        public void SendUTReport(double ut)
        {
            if (Client == null) return;
            var packet = new UTReportPacket { UT = ut };
            Client.SendSequenced(Client.SerializePacket(w => packet.Serialize(w)));
        }

        public void RequestVesselControl(string vesselGuid)
        {
            if (Client == null) return;
            var packet = new VesselControlRequestPacket { VesselGuid = vesselGuid, PlayerId = LocalPlayerIdString };
            Client.Send(Client.SerializePacket(w => packet.Serialize(w)));
        }

        public void ReleaseVesselControl(string vesselGuid)
        {
            if (Client == null) return;
            var packet = new VesselControlReleasePacket { VesselGuid = vesselGuid, PlayerId = LocalPlayerIdString };
            Client.Send(Client.SerializePacket(w => packet.Serialize(w)));
        }

        /// <summary>Client's RTT to the server in ms.</summary>
        public int ServerPingMs => Client?.PingMs ?? 0;

        private bool _voteActive;
        private int _voteTargetRate;
        private float _voteStartTime;
        private Dictionary<int, bool> _votes = new Dictionary<int, bool>();
        private const float VoteTimeoutSeconds = 30f;

        // In-flight incoming transfers keyed by TransferId so concurrent payloads can't interleave.
        private class IncomingTransfer
        {
            public PayloadType Type;
            public readonly Dictionary<int, byte[]> Chunks = new Dictionary<int, byte[]>();
            public int TotalChunks;
        }
        private readonly Dictionary<int, IncomingTransfer> _incomingTransfers = new Dictionary<int, IncomingTransfer>();

        // Static fields for save callback fallback (when delegate type doesn't match Action<byte[]>)
        private static byte[] _pendingSaveData;
        private static bool _pendingSaveDone;

        // Set right before we LoadGameFromBuffer as a JOINER, so the completion callback can run the
        // "make sure we're at the KSC" fallback. Must NOT fire for the uploader's SaveGameToMemory.
        private bool _joinLoadPending;

        /// <summary>
        /// Static helper method that can be converted to the KSP2 save callback delegate type.
        /// The delegate signature is: void OnLoadOrSaveCampaignFinishedCallback(LoadOrSaveCampaignTicket ticket, bool success)
        /// Since ticket is a reference type, we can use 'object' here to allow Delegate.CreateDelegate to bind to it via contravariance.
        /// </summary>
        public static void SaveCallbackHelper(object ticket, bool success)
        {
            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] SaveCallbackHelper fired! Success: {success}");
            
            byte[] data = null;
            if (success && ticket != null)
            {
                try
                {
                    // Use reflection to pull NewJsonBuffer off the ticket
                    var prop = ticket.GetType().GetProperty("NewJsonBuffer", BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null)
                    {
                        data = prop.GetValue(ticket) as byte[];
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] Extracted NewJsonBuffer! Length: {data?.Length ?? 0}");
                    }
                    else
                    {
                        KSP2MultiplayerReduxPlugin.Logger.LogError("[NetworkManager] Could not find NewJsonBuffer property on ticket!");
                    }
                }
                catch (Exception ex)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] Error extracting buffer from ticket: {ex.Message}");
                }
            }
            
            _pendingSaveData = data;
            _pendingSaveDone = true;
            
            Instance?.OnSaveLoadComplete?.Invoke();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == "com.rlabrecque.steamworks.net")
                    {
                        var friendsType = assembly.GetType("Steamworks.SteamFriends");
                        if (friendsType != null)
                        {
                            var method = friendsType.GetMethod("GetPersonaName", BindingFlags.Public | BindingFlags.Static);
                            if (method != null)
                            {
                                var name = method.Invoke(null, null) as string;
                                if (!string.IsNullOrEmpty(name)) LocalPlayerName = name;
                            }
                        }

                        var userType = assembly.GetType("Steamworks.SteamUser");
                        if (userType != null)
                        {
                            var method = userType.GetMethod("GetSteamID", BindingFlags.Public | BindingFlags.Static);
                            if (method != null)
                            {
                                var steamIdObj = method.Invoke(null, null);
                                if (steamIdObj != null)
                                {
                                    LocalPlayerIdString = steamIdObj.ToString();
                                }
                            }
                        }
                        break;
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(LocalPlayerName) || LocalPlayerName == "Unknown")
            {
                LocalPlayerName = Environment.UserName + "-" + UnityEngine.Random.Range(1000, 9999).ToString();
            }

            if (string.IsNullOrEmpty(LocalPlayerIdString) || LocalPlayerIdString == "Unknown")
            {
                LocalPlayerIdString = SystemInfo.deviceUniqueIdentifier;
                KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[NetworkManager] SteamID not found, falling back to Device ID: {LocalPlayerIdString}");
            }

            OnSaveLoadComplete += OnAnySaveLoadComplete;
        }

        private void OnAnySaveLoadComplete()
        {
            if (!_joinLoadPending) return;
            _joinLoadPending = false;
            StartCoroutine(EnsureAtKSCAfterJoin());
        }

        /// <summary>
        /// Belt-and-suspenders for the join flow. ScrubSaveForJoin sets StartingGameState to the KSC,
        /// which should make LoadGameFromBuffer land us there. If KSP2 ignores that and drops us into a
        /// flight scene anyway (i.e. into the uploader's vessel), use the game's own escape-menu
        /// transition — KSP.Game.GlobalEscapeMenu.TransitionToKSC() — to go to the Space Center.
        /// </summary>
        private IEnumerator EnsureAtKSCAfterJoin()
        {
            // Let the loaded scene settle before judging where we ended up.
            yield return new WaitForSeconds(3f);

            float deadline = Time.time + 10f;
            while (Time.time < deadline)
            {
                if (!KSP2MultiplayerReduxPlugin.IsFlightScene)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] Join complete — landed at {KSP2MultiplayerReduxPlugin.CurrentGameState} (not in a flight scene). Good.");
                    yield break;
                }

                KSP2MultiplayerReduxPlugin.Logger.LogWarning("[NetworkManager] Joined into a FLIGHT scene despite scrubbed save — forcing transition to the KSC.");
                bool ok = false;
                try
                {
                    var t = typeof(KSP.Game.GlobalEscapeMenu);
                    object menu = typeof(UnityEngine.Object).IsAssignableFrom(t)
                        ? UnityEngine.Object.FindObjectOfType(t, true)
                        : null;
                    var m = t.GetMethod("TransitionToKSC", BindingFlags.Public | BindingFlags.Instance);
                    if (menu != null && m != null)
                    {
                        m.Invoke(menu, null);
                        ok = true;
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo("[NetworkManager] GlobalEscapeMenu.TransitionToKSC() invoked.");
                    }
                    else
                    {
                        KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] TransitionToKSC fallback unavailable (menu={(menu != null)}, method={(m != null)}).");
                    }
                }
                catch (Exception ex)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] TransitionToKSC fallback threw: {ex.Message}");
                }

                if (!ok) yield break;
                yield return new WaitForSeconds(2f);   // give the transition a moment, then re-check
            }
        }

        private void Update()
        {
            Server?.Update();
            Client?.Update();

            if (IsServer && _voteActive)
            {
                if (Time.time - _voteStartTime > VoteTimeoutSeconds)
                {
                    BroadcastVoteResult(false);
                    ClearVote();
                }
            }
        }

        private void OnDestroy()
        {
            Disconnect();
            if (Instance == this) Instance = null;
        }

        public void StartHost(int port)
        {
            Server = new ServerHost(port);
            Server.Start();

            Server.OnPlayerJoined += ServerOnPlayerJoined;
            Server.OnPlayerLeft += ServerOnPlayerLeft;
            Server.OnWarpVoteReceived += ServerOnWarpVoteReceived;
            Server.OnVesselTransformReceived += (pkt) => OnVesselTransformReceived?.Invoke(pkt);
            Server.OnTechUnlockReceived += (pkt) => KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] Server relayed tech: {pkt.NodeId}");
            Server.OnScienceEarnedReceived += (pkt) => KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] Server relayed science: {pkt.Amount}");

            OnPlayerListUpdated?.Invoke();

            ConnectClient("127.0.0.1", port);
        }

        public void ConnectClient(string ip, int port)
        {
            // Prevent join-spam: if already connected, ignore
            if (IsClient)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogWarning("[NetworkManager] ConnectClient called but already connected! Ignoring.");
                return;
            }

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] ConnectClient({ip}, {port}) — LocalPlayerName='{LocalPlayerName}'");
            Client = new ClientConnection(ip, port, LocalPlayerName);

            Client.OnPlayerListReceived += ClientOnPlayerListReceived;
            Client.OnPlayerJoined += ClientOnPlayerJoined;
            Client.OnPlayerLeft += ClientOnPlayerLeft;
            Client.OnPingUpdated += ClientOnPingUpdated;
            Client.OnWarpVoteRequested += ClientOnWarpVoteRequested;
            Client.OnWarpVoteResult += ClientOnWarpVoteResult;
            Client.OnWarpApply += ClientOnWarpApply;
            Client.OnDisconnected += ClientOnDisconnected;

            Client.OnVesselTransformReceived += (pkt) => OnVesselTransformReceived?.Invoke(pkt);
            Client.OnVesselStateReceived += (pkt) => OnVesselStateReceived?.Invoke(pkt);
            Client.OnVesselDestroyedReceived += (pkt) => OnVesselDestroyedReceived?.Invoke(pkt);
            Client.OnVesselRecoveredReceived += (pkt) => OnVesselRecoveredReceived?.Invoke(pkt);
            Client.OnSaveChunkReceived += ClientOnSaveChunkReceived;
            Client.OnSaveComplete += ClientOnSaveComplete;
            Client.OnSaveRequested += ClientOnSaveRequested;
            Client.OnTechUnlockReceived += (pkt) => { OnRemoteTechUnlock?.Invoke(pkt.NodeId); OnTechUnlockReceived?.Invoke(pkt); };
            Client.OnScienceEarnedReceived += (pkt) => { OnRemoteScienceEarned?.Invoke(pkt.Amount); OnScienceEarnedReceived?.Invoke(pkt); };
            Client.OnTimeSyncReceived += (pkt) => OnTimeSyncReceived?.Invoke(pkt);
            Client.OnVesselControlUpdateReceived += (pkt) => OnVesselControlUpdate?.Invoke(pkt);
        }

        public void Disconnect()
        {
            if (Client != null)
            {
                Client.OnPlayerListReceived -= ClientOnPlayerListReceived;
                Client.OnPlayerJoined -= ClientOnPlayerJoined;
                Client.OnPlayerLeft -= ClientOnPlayerLeft;
                Client.OnPingUpdated -= ClientOnPingUpdated;
                Client.OnWarpVoteRequested -= ClientOnWarpVoteRequested;
                Client.OnWarpVoteResult -= ClientOnWarpVoteResult;
                Client.OnWarpApply -= ClientOnWarpApply;
                Client.OnDisconnected -= ClientOnDisconnected;
                
                Client.OnSaveChunkReceived -= ClientOnSaveChunkReceived;
                Client.OnSaveComplete -= ClientOnSaveComplete;
                
                Client.Disconnect();
                Client = null;
            }

            if (Server != null)
            {
                Server.OnPlayerJoined -= ServerOnPlayerJoined;
                Server.OnPlayerLeft -= ServerOnPlayerLeft;
                Server.OnWarpVoteReceived -= ServerOnWarpVoteReceived;
                Server.Stop();
                Server = null;
            }

            Players.Clear();
            _incomingTransfers.Clear();
            ClearVote();
            OnPlayerListUpdated?.Invoke();
        }

        public void BroadcastVesselTransform(VesselTransformPacket packet)
        {
            if (Client == null) return;
            var data = Client.SerializePacket(w => packet.Serialize(w));
            Client.SendUnreliable(data);
        }

        public void BroadcastVesselState(VesselStatePacket packet)
        {
            if (Client == null) return;
            var data = Client.SerializePacket(w => packet.Serialize(w));
            Client.SendSequenced(data);
        }

        public void BroadcastVesselDestroyed(VesselDestroyedPacket packet)
        {
            if (Client == null) return;
            var data = Client.SerializePacket(w => packet.Serialize(w));
            Client.Send(data);
        }

        public void BroadcastVesselRecovered(VesselRecoveredPacket packet)
        {
            if (Client == null) return;
            var data = Client.SerializePacket(w => packet.Serialize(w));
            Client.Send(data);
        }

        public void BroadcastTechUnlock(TechUnlockPacket packet)
        {
            if (Client == null) return;
            var data = Client.SerializePacket(w => packet.Serialize(w));
            Client.Send(data);
        }

        public void BroadcastTechUnlock(string nodeId)
        {
            BroadcastTechUnlock(new TechUnlockPacket { NodeId = nodeId });
        }

        public void BroadcastScienceEarned(ScienceEarnedPacket packet)
        {
            if (Client == null) return;
            var data = Client.SerializePacket(w => packet.Serialize(w));
            Client.Send(data);
        }

        public void BroadcastScienceEarned(float amount)
        {
            BroadcastScienceEarned(new ScienceEarnedPacket { Amount = amount, ExperimentID = "", ResearchLocationID = "", ReportType = 1 });
        }

        public void RequestWarpVote(int targetRate)
        {
            if (Client == null) return;
            var packet = new WarpVoteRequestPacket
            {
                RequesterPeerId = -1,
                RequesterName = LocalPlayerName,
                TargetWarpRate = targetRate
            };
            byte[] data = Client.SerializePacket(w => packet.Serialize(w));
            Client.Send(data);
        }

        public void CastWarpVote(bool approve)
        {
            if (Client == null) return;
            var packet = new WarpVoteResponsePacket { VoterPeerId = -1, Approved = approve };
            byte[] data = Client.SerializePacket(w => packet.Serialize(w));
            Client.Send(data);
        }

        private void ServerOnPlayerJoined(int peerId, string steamName)
        {
            var info = new PlayerInfo(peerId, steamName, isHost: false);
            Players[peerId] = info;

            OnPlayerJoined?.Invoke(info);
            OnPlayerListUpdated?.Invoke();

            // In Listen Server mode (we ARE the server), push the save directly
            if (IsServer)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] ServerOnPlayerJoined: {steamName} (id={peerId}) — we are the Listen Server, starting save transfer.");
                StartCoroutine(SaveAndTransferToNewPlayer(peerId));
            }
        }



        private void ClientOnSaveRequested()
        {
            KSP2MultiplayerReduxPlugin.Logger.LogInfo("[NetworkManager] Server requested save upload! Starting save serialization...");
            StartCoroutine(SaveAndTransferToNewPlayer(-1)); // -1 = upload to server cache, not to a specific peer
        }

        private IEnumerator SaveAndTransferToNewPlayer(int targetPeerId)
        {
            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] === SAVE TRANSFER START === target={targetPeerId}");

            // Reset static save callback state
            _pendingSaveData = null;
            _pendingSaveDone = false;
            bool setupFailed = false;

            // Phase 1: Setup and invoke (no yield allowed in try/catch)
            try
            {
                var gameInstance = GameManager.Instance?.Game;
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] GameManager.Instance = {(GameManager.Instance != null ? "OK" : "NULL")}");
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] Game = {(gameInstance != null ? "OK" : "NULL")}");

                var saveManager = gameInstance?.SaveLoadManager;
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] SaveLoadManager = {(saveManager != null ? "OK" : "NULL")}");

                if (saveManager == null)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogError("[NetworkManager] SaveLoadManager is NULL — cannot save game!");
                    setupFailed = true;
                }
                else
                {
                    var method = saveManager.GetType().GetMethod("SaveGameToMemory", BindingFlags.Public | BindingFlags.Instance);
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] SaveGameToMemory method = {(method != null ? "FOUND" : "NOT FOUND")}");

                    if (method == null)
                    {
                        var allMethods = saveManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                        var saveRelated = allMethods.Where(m => m.Name.Contains("Save") || m.Name.Contains("Load")).Select(m => m.Name);
                        KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] Could not find SaveGameToMemory! Available: {string.Join(", ", saveRelated)}");
                        setupFailed = true;
                    }
                    else
                    {
                        var parameters = method.GetParameters();
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] Method has {parameters.Length} params: {string.Join(", ", parameters.Select(p => $"{p.ParameterType.Name} {p.Name}"))}");

                        if (parameters.Length != 2)
                        {
                            KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] Unexpected param count: {parameters.Length}");
                            setupFailed = true;
                        }
                        else
                        {
                            var callbackType = parameters[1].ParameterType;
                            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] Callback type: {callbackType.FullName}");

                            // Use our static helper method which takes byte[] — create a delegate of the correct type from it
                            var helperMethod = typeof(NetworkManager).GetMethod("SaveCallbackHelper", BindingFlags.Public | BindingFlags.Static);
                            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] SaveCallbackHelper method = {(helperMethod != null ? "FOUND" : "NOT FOUND")}");

                            if (helperMethod == null)
                            {
                                KSP2MultiplayerReduxPlugin.Logger.LogError("[NetworkManager] SaveCallbackHelper not found!");
                                setupFailed = true;
                            }
                            else
                            {
                                var typedDelegate = Delegate.CreateDelegate(callbackType, helperMethod);
                                KSP2MultiplayerReduxPlugin.Logger.LogInfo("[NetworkManager] Invoking SaveGameToMemory...");
                                method.Invoke(saveManager, new object[] { SaveJsonFormatting.None, typedDelegate });
                                KSP2MultiplayerReduxPlugin.Logger.LogInfo("[NetworkManager] SaveGameToMemory invoked! Waiting for callback...");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] EXCEPTION during save setup: {ex.GetType().Name}: {ex.Message}");
                KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] Stack: {ex.StackTrace}");
                setupFailed = true;
            }

            if (setupFailed)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError("[NetworkManager] === SAVE TRANSFER FAILED (setup) ===");
                yield break;
            }

            // Phase 2: Wait for the callback (yield is safe outside try/catch)
            float timeout = 30f;
            float elapsed = 0f;
            while (!_pendingSaveDone && elapsed < timeout)
            {
                elapsed += UnityEngine.Time.deltaTime;
                yield return null;
            }

            if (!_pendingSaveDone)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] Save callback TIMED OUT after {timeout}s!");
                yield break;
            }

            byte[] saveData = _pendingSaveData;

            if (saveData != null && saveData.Length > 0)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] Save serialized successfully: {saveData.Length} bytes");

                if (Server != null)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] Sending via Listen Server to peer {targetPeerId}");
                    Server.StartLargePayloadTransfer(targetPeerId, PayloadType.SaveData, saveData);
                }
                else if (Client != null)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] Sending via Client to server (target={targetPeerId})");
                    Client.StartLargePayloadTransfer(targetPeerId, PayloadType.SaveData, saveData);
                }
                else
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogError("[NetworkManager] Neither Server nor Client available to send save!");
                }

                KSP2MultiplayerReduxPlugin.Logger.LogInfo("[NetworkManager] === SAVE TRANSFER COMPLETE ===");
            }
            else
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] FAILED to serialize save! saveData={(saveData == null ? "null" : $"{saveData.Length} bytes")}");
            }
        }

        private void ServerOnPlayerLeft(int peerId, string steamName)
        {
            if (Players.TryGetValue(peerId, out var info)) Players.Remove(peerId);
            else info = new PlayerInfo(peerId, steamName);

            if (_voteActive && _votes.ContainsKey(peerId))
            {
                _votes.Remove(peerId);
                EvaluateVote();
            }

            OnPlayerLeft?.Invoke(info);
            OnPlayerListUpdated?.Invoke();
        }

        private void ServerOnWarpVoteReceived(int voterPeerId, bool approved)
        {
            if (!_voteActive) return;
            _votes[voterPeerId] = approved;
            if (Players.TryGetValue(voterPeerId, out var info)) info.WarpVote = approved;
            EvaluateVote();
        }

        private void ClientOnPlayerListReceived(PlayerListPacket packet)
        {
            Players.Clear();

            foreach (var entry in packet.Players)
            {
                Players[entry.PeerId] = new PlayerInfo(entry.PeerId, entry.SteamName, isHost: entry.IsHost, isAdmin: entry.IsAdmin) { Ping = entry.Ping };
            }

            OnPlayerListUpdated?.Invoke();
        }

        private void ClientOnPlayerJoined(PlayerJoinPacket packet)
        {
            if (IsServer) return;
            if (!Players.ContainsKey(packet.PeerId))
            {
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] ClientOnPlayerJoined: {packet.SteamName} (id={packet.PeerId})");
                var info = new PlayerInfo(packet.PeerId, packet.SteamName, packet.PlayerIdString, isHost: false);
                Players[packet.PeerId] = info;
                OnPlayerJoined?.Invoke(info);
                OnPlayerListUpdated?.Invoke();
                // Note: Save transfer is now handled by the server via SaveRequest packets
            }
        }

        private void ClientOnPlayerLeft(int peerId, string steamName)
        {
            if (IsServer) return;
            if (Players.TryGetValue(peerId, out var info))
            {
                Players.Remove(peerId);
                OnPlayerLeft?.Invoke(info);
                OnPlayerListUpdated?.Invoke();
            }
        }

        private void ClientOnPingUpdated(PlayerPingPacket packet)
        {
            foreach (var entry in packet.Pings)
            {
                if (Players.TryGetValue(entry.PeerId, out var info)) info.Ping = entry.PingMs;
            }
            OnPlayerListUpdated?.Invoke();
        }

        private void ClientOnWarpVoteRequested(WarpVoteRequestPacket packet)
        {
            if (IsServer)
            {
                // Dropping to 1x warp (rate index 0) is always approved instantly — no vote needed
                if (packet.TargetWarpRate <= 0)
                {
                    BroadcastWarpApply(0);
                    BroadcastVoteResult(true);
                    ClearVote();
                    return;
                }

                if (_voteActive) return;
                if (Players.Count <= 1)
                {
                    BroadcastWarpApply(packet.TargetWarpRate);
                    BroadcastVoteResult(true);
                    return;
                }
                _voteActive = true;
                _voteTargetRate = packet.TargetWarpRate;
                _voteStartTime = Time.time;
                _votes.Clear();
                foreach (var player in Players.Values) player.WarpVote = null;
            }
            OnWarpVoteRequested?.Invoke(packet);
        }

        private void ClientOnWarpVoteResult(WarpVoteResultPacket packet)
        {
            OnWarpVoteCompleted?.Invoke(packet.Passed);
        }

        private void ClientOnWarpApply(WarpApplyPacket packet)
        {
            OnWarpApply?.Invoke(packet.WarpRateIndex);
        }

        private void ClientOnDisconnected()
        {
            if (!IsServer)
            {
                Players.Clear();
                OnPlayerListUpdated?.Invoke();
            }
        }

        public event Action<float> OnSaveTransferProgress;

        private void ClientOnSaveChunkReceived(SaveDataChunkPacket packet)
        {
            if (!_incomingTransfers.TryGetValue(packet.TransferId, out var transfer))
            {
                transfer = new IncomingTransfer { Type = packet.Type, TotalChunks = packet.TotalChunks };
                _incomingTransfers[packet.TransferId] = transfer;
                if (packet.Type == PayloadType.SaveData) OnSaveLoadStarted?.Invoke();
            }

            transfer.Chunks[packet.ChunkIndex] = packet.Payload;
            float progress = (float)transfer.Chunks.Count / packet.TotalChunks;
            if (packet.Type == PayloadType.SaveData) OnSaveTransferProgress?.Invoke(progress);

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] {packet.Type} transfer #{packet.TransferId} chunk {packet.ChunkIndex + 1}/{packet.TotalChunks} received");
        }

        private void ClientOnSaveComplete(SaveDataCompletePacket packet)
        {
            int totalChunks = packet.TotalChunks;
            PayloadType type = packet.Type;

            if (!_incomingTransfers.TryGetValue(packet.TransferId, out var transfer))
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] SaveDataComplete for unknown transfer #{packet.TransferId} — ignoring.");
                return;
            }
            _incomingTransfers.Remove(packet.TransferId);

            if (transfer.Chunks.Count < totalChunks)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] Transfer #{packet.TransferId} incomplete: {transfer.Chunks.Count}/{totalChunks} chunks — discarding to avoid loading corrupted data.");
                return;
            }

            int totalSize = transfer.Chunks.Values.Sum(b => b.Length);
            byte[] fullBuffer = new byte[totalSize];
            int offset = 0;

            for (int i = 0; i < totalChunks; i++)
            {
                if (transfer.Chunks.TryGetValue(i, out var chunk))
                {
                    Array.Copy(chunk, 0, fullBuffer, offset, chunk.Length);
                    offset += chunk.Length;
                }
            }

            // --- SCRUB SAVE FOR JOIN (LunaMP "join at the Space Center" behaviour) ---
            if (type == PayloadType.SaveData)
            {
                try
                {
                    string json = System.Text.Encoding.UTF8.GetString(fullBuffer);
                    json = ScrubSaveForJoin(json, out string scrubReport);
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] Save scrubbed for join: {scrubReport}");
                    fullBuffer = System.Text.Encoding.UTF8.GetBytes(json);
                    totalSize = fullBuffer.Length;
                }
                catch (Exception ex)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] Failed to scrub save for join: {ex.Message}");
                }
            }

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] {type} download complete: {totalSize} bytes");

            if (type == PayloadType.SaveData)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[NetworkManager] Loading save game...");
                try
                {
                    var saveManager = GameManager.Instance?.Game?.SaveLoadManager;
                    if (saveManager != null)
                    {
                        var method = saveManager.GetType().GetMethod("LoadGameFromBuffer", BindingFlags.Public | BindingFlags.Instance);
                        if (method != null)
                        {
                            var parameters = method.GetParameters();
                            var helperMethod = typeof(NetworkManager).GetMethod("SaveCallbackHelper", BindingFlags.Public | BindingFlags.Static);
                            var callbackType = parameters[1].ParameterType;
                            var typedDelegate = Delegate.CreateDelegate(callbackType, helperMethod);

                            _joinLoadPending = true;   // we are a JOINER loading the shared universe
                            if (parameters.Length == 2)
                            {
                                method.Invoke(saveManager, new object[] { fullBuffer, typedDelegate });
                            }
                            else if (parameters.Length == 3)
                            {
                                method.Invoke(saveManager, new object[] { fullBuffer, typedDelegate, null });
                            }
                            else
                            {
                                KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] Unexpected parameters: {parameters.Length}");
                                _pendingSaveDone = true;
                                Instance?.OnSaveLoadComplete?.Invoke();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogError($"[NetworkManager] Failed to load save: {ex.Message}");
                    _pendingSaveDone = true;
                    Instance?.OnSaveLoadComplete?.Invoke();
                }
            }
            else if (type == PayloadType.VesselSpawn)
            {
                OnVesselSpawnPayloadReceived?.Invoke(fullBuffer);
            }
        }

        /// <summary>
        /// Rewrites a received save so the joining player lands at the Kerbal Space Center with NO
        /// active vessel — instead of being dropped into whatever the uploader was flying (the
        /// "player 2 picks up where player 1 was" hijack).
        ///
        /// Field names verified against real KSP2 saves. A save taken in flight contains:
        ///   "StartingGameState": "FlightView"        <- the scene KSP2 loads into (this was never scrubbed)
        ///   "HistoricalGameState": "Map3DView"
        ///   "ActiveVesselName": "Example-1"
        ///   "HostPlayerActiveVesselName": "Example-1"
        ///   "ActiveVesselGuid": { "Guid": "e927a01d-...", "DebugName": null }
        /// A save taken at the KSC/VAB has StartingGameState "KerbalSpaceCenter"/"VehicleAssemblyBuilder",
        /// empty names and an all-zero Guid — that is the state we produce here.
        /// The MP cache is minified (SaveJsonFormatting.None) while disk saves are pretty-printed, so every
        /// pattern uses \s* around ':' and '{' to match both.
        /// </summary>
        public static string ScrubSaveForJoin(string json, out string report)
        {
            int n1 = 0, n2 = 0, n3 = 0, n4 = 0, n5 = 0;

            // Scene to load into: force the Space Center (LunaMP behaviour).
            json = System.Text.RegularExpressions.Regex.Replace(json, @"\""StartingGameState\""\s*:\s*\""[^\""]*\""",
                m => { n1++; return "\"StartingGameState\":\"KerbalSpaceCenter\""; });
            // "Back to previous state" target: don't let it bounce into someone's flight.
            json = System.Text.RegularExpressions.Regex.Replace(json, @"\""HistoricalGameState\""\s*:\s*\""[^\""]*\""",
                m => { n2++; return "\"HistoricalGameState\":\"KerbalSpaceCenter\""; });

            // No active vessel.
            json = System.Text.RegularExpressions.Regex.Replace(json, @"\""ActiveVesselName\""\s*:\s*\""[^\""]*\""",
                m => { n3++; return "\"ActiveVesselName\":\"\""; });
            json = System.Text.RegularExpressions.Regex.Replace(json, @"\""HostPlayerActiveVesselName\""\s*:\s*\""[^\""]*\""",
                m => { n4++; return "\"HostPlayerActiveVesselName\":\"\""; });
            json = System.Text.RegularExpressions.Regex.Replace(json, @"\""ActiveVesselGuid\""\s*:\s*\{\s*\""Guid\""\s*:\s*\""[^\""]*\""",
                m => { n5++; return "\"ActiveVesselGuid\":{\"Guid\":\"00000000-0000-0000-0000-000000000000\""; });

            report = $"StartingGameState->KSC x{n1}, HistoricalGameState->KSC x{n2}, ActiveVesselName x{n3}, HostPlayerActiveVesselName x{n4}, ActiveVesselGuid x{n5}";
            if (n1 == 0)
                KSP2MultiplayerReduxPlugin.Logger.LogWarning("[NetworkManager] ScrubSaveForJoin: StartingGameState NOT found in save — joiner may still load into the uploader's scene!");
            return json;
        }

        private int GetVoterCount() => Players.Count;

        private void EvaluateVote()
        {
            if (!_voteActive) return;
            int totalVoters = GetVoterCount();

            if (_votes.Values.Any(v => !v))
            {
                BroadcastVoteResult(false);
                ClearVote();
                return;
            }

            if (_votes.Count >= totalVoters)
            {
                BroadcastWarpApply(_voteTargetRate);
                BroadcastVoteResult(true);
                ClearVote();
            }
        }

        private void BroadcastVoteResult(bool passed)
        {
            if (Server == null) return;
            var packet = new WarpVoteResultPacket { Passed = passed, TargetWarpRate = _voteTargetRate };
            byte[] data = Server.SerializePacket(w => packet.Serialize(w));
            Server.SendToAll(data);
        }

        private void BroadcastWarpApply(int rateIndex)
        {
            if (Server == null) return;
            var packet = new WarpApplyPacket { WarpRateIndex = rateIndex };
            byte[] data = Server.SerializePacket(w => packet.Serialize(w));
            Server.SendToAll(data);
        }

        private void ClearVote()
        {
            _voteActive = false;
            _voteTargetRate = 0;
            _voteStartTime = 0f;
            _votes.Clear();
            foreach (var player in Players.Values) player.WarpVote = null;
        }
    }
}
