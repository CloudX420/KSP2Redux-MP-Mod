using System;
using UnityEngine;
using KSP.Game;
using KSP.Messages;
using KSP2MultiplayerRedux.Networking;

namespace KSP2MultiplayerRedux.Sync
{
    /// <summary>
    /// Handles vessel lifecycle events (destruction, recovery) and syncs them across clients.
    /// </summary>
    public class VesselLifecycleManager : MonoBehaviour
    {
        private bool _suppressRemoteEvents = false;
        private bool _hooked = false;

        private void Start()
        {
            KSP2MultiplayerReduxPlugin.Logger.LogInfo("[VesselLifecycle] Initialized.");
        }

        private void Update()
        {
            if (_hooked) return;
            if (NetworkManager.Instance == null) return;
            if (!NetworkManager.Instance.IsClient && !NetworkManager.Instance.IsServer) return;

            // Hook MessageCenter events
            var messages = GameManager.Instance?.Game?.Messages;
            if (messages != null)
            {
                messages.PersistentSubscribe<VesselDestroyedMessage>(OnLocalVesselDestroyed);
                messages.PersistentSubscribe<VesselRecoveredMessage>(OnLocalVesselRecovered);
                messages.PersistentSubscribe<GameStateChangedMessage>(OnGameStateChanged);
            }

            // Hook network events
            NetworkManager.Instance.OnVesselDestroyedReceived += OnRemoteVesselDestroyed;
            NetworkManager.Instance.OnVesselRecoveredReceived += OnRemoteVesselRecovered;

            _hooked = true;
            KSP2MultiplayerReduxPlugin.Logger.LogInfo("[VesselLifecycle] Hooked into events.");
        }

        private void OnLocalVesselDestroyed(MessageCenterMessage msg)
        {
            if (_suppressRemoteEvents) return;
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsClient) return;

            var destroyedMsg = msg as VesselDestroyedMessage;
            if (destroyedMsg == null) return;

            string vesselGuid = destroyedMsg.Guid.ToString();
            if (string.IsNullOrEmpty(vesselGuid)) return;

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselLifecycle] Local vessel destroyed: {vesselGuid}");

            var packet = new VesselDestroyedPacket
            {
                VesselGuid = vesselGuid,
                OwnerName = NetworkManager.Instance.LocalPlayerName ?? ""
            };

            NetworkManager.Instance.BroadcastVesselDestroyed(packet);
        }

        private void OnLocalVesselRecovered(MessageCenterMessage msg)
        {
            if (_suppressRemoteEvents) return;
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsClient) return;

            var recoveredMsg = msg as VesselRecoveredMessage;
            if (recoveredMsg == null) return;

            string vesselGuid = recoveredMsg.VesselID.ToString();
            if (string.IsNullOrEmpty(vesselGuid)) return;

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselLifecycle] Local vessel recovered: {vesselGuid}");

            var packet = new VesselRecoveredPacket
            {
                VesselGuid = vesselGuid,
                OwnerName = NetworkManager.Instance.LocalPlayerName ?? ""
            };

            NetworkManager.Instance.BroadcastVesselRecovered(packet);
        }

        private void OnGameStateChanged(MessageCenterMessage msg)
        {
            if (_suppressRemoteEvents) return;
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsClient) return;

            var gsMsg = msg as GameStateChangedMessage;
            if (gsMsg == null) return;

            KSP2MultiplayerReduxPlugin.CurrentGameState = gsMsg.CurrentState;

            if (gsMsg.CurrentState == GameState.VehicleAssemblyBuilder || gsMsg.CurrentState == GameState.MainMenu)
            {
                // Leaving flight scene (reverting or exiting). Only broadcast destruction if the
                // vessel never really "existed" in the shared universe (still on the pad / in atmo
                // revert range). Orbiting/escaping vessels persist for everyone.
                var activeVessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel(true);
                if (activeVessel != null)
                {
                    var situation = activeVessel.Situation;
                    if (situation == KSP.Sim.impl.VesselSituations.Orbiting ||
                        situation == KSP.Sim.impl.VesselSituations.Escaping)
                    {
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo("[VesselLifecycle] Leaving flight with vessel in orbit — vessel persists in shared universe.");
                        return;
                    }
                    string guidStr = activeVessel.Guid.ToString();
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselLifecycle] Reverting/Leaving Flight. Broadcasting destruction of active vessel {guidStr}");
                    
                    var packet = new VesselDestroyedPacket
                    {
                        VesselGuid = guidStr,
                        OwnerName = NetworkManager.Instance.LocalPlayerName ?? ""
                    };
                    NetworkManager.Instance.BroadcastVesselDestroyed(packet);
                }
            }
        }

        private void OnRemoteVesselDestroyed(VesselDestroyedPacket packet)
        {
            if (string.Equals(packet.OwnerName, NetworkManager.Instance?.LocalPlayerName, StringComparison.Ordinal)) return;
            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselLifecycle] Remote vessel destroyed: {packet.VesselGuid} by {packet.OwnerName}");

            DestroyRemoteVessel(packet.VesselGuid);
        }

        private void OnRemoteVesselRecovered(VesselRecoveredPacket packet)
        {
            if (string.Equals(packet.OwnerName, NetworkManager.Instance?.LocalPlayerName, StringComparison.Ordinal)) return;
            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselLifecycle] Remote vessel recovered: {packet.VesselGuid} by {packet.OwnerName}");

            DestroyRemoteVessel(packet.VesselGuid);
        }

        private void DestroyRemoteVessel(string guidStr)
        {
            // Clean up tracking in VesselSync
            var vesselSync = FindAnyObjectByType<VesselSyncManager>();
            vesselSync?.RemoveVessel(guidStr);

            var universe = GameManager.Instance?.Game?.UniverseModel;
            if (universe == null) return;

            string mappedGuid = vesselSync != null ? vesselSync.GetMappedGuid(guidStr) : guidStr;

            foreach (var v in universe.GetAllVessels())
            {
                if (string.Equals(v.Guid, mappedGuid, StringComparison.Ordinal))
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselLifecycle] Removing ghost SimulationObject for {guidStr} (Mapped: {mappedGuid})");
                    try
                    {
                        universe.RemoveVessel(v);
                        universe.RemoveSimulationObject(v.SimulationObject);
                    }
                    catch (Exception ex)
                    {
                        KSP2MultiplayerReduxPlugin.Logger.LogError($"[VesselLifecycle] Error destroying vessel: {ex.Message}");
                    }
                    break;
                }
            }
        }

        private void OnDestroy()
        {
            var messages = GameManager.Instance?.Game?.Messages;
            if (messages != null)
            {
                messages.Unsubscribe<VesselDestroyedMessage>(OnLocalVesselDestroyed);
                messages.Unsubscribe<VesselRecoveredMessage>(OnLocalVesselRecovered);
                messages.Unsubscribe<GameStateChangedMessage>(OnGameStateChanged);
            }

            if (NetworkManager.Instance != null && _hooked)
            {
                NetworkManager.Instance.OnVesselDestroyedReceived -= OnRemoteVesselDestroyed;
                NetworkManager.Instance.OnVesselRecoveredReceived -= OnRemoteVesselRecovered;
            }
        }
    }
}
