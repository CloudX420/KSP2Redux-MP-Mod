using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TMPro;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using KSP2MultiplayerRedux.Networking;

namespace KSP2MultiplayerRedux.Sync
{
    /// <summary>
    /// Vessel Sync v3: Real-time, lock-based, interpolated replication (LunaMP-style).
    /// - Server-granted control locks: only the lock holder broadcasts a vessel's state.
    /// - 20Hz normal / 50Hz under thrust broadcast rates.
    /// - Received states are buffered (prev/next) and interpolated with a small delay,
    ///   so remote vessels move smoothly instead of teleporting between packets.
    /// - Orbiting vessels: Keplerian elements applied once per packet, KSP2's solver
    ///   handles motion; rotation is slerped.
    /// - Surface/atmospheric vessels: position lerped + rotation slerped in body frame.
    /// </summary>
    public class VesselSyncManager : MonoBehaviour
    {
        private const float NormalBroadcastRate = 0.05f;   // 20Hz
        private const float ThrustBroadcastRate = 0.02f;   // 50Hz during thrust
        private const float InterpolationDelay = 0.1f;     // render remote vessels 100ms in the past
        private const float StaleTimeout = 30f;
        private const float QueueRetryInterval = 0.5f;
        private const float LockRequestRetryInterval = 1f;
        private const float ViewCheckInterval = 1f;        // how often to (un)load view objects
        private const double ViewLoadRangeMeters = 15000;  // instantiate visible model within this range
        private const double ViewUnloadRangeMeters = 22500; // destroy view beyond this (hysteresis)

        private float _broadcastTimer;
        private float _queueRetryTimer;
        private bool _hooked;

        // KSP2 assigns NEW GUIDs when injecting vessels. Map Host GUID -> Local GUID.
        private readonly Dictionary<string, string> _remoteGuidMapping = new Dictionary<string, string>();
        private readonly Dictionary<string, VesselSyncState> _syncStates = new Dictionary<string, VesselSyncState>();

        // Queue for packets that arrive before the vessel has spawned locally
        private readonly List<VesselStatePacket> _pendingPackets = new List<VesselStatePacket>();

        // ── Control locks ───────────────────────────────────────────
        // vesselGuid -> controlling PlayerId (SteamID/device id), as broadcast by the server
        private readonly Dictionary<string, string> _vesselControllers = new Dictionary<string, string>();
        private string _controlledGuid;          // guid of the vessel we hold the lock for
        private string _requestedGuid;           // guid we're currently requesting
        private float _lastLockRequestTime;

        public void MapGuid(string originalGuid, string newGuid)
        {
            _remoteGuidMapping[originalGuid] = newGuid;
            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] Mapped Remote GUID {originalGuid} -> Local GUID {newGuid}");
        }

        public string GetMappedGuid(string originalGuid)
        {
            return _remoteGuidMapping.TryGetValue(originalGuid, out var newGuid) ? newGuid : originalGuid;
        }

        /// <summary>True if the given vessel is currently controlled by another player.</summary>
        public bool IsControlledByRemote(string vesselGuid)
        {
            return _vesselControllers.TryGetValue(vesselGuid, out var pid)
                && !string.Equals(pid, NetworkManager.Instance?.LocalPlayerIdString, StringComparison.Ordinal);
        }

        private class VesselSyncState
        {
            public string VesselGuid;        // Original remote GUID (key)
            public string OwnerName;
            public float LastUpdateTime;
            public GameObject NameplateObject;
            public TextMeshPro NameplateText;

            // Interpolation buffer
            public VesselStatePacket Prev;
            public VesselStatePacket Next;
            public float PrevArrival;
            public float NextArrival;
            public bool HasPrev;
            public bool OrbitApplied;        // elements from Next already pushed to the solver

            // View / physics management
            public float LastViewCheck;
            public bool ViewRequestPending;
            public byte AppliedSituation = 255;  // last situation we set a physics mode for

            // Multi-strategy view loading (see ManageViewObject)
            public int ViewStrategy;             // next strategy to try (0..3); 4 = exhausted
            public float ViewAttemptTime;        // when the current strategy was started
            public int EmptyViewChecks;          // consecutive checks where a view existed but had 0 parts
            public bool ViewVerified;            // view exists AND has rendered parts
            public bool ViewDiagDumped;
            public bool ViewExhaustedLogged;
        }

        private void Start()
        {
            KSP2MultiplayerReduxPlugin.Logger.LogInfo("[VesselSync] Initialized — Lock-based Interpolated Sync v3.");
        }

