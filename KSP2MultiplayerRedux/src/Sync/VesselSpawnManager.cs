using System;
using System.Text;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using KSP.Sim.State;
using KSP.Game.Serialization;
using KSP.IO;
using KSP.Messages;
using UnityEngine;
using KSP2MultiplayerRedux.Networking;

namespace KSP2MultiplayerRedux.Sync
{
    public class VesselSpawnManager : MonoBehaviour
    {
        private bool _isInjectingRemoteVessel = false;

        // Remote vessel payloads that arrived while we were NOT in a flight scene (e.g. in the
        // VAB or main menu). Injecting a flight vessel into the editor corrupts the scene
        // ("spawned in the middle of my building"), so we hold them until we're in flight.
        private readonly System.Collections.Generic.List<byte[]> _deferredSpawns = new System.Collections.Generic.List<byte[]>();

        private void Update()
        {
            if (_deferredSpawns.Count > 0 && KSP2MultiplayerReduxPlugin.IsFlightScene
                && GameManager.Instance?.Game?.SpaceSimulation != null)
            {
                var pending = _deferredSpawns.ToArray();
                _deferredSpawns.Clear();
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSpawnManager] Entered flight scene — injecting {pending.Length} deferred remote vessel(s).");
                foreach (var payload in pending)
                    InjectRemoteVessel(payload);
            }
        }

        private void Start()
        {
            if (GameManager.Instance?.Game?.Messages != null)
            {
                GameManager.Instance.Game.Messages.PersistentSubscribe<VesselCreatedMessage>(OnVesselCreated);
                GameManager.Instance.Game.Messages.PersistentSubscribe<VesselLaunchedMessage>(OnVesselLaunched);
            }
            if (NetworkManager.Instance != null)
            {
                NetworkManager.Instance.OnVesselSpawnPayloadReceived += OnRemoteVesselSpawnPayloadReceived;
            }
            KSP2MultiplayerReduxPlugin.Logger.LogInfo("[VesselSpawnManager] Initialized.");
        }

        private void OnDestroy()
        {
            if (GameManager.Instance?.Game?.Messages != null)
            {
                GameManager.Instance.Game.Messages.Unsubscribe<VesselCreatedMessage>(OnVesselCreated);
                GameManager.Instance.Game.Messages.Unsubscribe<VesselLaunchedMessage>(OnVesselLaunched);
            }
            if (NetworkManager.Instance != null)
            {
                NetworkManager.Instance.OnVesselSpawnPayloadReceived -= OnRemoteVesselSpawnPayloadReceived;
            }
        }

