using System;
using System.IO;
using System.Linq;
using UnityEngine;
using KSP.Game;
using KSP2MultiplayerRedux.Networking;

namespace KSP2MultiplayerRedux
{
    /// <summary>
    /// Dedicated Server Manager — when KSP2 is launched with -server,
    /// this component auto-loads a save file and starts hosting.
    /// All events are logged to both the BepInEx/SpaceWarp log AND
    /// a standalone "server.log" file in the game directory.
    /// 
    /// Usage:
    ///   KSP2_x64.exe -batchmode -nographics -server -port 7777 -save "MyCampaign"
    /// </summary>
    public class DedicatedServerManager : MonoBehaviour
    {
        public static DedicatedServerManager Instance { get; private set; }
        public static bool IsServerMode { get; private set; }

        private int _port = 7777;
        private string _saveName = "";
        private string _logFilePath;
        private StreamWriter _logWriter;

        private bool _hasAutoStarted = false;
        private float _bootTimer = 0f;
        private const float BootDelaySeconds = 10f; // Wait for KSP2 to fully initialize

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            // Check command line args for -server flag
            string[] args = Environment.GetCommandLineArgs();
            IsServerMode = args.Any(a => a.Equals("-server", StringComparison.OrdinalIgnoreCase));

            if (!IsServerMode) return;

            // Parse additional args
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("-port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], out _port);

                if (args[i].Equals("-save", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    _saveName = args[i + 1];
            }

            // Set up dedicated log file
            string gameDir = Path.GetDirectoryName(Application.dataPath);
            _logFilePath = Path.Combine(gameDir, "server.log");

            try
            {
                _logWriter = new StreamWriter(_logFilePath, false) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DedicatedServer] Could not create server.log: {ex.Message}");
            }

            // Hook into Unity's log system so ALL game logs are mirrored to our file
            Application.logMessageReceived += OnUnityLogMessage;

            ServerLog("╔══════════════════════════════════════════════════╗");
            ServerLog("║   KSP2 Multiplayer Redux — Dedicated Server     ║");
            ServerLog($"║   v{Constants.VERSION}  (Headless Client Mode)                ║");
            ServerLog("╚══════════════════════════════════════════════════╝");
            ServerLog("");
            ServerLog($"  Port:       {_port}");
            ServerLog($"  Save Name:  {(_saveName != "" ? _saveName : "(auto — last played)")}");
            ServerLog($"  Log File:   {_logFilePath}");
            ServerLog("");
            ServerLog("Waiting for KSP2 engine to finish booting...");
        }

        private void Update()
        {
            if (!IsServerMode || _hasAutoStarted) return;

            _bootTimer += Time.deltaTime;

            // Wait for KSP2 to fully initialize before trying to load saves
            if (_bootTimer < BootDelaySeconds) return;

            // Check if the game instance is ready
            var gameInstance = GameManager.Instance?.Game;
            if (gameInstance == null) return;

            _hasAutoStarted = true;
            ServerLog("KSP2 engine is ready. Starting dedicated server sequence...");

            // Start hosting immediately
            StartServerHosting();
        }

        private void StartServerHosting()
        {
            try
            {
                var networkManager = NetworkManager.Instance;
                if (networkManager == null)
                {
                    ServerLog("ERROR: NetworkManager not found! Make sure the mod loaded correctly.");
                    return;
                }

                // Start the listen server
                networkManager.StartHost(_port);
                ServerLog($"Server is now LIVE on port {_port}!");
                ServerLog("Waiting for players to connect...");
                ServerLog("");
                ServerLog("─────────────────────────────────────────────────");

                // Hook into network events for logging
                networkManager.OnPlayerJoined += (info) =>
                {
                    ServerLog($"[+] Player joined: {info.SteamName} (id={info.PeerId})");
                    ServerLog($"    Total players: {networkManager.Players.Count}");
                };

                networkManager.OnPlayerLeft += (info) =>
                {
                    ServerLog($"[-] Player left: {info.SteamName} (id={info.PeerId})");
                    ServerLog($"    Total players: {networkManager.Players.Count}");
                };

                networkManager.OnPlayerListUpdated += () =>
                {
                    // Periodic roster dump
                    string roster = string.Join(", ", networkManager.Players.Values.Select(p => $"{p.SteamName}({p.Ping}ms)"));
                    ServerLog($"[Roster] {networkManager.Players.Count} players: {roster}");
                };

                networkManager.OnWarpVoteRequested += (pkt) =>
                {
                    ServerLog($"[Warp] Vote requested by {pkt.RequesterName} for rate {pkt.TargetWarpRate}");
                };

                networkManager.OnWarpVoteCompleted += (passed) =>
                {
                    ServerLog($"[Warp] Vote result: {(passed ? "PASSED" : "DENIED")}");
                };

                networkManager.OnWarpApply += (rate) =>
                {
                    ServerLog($"[Warp] Applied warp rate index: {rate}");
                };

                networkManager.OnSaveLoadStarted += () =>
                {
                    ServerLog("[Save] Save transfer started...");
                };

                networkManager.OnSaveLoadComplete += () =>
                {
                    ServerLog("[Save] Save transfer complete.");
                };

                networkManager.OnRemoteTechUnlock += (nodeId) =>
                {
                    ServerLog($"[Tech] Remote tech unlock: {nodeId}");
                };

                networkManager.OnRemoteScienceEarned += (amount) =>
                {
                    ServerLog($"[Science] Remote science earned: {amount}");
                };
            }
            catch (Exception ex)
            {
                ServerLog($"FATAL ERROR starting server: {ex}");
            }
        }

        // ── Logging ─────────────────────────────────────────────────

        public void ServerLog(string message)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            string line = $"[{timestamp}] {message}";

            // Write to dedicated log file
            try
            {
                _logWriter?.WriteLine(line);
            }
            catch { }

            // Also write to Unity/BepInEx log
            KSP2MultiplayerReduxPlugin.Logger?.LogInfo($"[DedicatedServer] {message}");

            // Write to stdout for -batchmode console visibility
            Console.WriteLine(line);
        }

        private void OnUnityLogMessage(string condition, string stackTrace, LogType type)
        {
            // Only mirror errors and warnings from Unity to our server log
            if (type == LogType.Error || type == LogType.Exception)
            {
                try
                {
                    _logWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss}] [UNITY-{type}] {condition}");
                    if (!string.IsNullOrEmpty(stackTrace))
                        _logWriter?.WriteLine($"  {stackTrace.Split('\n')[0]}");
                }
                catch { }
            }
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnUnityLogMessage;

            if (_logWriter != null)
            {
                ServerLog("Server shutting down.");
                _logWriter.Close();
                _logWriter = null;
            }

            if (Instance == this) Instance = null;
        }
    }
}