        private void Update()
        {
            var net = NetworkManager.Instance;
            if (net == null) return;
            if (!net.IsClient && !net.IsServer) return;

            if (!_hooked)
            {
                net.OnVesselStateReceived += OnRemoteVesselState;
                net.OnPlayerLeft += OnPlayerLeft;
                net.OnVesselControlUpdate += OnVesselControlUpdate;
                _hooked = true;
            }

            // Only touch vessels in flight-capable scenes. In the VAB/KSC/menus, or mid
            // scene-transition (revert/loading), suspend all sync work — this prevents
            // injecting remote craft into the editor and prevents crashes during revert
            // teardown. Buffered packets keep accumulating and apply once we're back.
            if (!KSP2MultiplayerReduxPlugin.IsFlightScene)
                return;

            // Determine broadcast rate based on thrust state
            float rate = NormalBroadcastRate;
            VesselComponent activeVessel = null;
            try
            {
                activeVessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();
                if (activeVessel != null && activeVessel.IsOrbitalPhysicsUnderThrustActive)
                    rate = ThrustBroadcastRate;
            }
            catch { }

            ManageControlLock(activeVessel);

            _broadcastTimer += Time.deltaTime;
            if (_broadcastTimer >= rate)
            {
                _broadcastTimer = 0f;
                BroadcastOwnedVessel(activeVessel);
            }

            // Retry queued packets for vessels that may have spawned since last check
            _queueRetryTimer += Time.deltaTime;
            if (_queueRetryTimer >= QueueRetryInterval && _pendingPackets.Count > 0)
            {
                _queueRetryTimer = 0f;
                RetryPendingPackets();
            }

            InterpolateRemoteVessels();

            // Stale cleanup — stop tracking vessels that haven't been updated in 30s.
            // The vessel itself persists in the shared universe (server keeps it alive);
            // we only drop the nameplate + interpolation state.
            var staleKeys = _syncStates
                .Where(kv => Time.time - kv.Value.LastUpdateTime > StaleTimeout)
                .Select(kv => kv.Key).ToList();
            foreach (var key in staleKeys)
            {
                var state = _syncStates[key];
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] Vessel {key} (Owner: {state.OwnerName}) went stale — releasing sync tracking (vessel persists).");
                DestroyNameplate(state);
                _syncStates.Remove(key);
            }