        private void OnVesselCreated(MessageCenterMessage message)
        {
            if (_isInjectingRemoteVessel) return;

            var createMsg = message as VesselCreatedMessage;
            if (createMsg == null) return;
            var vehicle = createMsg.vehicle;
            if (vehicle == null) return;

            var serializedAssembly = createMsg.SerializedVessel;
            if (serializedAssembly == null) return;

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSpawnManager] Local vessel spawned (VesselCreated): {vehicle.Guid}. Broadcasting...");
            BroadcastSerializedAssembly(serializedAssembly, vehicle.Guid.ToString());
        }

        private void OnVesselLaunched(MessageCenterMessage message)
        {
            if (_isInjectingRemoteVessel) return;

            var launchedMsg = message as VesselLaunchedMessage;
            if (launchedMsg == null) return;
            var vessel = launchedMsg.Vessel;
            if (vessel == null || vessel.SimulationObject == null) return;

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSpawnManager] Local vessel launched from VAB: {vessel.Guid}. Serializing...");

            try
            {
                var serializedAssembly = SerializationUtility.VesselToSerializable(vessel.SimulationObject, false);
                if (serializedAssembly != null)
                {
                    BroadcastSerializedAssembly(serializedAssembly, vessel.Guid.ToString());
                }
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError($"[VesselSpawnManager] Failed to serialize launched vessel: {ex}");
            }
        }

        private void BroadcastSerializedAssembly(SerializedAssembly serializedAssembly, string guidLog)
        {
            try
            {
                string json = KSP.IO.IOProvider.ToJson(serializedAssembly);
                byte[] data = System.Text.Encoding.UTF8.GetBytes(json);

                NetworkManager.Instance.StartLargePayloadTransfer(PayloadType.VesselSpawn, data, guidLog);
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSpawnManager] Vessel {guidLog} serialized ({data.Length} bytes). Started broadcast.");
            }
            catch (System.Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError($"[VesselSpawnManager] Error serializing/sending vessel: {ex.Message}");
            }
        }

        private void OnRemoteVesselSpawnPayloadReceived(byte[] payload)
        {
            // Defer injection until we're in a flight-capable scene (not the VAB / menu / loading).
            if (!KSP2MultiplayerReduxPlugin.IsFlightScene || GameManager.Instance?.Game?.SpaceSimulation == null)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogInfo("[VesselSpawnManager] Remote vessel payload received but not in flight scene — deferring injection.");
                _deferredSpawns.Add(payload);
                return;
            }
            InjectRemoteVessel(payload);
        }

        private void InjectRemoteVessel(byte[] payload)
        {
            try
            {
                string json = Encoding.UTF8.GetString(payload);
                var serializedAssembly = IOProvider.FromJson<SerializedAssembly>(json);
                if (serializedAssembly == null)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogError("[VesselSpawnManager] Failed to deserialize remote vessel assembly.");
                    return;
                }

                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSpawnManager] Remote vessel payload received. Injecting into Universe...");

                _isInjectingRemoteVessel = true;

                // Inject into the simulation
                var simObj = GameManager.Instance.Game.SpaceSimulation.CreateVesselSimObject(
                    serializedAssembly, 
                    NetworkManager.Instance.LocalPlayerIdString, // owner guid
                    0, // owner id
                    0  // authority id
                );

                if (simObj != null)
                {
                    string originalGuidFromAssembly = serializedAssembly.Guid.ToString();
                    string localGuidFromSimObj = simObj.GlobalIdGuidString;
                    
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSpawnManager] GUID DEBUG: Assembly.Guid.ToString() = '{originalGuidFromAssembly}'");
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSpawnManager] GUID DEBUG: SimObj.GlobalIdGuidString = '{localGuidFromSimObj}'");

                    // Also try to get the VesselComponent's Guid string for verification
                    string vesselComponentGuid = null;
                    try
                    {
                        var vc = simObj.FindComponent<VesselComponent>();
                        if (vc != null)
                        {
                            vesselComponentGuid = vc.Guid;
                            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSpawnManager] GUID DEBUG: VesselComponent.Guid = '{vesselComponentGuid}'");
                        }
                    }
                    catch { }

                    // Map both possible original GUID formats to the local GUID
                    var vesselSync = FindAnyObjectByType<VesselSyncManager>();
                    if (vesselSync != null) 
                    {
                        // Primary mapping: serialized GUID -> local GUID
                        vesselSync.MapGuid(originalGuidFromAssembly, vesselComponentGuid ?? localGuidFromSimObj);
                        
                        // If formats differ, also map the SimObj GUID format
                        if (!string.Equals(originalGuidFromAssembly, localGuidFromSimObj, StringComparison.Ordinal))
                        {
                            vesselSync.MapGuid(localGuidFromSimObj, vesselComponentGuid ?? localGuidFromSimObj);
                        }
                    }
                }

                _isInjectingRemoteVessel = false;
                KSP2MultiplayerReduxPlugin.Logger.LogInfo("[VesselSpawnManager] Remote vessel injected successfully.");
            }
            catch (Exception ex)
            {
                _isInjectingRemoteVessel = false;
                KSP2MultiplayerReduxPlugin.Logger.LogError($"[VesselSpawnManager] Failed to inject remote vessel: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
