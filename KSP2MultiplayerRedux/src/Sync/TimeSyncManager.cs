using System;
using System.Reflection;
using UnityEngine;
using KSP.Game;
using KSP2MultiplayerRedux.Networking;

namespace KSP2MultiplayerRedux.Sync
{
    /// <summary>
    /// Server-authoritative universe time sync (modeled on LunaMultiplayer's TimeSyncer).
    /// - Dedicated server owns the clock (seeded by the first client's UT report, then advanced
    ///   by wall-clock * warp rate). Listen-server host's game clock is authoritative.
    /// - Clients receive TimeSync packets (~2Hz), compensate for half-RTT, and either hard-set
    ///   (large drift) or smoothly slew (small drift) their universe time.
    /// Without this, Keplerian orbital sync places vessels at the wrong anomaly because
    /// epoch/meanAnomaly only agree if both clients agree on UT.
    /// </summary>
    public class TimeSyncManager : MonoBehaviour
    {
        private const double HardSyncThreshold = 5.0;    // seconds of drift -> hard set
        private const double SlewThreshold = 0.05;       // ignore drift below this
        private const float UTReportInterval = 1.0f;     // client -> server seed reports
        private const float HostBroadcastInterval = 0.5f;

        // KSP2 warp rate table (index-aligned with TimeWarp rate indices)
        public static readonly double[] WarpRates = { 1, 2, 4, 10, 50, 100, 1000, 10_000, 100_000, 1_000_000, 10_000_000 };

        private bool _hooked;
        private float _utReportTimer;
        private float _hostBroadcastTimer;
        private double _pendingSlew;        // remaining seconds to absorb via slewing
        private bool _reflectionFailed;

        // Cached reflection for reading/writing universe time
        private PropertyInfo _utProperty;
        private object _utTarget;

        public static TimeSyncManager Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        /// <summary>Best-known synced universe time (falls back to local game UT).</summary>
        public double UniverseTime => GetLocalUT() ?? 0.0;

        private void Update()
        {
            var net = NetworkManager.Instance;
            if (net == null) return;
            if (!net.IsClient && !net.IsServer) return;

            if (!_hooked)
            {
                net.OnTimeSyncReceived += OnTimeSync;
                _hooked = true;
                KSP2MultiplayerReduxPlugin.Logger.LogInfo("[TimeSync] Hooked into network events.");
            }

            // Absorb pending slew gradually (10% per frame, framerate-independent enough at small values)
            if (Math.Abs(_pendingSlew) > 0.001)
            {
                double step = _pendingSlew * Mathf.Clamp01(Time.deltaTime * 5f);
                var ut = GetLocalUT();
                if (ut.HasValue && TrySetLocalUT(ut.Value + step))
                    _pendingSlew -= step;
                else
                    _pendingSlew = 0;
            }

            if (net.IsServer)
            {
                // Listen-server: broadcast our game clock as the authoritative time
                _hostBroadcastTimer += Time.deltaTime;
                if (_hostBroadcastTimer >= HostBroadcastInterval)
                {
                    _hostBroadcastTimer = 0f;
                    var ut = GetLocalUT();
                    if (ut.HasValue)
                    {
                        int warpIndex = GameManager.Instance?.Game?.ViewController?.TimeWarp?.CurrentRateIndex ?? 0;
                        net.Server.BroadcastTimeSync(ut.Value, warpIndex);
                    }
                }
            }
            else
            {
                // Pure client: report UT so a dedicated server can seed its clock
                _utReportTimer += Time.deltaTime;
                if (_utReportTimer >= UTReportInterval)
                {
                    _utReportTimer = 0f;
                    var ut = GetLocalUT();
                    if (ut.HasValue) net.SendUTReport(ut.Value);
                }
            }
        }

        private void OnTimeSync(TimeSyncPacket packet)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsServer) return; // host ignores its own echo

            var localUT = GetLocalUT();
            if (!localUT.HasValue) return;

            // Latency compensation: the packet is half-RTT old, during which server time advanced at warp rate
            double warpMult = WarpRates[Mathf.Clamp(packet.WarpRateIndex, 0, WarpRates.Length - 1)];
            double latency = (net.ServerPingMs / 2.0) / 1000.0;
            double targetUT = packet.ServerUT + latency * warpMult;

            double drift = targetUT - localUT.Value;

            if (Math.Abs(drift) >= HardSyncThreshold)
            {
                if (TrySetLocalUT(targetUT))
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[TimeSync] Hard sync: drift was {drift:F2}s, UT set to {targetUT:F1}");
                _pendingSlew = 0;
            }
            else if (Math.Abs(drift) >= SlewThreshold)
            {
                _pendingSlew = drift;
            }
        }

        // ── Universe time access (reflection with caching) ──────────

        private double? GetLocalUT()
        {
            if (!EnsureUTAccess()) return null;
            try
            {
                var val = _utProperty.GetValue(_utTarget);
                if (val is double d) return d;
            }
            catch { }
            return null;
        }

        private bool TrySetLocalUT(double ut)
        {
            if (!EnsureUTAccess()) return false;
            try
            {
                if (_utProperty.CanWrite)
                {
                    _utProperty.SetValue(_utTarget, ut);
                    return true;
                }
                // Fallback: look for a setter method on the same object
                var setter = _utTarget.GetType().GetMethod("SetUniverseTime", BindingFlags.Public | BindingFlags.Instance)
                          ?? _utTarget.GetType().GetMethod("SetUniversalTime", BindingFlags.Public | BindingFlags.Instance);
                if (setter != null && setter.GetParameters().Length == 1)
                {
                    setter.Invoke(_utTarget, new object[] { ut });
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (!_reflectionFailed)
                    KSP2MultiplayerReduxPlugin.Logger.LogError($"[TimeSync] Failed to set universe time: {ex.Message}");
            }
            if (!_reflectionFailed)
            {
                _reflectionFailed = true;
                KSP2MultiplayerReduxPlugin.Logger.LogWarning("[TimeSync] No writable universe-time API found — time sync will be read-only. Orbital sync accuracy may drift.");
            }
            return false;
        }

        private bool EnsureUTAccess()
        {
            if (_utProperty != null && _utTarget != null) return true;

            var universe = GameManager.Instance?.Game?.UniverseModel;
            if (universe == null) return false;

            foreach (var name in new[] { "UniverseTime", "UniversalTime", "Time" })
            {
                var prop = universe.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.PropertyType == typeof(double))
                {
                    _utProperty = prop;
                    _utTarget = universe;
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[TimeSync] Universe time bound to UniverseModel.{name} (writable: {prop.CanWrite})");
                    return true;
                }
            }
            return false;
        }

        private void OnDestroy()
        {
            if (NetworkManager.Instance != null && _hooked)
                NetworkManager.Instance.OnTimeSyncReceived -= OnTimeSync;
        }
    }
}