            // Update Nameplate Billboards
            foreach (var state in _syncStates.Values)
            {
                if (state.NameplateObject != null && Camera.main != null)
                {
                    state.NameplateObject.transform.rotation = Camera.main.transform.rotation;
                }
            }
        }

        // ═══════════════════════════════════════════════════
        //  CONTROL LOCKS
        // ═══════════════════════════════════════════════════

        private void ManageControlLock(VesselComponent activeVessel)
        {
            var net = NetworkManager.Instance;
            string activeGuid = null;
            try { activeGuid = activeVessel?.Guid; } catch { }

            // Active vessel changed: release the old lock
            if (_controlledGuid != null && !string.Equals(_controlledGuid, activeGuid, StringComparison.Ordinal))
            {
                net.ReleaseVesselControl(_controlledGuid);
                _vesselControllers.Remove(_controlledGuid);
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] Released control lock on {_controlledGuid}");
                _controlledGuid = null;
            }

            if (activeGuid == null) return;
            if (string.Equals(_controlledGuid, activeGuid, StringComparison.Ordinal)) return; // already held

            // Don't request control of a vessel someone else holds — spectate instead
            if (IsControlledByRemote(activeGuid)) return;

            // Request (with retry throttle) until the server grants it
            if (!string.Equals(_requestedGuid, activeGuid, StringComparison.Ordinal)
                || Time.time - _lastLockRequestTime > LockRequestRetryInterval)
            {
                _requestedGuid = activeGuid;
                _lastLockRequestTime = Time.time;
                net.RequestVesselControl(activeGuid);
            }
        }

        private void OnVesselControlUpdate(VesselControlUpdatePacket packet)
        {
            if (packet.ControllerPeerId == -1 || string.IsNullOrEmpty(packet.ControllerPlayerId))
            {
                _vesselControllers.Remove(packet.VesselGuid);
                if (string.Equals(_controlledGuid, packet.VesselGuid, StringComparison.Ordinal))
                    _controlledGuid = null;
                return;
            }

            _vesselControllers[packet.VesselGuid] = packet.ControllerPlayerId;

            if (string.Equals(packet.ControllerPlayerId, NetworkManager.Instance?.LocalPlayerIdString, StringComparison.Ordinal))
            {
                if (!string.Equals(_controlledGuid, packet.VesselGuid, StringComparison.Ordinal))
                {
                    _controlledGuid = packet.VesselGuid;
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] Control lock GRANTED for {packet.VesselGuid}");
                }
            }
            else if (string.Equals(_controlledGuid, packet.VesselGuid, StringComparison.Ordinal))
            {
                // Someone else took it (shouldn't happen, but be safe)
                _controlledGuid = null;
                KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[VesselSync] Lost control lock on {packet.VesselGuid} to {packet.ControllerName}");
            }
        }

        // ═══════════════════════════════════════════════════
        //  BROADCAST — Send our controlled vessel to the network
        // ═══════════════════════════════════════════════════

        private float _lastBroadcastLog;

        private void BroadcastOwnedVessel(VesselComponent activeVessel)
        {
            if (activeVessel == null) return;

            string guid = null;
            try { guid = activeVessel.Guid; } catch { return; }

            // Broadcast if we hold the lock, OR optimistically if nobody else is known to
            // control it (the lock grant is a round-trip; without this, state wouldn't flow
            // until the grant arrives — and a dropped grant would freeze the vessel forever).
            bool weHoldLock = string.Equals(_controlledGuid, guid, StringComparison.Ordinal);
            if (!weHoldLock && IsControlledByRemote(guid)) return;

            BroadcastVessel(activeVessel);
        }

        private void BroadcastVessel(VesselComponent vessel)
        {
            if (vessel == null) return;
            if (vessel.mainBody == null) return;

            try
            {
                var syncFrame = GetSyncFrame(vessel.mainBody, vessel.Situation);
                var localPos = syncFrame.ToLocalPosition(vessel.SimulationObject.transform.Position);
                var localRot = syncFrame.ToLocalRotation(vessel.SimulationObject.transform.Rotation);

                if (Time.time - _lastBroadcastLog > 5f)
                {
                    _lastBroadcastLog = Time.time;
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] Broadcasting '{vessel.Name}' ({vessel.Situation}) @ body {vessel.mainBody.Name}");
                }

                var packet = new VesselStatePacket
                {
                    VesselGuid = vessel.Guid,
                    VesselName = vessel.Name ?? "Unknown",
                    OwnerName = NetworkManager.Instance.LocalPlayerName,
                    ReferenceBodyName = vessel.mainBody.Name ?? "Kerbin",
                    Situation = (byte)vessel.Situation,
                    RotX = localRot.x, RotY = localRot.y, RotZ = localRot.z, RotW = localRot.w,
                    PosX = localPos.x, PosY = localPos.y, PosZ = localPos.z,
                    Latitude = vessel.Latitude,
                    Longitude = vessel.Longitude,
                    AltitudeFromTerrain = vessel.AltitudeFromTerrain,
                    IsUnderThrust = vessel.IsOrbitalPhysicsUnderThrustActive,
                    TimeStamp = TimeSyncManager.Instance != null ? TimeSyncManager.Instance.UniverseTime : 0.0
                };

                if (vessel.Orbit != null)
                {
                    var orbit = vessel.Orbit;
                    packet.Inclination = orbit.inclination;
                    packet.Eccentricity = orbit.eccentricity;
                    packet.SemiMajorAxis = orbit.semiMajorAxis;
                    packet.LongitudeOfAscendingNode = orbit.longitudeOfAscendingNode;
                    packet.ArgumentOfPeriapsis = orbit.argumentOfPeriapsis;
                    packet.MeanAnomalyAtEpoch = orbit.meanAnomalyAtEpoch;
                    packet.Epoch = orbit.epoch;
                }

                NetworkManager.Instance.BroadcastVesselState(packet);
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[VesselSync] Error broadcasting vessel {vessel.Name}: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════
        //  RECEIVE — buffer remote state for interpolation
        // ═══════════════════════════════════════════════════

        private void OnRemoteVesselState(VesselStatePacket packet)
        {
            // Ignore our own packets and packets for vessels we control
            if (string.Equals(packet.OwnerName, NetworkManager.Instance.LocalPlayerName, StringComparison.Ordinal))
                return;
            if (string.Equals(GetMappedGuid(packet.VesselGuid), _controlledGuid, StringComparison.Ordinal) && _controlledGuid != null)
                return;

            var gameInstance = GameManager.Instance?.Game;
            if (gameInstance == null) return;

            VesselComponent targetVessel = FindVesselByGuid(packet.VesselGuid);

            if (targetVessel == null)
            {
                if (_pendingPackets.Count < 50)
                {
                    _pendingPackets.RemoveAll(p => string.Equals(p.VesselGuid, packet.VesselGuid, StringComparison.Ordinal));
                    _pendingPackets.Add(packet);
                }
                return;
            }

            BufferVesselState(packet);
        }

        private void BufferVesselState(VesselStatePacket packet)
        {
            if (!_syncStates.TryGetValue(packet.VesselGuid, out var state))
            {
                state = new VesselSyncState
                {
                    VesselGuid = packet.VesselGuid,
                    OwnerName = packet.OwnerName
                };
                _syncStates[packet.VesselGuid] = state;
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] Tracking remote vessel: {packet.OwnerName} in {packet.VesselName} (Situation: {(VesselSituations)packet.Situation})");
            }

            state.LastUpdateTime = Time.time;

            // Shift interpolation buffer
            state.Prev = state.Next;
            state.PrevArrival = state.NextArrival;
            state.HasPrev = state.NextArrival > 0f;
            state.Next = packet;
            state.NextArrival = Time.time;
            state.OrbitApplied = false;
        }

        /// <summary>
        /// Returns the coordinate frame to sync a vessel in, based on its situation.
        /// Surface / atmospheric vessels use the body-fixed ROTATING frame (bodyFrame) so a
        /// stationary launch-pad craft keeps constant coordinates regardless of planet rotation
        /// or clock skew — the inertial celestialFrame made them drift tens of km ("40km off").
        /// Orbiting vessels use the inertial celestialFrame for attitude (position comes from
        /// Keplerian elements).
        /// </summary>
        private ITransformFrame GetSyncFrame(CelestialBodyComponent body, VesselSituations situation)
        {
            bool orbital = situation == VesselSituations.Orbiting || situation == VesselSituations.Escaping;
            return orbital ? body.transform.celestialFrame : body.transform.bodyFrame;
        }

        private void RetryPendingPackets()
        {
            var resolved = new List<VesselStatePacket>();
            foreach (var packet in _pendingPackets)
            {
                var vessel = FindVesselByGuid(packet.VesselGuid);
                if (vessel != null)
                {
                    BufferVesselState(packet);
                    resolved.Add(packet);
                }
            }
            foreach (var p in resolved)
                _pendingPackets.Remove(p);
        }

        // ═══════════════════════════════════════════════════
        //  INTERPOLATION — apply buffered states smoothly
        // ═══════════════════════════════════════════════════

        private void InterpolateRemoteVessels()
        {
            foreach (var state in _syncStates.Values)
            {
                if (state.NextArrival <= 0f) continue;

                var vessel = FindVesselByGuid(state.VesselGuid);
                if (vessel == null) continue;

                try
                {
                    ApplyPhysicsMode(vessel, state);
                    ApplyInterpolated(vessel, state);
                    ManageViewObject(vessel, state);
                    EnsureNameplate(vessel, state, state.Next);
                }
                catch (Exception ex)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[VesselSync] Error applying state to {vessel.Name}: {ex.Message}");
                }
            }
        }

        private void ApplyInterpolated(VesselComponent vessel, VesselSyncState state)
        {
            var next = state.Next;
            var situation = (VesselSituations)next.Situation;

            var body = ResolveCelestialBody(vessel, next.ReferenceBodyName);
            if (body == null) return;
            var bodyFrame = GetSyncFrame(body, situation);

            bool orbital = situation == VesselSituations.Orbiting || situation == VesselSituations.Escaping;

            if (orbital)
            {
                // Push Keplerian elements once per packet; the solver moves the vessel.
                if (!state.OrbitApplied && vessel.Orbit != null)
                {
                    ApplyOrbitalElements(vessel, next);
                    state.OrbitApplied = true;
                }
            }

            // Interpolation factor between Prev and Next (rendered InterpolationDelay in the past)
            float t = 1f;
            if (state.HasPrev && state.NextArrival > state.PrevArrival)
            {
                float interval = state.NextArrival - state.PrevArrival;
                t = (Time.time - InterpolationDelay - state.PrevArrival) / interval;
                t = Mathf.Clamp(t, 0f, 1.25f); // allow slight extrapolation when a packet is late
            }

            // Rotation: always slerp for smoothness
            QuaternionD rot;
            if (state.HasPrev)
            {
                var qa = new Quaternion((float)state.Prev.RotX, (float)state.Prev.RotY, (float)state.Prev.RotZ, (float)state.Prev.RotW);
                var qb = new Quaternion((float)next.RotX, (float)next.RotY, (float)next.RotZ, (float)next.RotW);
                var q = Quaternion.Slerp(qa, qb, Mathf.Clamp01(t));
                rot = new QuaternionD(q.x, q.y, q.z, q.w);
            }
            else
            {
                rot = new QuaternionD(next.RotX, next.RotY, next.RotZ, next.RotW);
            }
            vessel.SimulationObject.transform.UpdateRotation(new Rotation(bodyFrame, rot));

            if (!orbital)
            {
                // Surface / atmospheric flight: lerp position in body frame
                Vector3d pos;
                if (state.HasPrev)
                {
                    var pa = new Vector3d(state.Prev.PosX, state.Prev.PosY, state.Prev.PosZ);
                    var pb = new Vector3d(next.PosX, next.PosY, next.PosZ);
                    double td = t;
                    pos = new Vector3d(
                        pa.x + (pb.x - pa.x) * td,
                        pa.y + (pb.y - pa.y) * td,
                        pa.z + (pb.z - pa.z) * td);
                }
                else
                {
                    pos = new Vector3d(next.PosX, next.PosY, next.PosZ);
                }
                vessel.SimulationObject.transform.UpdatePosition(new Position(bodyFrame, pos));

                // Keep map view trajectory roughly correct
                if (!state.OrbitApplied && vessel.Orbit != null)
                {
                    ApplyOrbitalElements(vessel, next);
                    state.OrbitApplied = true;
                }
            }
        }

        // ═══════════════════════════════════════════════════
        //  PHYSICS MODE + VIEW OBJECT MANAGEMENT
        //  Injected remote vessels only exist as simulation models: KSP2 never
        //  instantiates their part meshes (that's why they showed in Map view but
        //  not in flight). We keep them "packed" (AtRest/Orbital) so local physics
        //  doesn't fight network updates, and instantiate/destroy their view
        //  objects ourselves based on distance to the active vessel.
        // ═══════════════════════════════════════════════════

        private void ApplyPhysicsMode(VesselComponent vessel, VesselSyncState state)
        {
            if (state.AppliedSituation == state.Next.Situation) return;

            var situation = (VesselSituations)state.Next.Situation;
            var mode = (situation == VesselSituations.PreLaunch ||
                        situation == VesselSituations.Landed ||
                        situation == VesselSituations.Splashed)
                ? PhysicsMode.AtRest
                : PhysicsMode.Orbital;

            try
            {
                if (vessel.Physics != mode)
                {
                    vessel.SetModelPhysicsMode(mode);
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] Set physics mode {mode} for remote vessel {state.VesselGuid} (situation {situation})");
                }
                state.AppliedSituation = state.Next.Situation;
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[VesselSync] Failed to set physics mode: {ex.Message}");
                state.AppliedSituation = state.Next.Situation; // don't retry every frame
            }
        }

        private const float ViewAsyncTimeout = 4f;   // seconds to wait for an async strategy before escalating

        /// <summary>
        /// Loads the visible 3D model for a remote vessel using an escalating set of strategies,
        /// and VERIFIES each one by checking the resulting view actually has parts. Injected vessels
        /// exist only as simulation models; a single guessed API call was not enough to trust.
        ///   0: UniverseView.LoadUnloadProximityViewObjects  — the game's own "load what's near me" pass
        ///   1: UniverseView.InstantiateViewObjectAsync       — the game's loader entry point
        ///   2: SimulationObjectModel.InstantiateViewObjectAsync
        ///   3: SimulationObjectModel.InstantiateViewObject   — synchronous
        /// Verification: SpaceSimulation.TryGetViewObject -> view.PartOwner.PartsCount > 0 && !view.IsDummy.
        /// A one-shot diagnostic dump (proximity loader state, distance, load state) is logged per vessel
        /// so a failing test still tells us exactly which stage failed.
        /// </summary>
        private void ManageViewObject(VesselComponent vessel, VesselSyncState state)
        {
            if (Time.time - state.LastViewCheck < ViewCheckInterval) return;
            state.LastViewCheck = Time.time;

            var game = GameManager.Instance?.Game;
            var spaceSim = game?.SpaceSimulation;
            var simObj = vessel.SimulationObject;
            if (spaceSim == null || simObj == null) return;

            var active = game.ViewController?.GetActiveSimVessel();
            if (active == null || active.SimulationObject == null) return;

            string vname = state.Next.VesselName;

            // Distance to our active vessel (common frame; only used for range gating)
            double dist;
            try
            {
                var frame = vessel.mainBody.transform.celestialFrame;
                var a = frame.ToLocalPosition(active.SimulationObject.transform.Position);
                var b = frame.ToLocalPosition(simObj.transform.Position);
                double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
                dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            catch { return; }

            bool viewLoaded;
            try { viewLoaded = spaceSim.IsViewObjectLoaded(simObj); }
            catch { return; }

            UniverseView universeView = null;
            try { universeView = spaceSim.UniverseView; } catch { }

            // ── One-shot diagnostic dump ─────────────────────────────
            if (!state.ViewDiagDumped)
            {
                state.ViewDiagDumped = true;
                string prox = "n/a";
                try
                {
                    if (universeView != null)
                        prox = $"enabled={universeView.IsProximityLoadUnloadEnabled} range={universeView.ProximityViewObjectRangeMeters:F0}m";
                }
                catch { }
                KSP2MultiplayerReduxPlugin.Logger.LogInfo(
                    $"[VesselSync] VIEW DIAG '{vname}': dist={dist:F0}m viewLoaded={viewLoaded} physics={vessel.Physics} situation={(VesselSituations)state.Next.Situation} proximityLoader[{prox}]");
            }

            // Make sure the game's own proximity loader is on and reaches our load range.
            try
            {
                if (universeView != null)
                {
                    if (!universeView.IsProximityLoadUnloadEnabled)
                    {
                        universeView.IsProximityLoadUnloadEnabled = true;
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo("[VesselSync] Proximity view loading was DISABLED — enabled it.");
                    }
                    if (universeView.ProximityViewObjectRangeMeters < ViewLoadRangeMeters)
                    {
                        universeView.ProximityViewObjectRangeMeters = (float)ViewLoadRangeMeters;
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] Raised proximity view range to {ViewLoadRangeMeters:F0}m.");
                    }
                }
            }
            catch { }

            // ── Already verified: just handle unload-by-distance and view loss ──
            if (state.ViewVerified)
            {
                if (!viewLoaded)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] View for '{vname}' was unloaded by the game — will reload when in range.");
                    state.ViewVerified = false;
                    state.ViewStrategy = 0;
                    state.ViewRequestPending = false;
                    DestroyNameplate(state);
                }
                else if (dist > ViewUnloadRangeMeters)
                {
                    try
                    {
                        DestroyNameplate(state);
                        simObj.DestroyViewObject();
                        state.ViewVerified = false;
                        state.ViewStrategy = 0;
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] Unloaded view for '{vname}' ({dist:F0}m away)");
                    }
                    catch (Exception ex)
                    {
                        KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[VesselSync] DestroyViewObject failed: {ex.Message}");
                    }
                }
                return;
            }

            // ── A view exists: verify it actually has parts ─────────
            if (viewLoaded)
            {
                int parts = -1; bool allCreated = false, dummy = false; bool gotView = false;
                try
                {
                    if (spaceSim.TryGetViewObject(simObj, out SimulationObjectView view) && view != null)
                    {
                        gotView = true;
                        dummy = view.IsDummy;
                        var po = view.PartOwner;
                        if (po != null)
                        {
                            parts = po.PartsCount;
                            try { allCreated = po.IsAllPartsCreated(); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[VesselSync] View verification threw for '{vname}': {ex.Message}");
                }

                if (gotView && parts > 0 && !dummy)
                {
                    state.ViewVerified = true;
                    state.ViewRequestPending = false;
                    state.EmptyViewChecks = 0;
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] ✓ RENDERED '{vname}' ({state.OwnerName}): {parts} parts, allCreated={allCreated}, strategy={state.ViewStrategy}");
                    return;
                }

                // View exists but is empty/dummy. Parts may still be building — allow a few checks before escalating.
                state.EmptyViewChecks++;
                KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[VesselSync] View for '{vname}' exists but is EMPTY (gotView={gotView} parts={parts} dummy={dummy} allCreated={allCreated}) check {state.EmptyViewChecks}/3");
                if (state.EmptyViewChecks < 3) return;
                state.EmptyViewChecks = 0;
                state.ViewRequestPending = false;
                state.ViewStrategy++;
                // fall through: escalate to the next strategy (destroy the empty shell first)
                try { simObj.DestroyViewObject(); } catch { }
            }

            if (dist > ViewLoadRangeMeters) return;

            // ── Wait for an in-flight async strategy, escalate on timeout ──
            if (state.ViewRequestPending)
            {
                if (Time.time - state.ViewAttemptTime < ViewAsyncTimeout) return;
                KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[VesselSync] Strategy {state.ViewStrategy} produced no view for '{vname}' within {ViewAsyncTimeout}s — escalating.");
                state.ViewRequestPending = false;
                state.ViewStrategy++;
            }

            if (state.ViewStrategy > 3)
            {
                if (!state.ViewExhaustedLogged)
                {
                    state.ViewExhaustedLogged = true;
                    KSP2MultiplayerReduxPlugin.Logger.LogError($"[VesselSync] ✗ ALL view strategies exhausted for '{vname}' — vessel model cannot be rendered by any known API. Report this line.");
                }
                return;
            }

            // ── Try the current strategy ────────────────────────────
            int strat = state.ViewStrategy;
            state.ViewRequestPending = true;
            state.ViewAttemptTime = Time.time;
            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] '{vname}' within {dist:F0}m — view strategy {strat} starting...");

            try
            {
                switch (strat)
                {
                    case 0:
                        if (universeView == null) { state.ViewRequestPending = false; state.ViewStrategy++; return; }
                        universeView.LoadUnloadProximityViewObjects(active.SimulationObject.transform.Position, ViewLoadRangeMeters, ok =>
                        {
                            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] strategy 0 (proximity pass) finished for '{vname}': ok={ok}");
                            state.ViewRequestPending = false;
                            if (!ok) state.ViewStrategy++;
                        });
                        break;

                    case 1:
                        if (universeView == null) { state.ViewRequestPending = false; state.ViewStrategy++; return; }
                        universeView.InstantiateViewObjectAsync(simObj, view =>
                        {
                            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] strategy 1 (UniverseView.InstantiateViewObjectAsync) finished for '{vname}': view={(view != null)}");
                            state.ViewRequestPending = false;
                            if (view == null) state.ViewStrategy++;
                        });
                        break;

                    case 2:
                        simObj.InstantiateViewObjectAsync(view =>
                        {
                            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] strategy 2 (SimObj.InstantiateViewObjectAsync) finished for '{vname}': view={(view != null)}");
                            state.ViewRequestPending = false;
                            if (view == null) state.ViewStrategy++;
                        });
                        break;

                    case 3:
                        var v = simObj.InstantiateViewObject();
                        KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] strategy 3 (SimObj.InstantiateViewObject sync) finished for '{vname}': view={(v != null)}");
                        state.ViewRequestPending = false;
                        if (v == null) state.ViewStrategy++;
                        break;
                }
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError($"[VesselSync] view strategy {strat} threw for '{vname}': {ex.Message} — escalating.");
                state.ViewRequestPending = false;
                state.ViewStrategy++;
            }
        }

        private VesselComponent FindVesselByGuid(string remoteGuid)
        {
            var universe = GameManager.Instance?.Game?.UniverseModel;
            if (universe == null) return null;

            string mappedGuid = GetMappedGuid(remoteGuid);

            foreach (var v in universe.GetAllVessels())
            {
                if (string.Equals(v.Guid, mappedGuid, StringComparison.Ordinal))
                    return v;
            }
            return null;
        }

        /// <summary>Resolve the correct celestial body from the packet's reference body name.
        /// Falls back to the vessel's current mainBody if lookup fails.</summary>
        private CelestialBodyComponent ResolveCelestialBody(VesselComponent vessel, string bodyName)
        {
            if (!string.IsNullOrEmpty(bodyName))
            {
                var universe = GameManager.Instance?.Game?.UniverseModel;
                if (universe != null)
                {
                    var body = universe.FindCelestialBodyByName(bodyName);
                    if (body != null) return body;
                }
            }
            return vessel.mainBody;
        }

        private bool _orbitTypeWarned;

        private void ApplyOrbitalElements(VesselComponent vessel, VesselStatePacket packet)
        {
            // VesselComponent.Orbit is exposed as the read-only IKeplerPatch interface; the concrete
            // PatchedConicsOrbit behind it has settable Keplerian elements (verified against the
            // current game assembly). Setting them lets KSP2's own solver drive the vessel's motion.
            if (vessel.Orbit is PatchedConicsOrbit orbit)
            {
                orbit.inclination = packet.Inclination;
                orbit.eccentricity = packet.Eccentricity;
                orbit.semiMajorAxis = packet.SemiMajorAxis;
                orbit.longitudeOfAscendingNode = packet.LongitudeOfAscendingNode;
                orbit.argumentOfPeriapsis = packet.ArgumentOfPeriapsis;
                orbit.meanAnomalyAtEpoch = packet.MeanAnomalyAtEpoch;
                orbit.epoch = packet.Epoch;
                return;
            }

            if (!_orbitTypeWarned)
            {
                _orbitTypeWarned = true;
                KSP2MultiplayerReduxPlugin.Logger.LogWarning(
                    $"[VesselSync] Vessel orbit is {vessel.Orbit?.GetType().Name ?? "null"}, not PatchedConicsOrbit — orbital elements cannot be applied. Report this line.");
            }
        }

        // ═══════════════════════════════════════════════════
        //  NAMEPLATE SYSTEM
        // ═══════════════════════════════════════════════════

        private void EnsureNameplate(VesselComponent vessel, VesselSyncState state, VesselStatePacket packet)
        {
            if (state.NameplateObject != null) return;

            var gameInstance = GameManager.Instance?.Game;
            if (gameInstance == null) return;

            var viewObj = gameInstance.SpaceSimulation?.ModelViewMap?.FromModel(vessel.SimulationObject);
            if (viewObj == null) return;

            state.NameplateObject = new GameObject($"Nameplate_{packet.OwnerName}");
            state.NameplateObject.transform.SetParent(viewObj.transform);
            state.NameplateObject.transform.localPosition = Vector3.up * 8f;

            state.NameplateText = state.NameplateObject.AddComponent<TextMeshPro>();
            state.NameplateText.text = packet.OwnerName;
            state.NameplateText.fontSize = 36;
            state.NameplateText.alignment = TextAlignmentOptions.Center;
            state.NameplateText.color = new Color(0.3f, 1f, 1f, 1f);
            state.NameplateText.outlineColor = new Color32(0, 0, 0, 200);
            state.NameplateText.outlineWidth = 0.25f;

            var rect = state.NameplateText.GetComponent<RectTransform>();
            if (rect != null) rect.sizeDelta = new Vector2(200f, 50f);

            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[VesselSync] Created nameplate for {packet.OwnerName}");
        }

        // ═══════════════════════════════════════════════════
        //  CLEANUP
        // ═══════════════════════════════════════════════════

        private void OnPlayerLeft(PlayerInfo player)
        {
            // Vessels persist in the shared universe when their pilot leaves —
            // only drop sync tracking + nameplates. The server releases their locks.
            var toRemove = _syncStates.Where(kv => string.Equals(kv.Value.OwnerName, player.SteamName, StringComparison.Ordinal))
                .Select(kv => kv.Key).ToList();
            foreach (var key in toRemove)
            {
                DestroyNameplate(_syncStates[key]);
                _syncStates.Remove(key);
            }
            _pendingPackets.RemoveAll(p => string.Equals(p.OwnerName, player.SteamName, StringComparison.Ordinal));
        }

        public void RemoveVessel(string vesselGuid)
        {
            if (_syncStates.TryGetValue(vesselGuid, out var state))
            {
                DestroyNameplate(state);
                _syncStates.Remove(vesselGuid);
            }
            _vesselControllers.Remove(vesselGuid);
            _pendingPackets.RemoveAll(p => string.Equals(p.VesselGuid, vesselGuid, StringComparison.Ordinal));
        }

        private void DestroyNameplate(VesselSyncState state)
        {
            if (state.NameplateObject != null)
            {
                UnityEngine.Object.Destroy(state.NameplateObject);
                state.NameplateObject = null;
            }
        }

        private void OnDestroy()
        {
            if (NetworkManager.Instance != null && _hooked)
            {
                NetworkManager.Instance.OnVesselStateReceived -= OnRemoteVesselState;
                NetworkManager.Instance.OnPlayerLeft -= OnPlayerLeft;
                NetworkManager.Instance.OnVesselControlUpdate -= OnVesselControlUpdate;
            }

            foreach (var state in _syncStates.Values)
                DestroyNameplate(state);
            _syncStates.Clear();
            _pendingPackets.Clear();
            _vesselControllers.Clear();
        }
    }
}
