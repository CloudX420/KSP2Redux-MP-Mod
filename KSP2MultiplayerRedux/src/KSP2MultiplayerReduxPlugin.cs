#nullable enable
using SpaceWarp2.API.Mods;
using KSP2MultiplayerRedux.Networking;
using KSP2MultiplayerRedux.Sync;
using KSP2MultiplayerRedux.UI;
using UnityEngine;

namespace KSP2MultiplayerRedux
{
    public class KSP2MultiplayerReduxPlugin : GeneralMod
    {
        public const string ModGuid = "KSP2MultiplayerRedux";
        public const string ModName = "KSP2 Multiplayer Redux";
        public const string ModVer = Constants.VERSION;

        private const string BTN_MULTIPLAYER_FLIGHT = "BTN-MultiplayerFlight";
        private const string BTN_MULTIPLAYER_OAB = "BTN-MultiplayerOAB";

        public static KSP2MultiplayerReduxPlugin? Instance { get; private set; }
        public static ReduxLib.Logging.ILogger? Logger { get; private set; }

        /// <summary>Current KSP2 game state. Kept fresh by GameStateChangedMessage, but also read
        /// live from the state machine (so a client that connects while already in flight is
        /// correct immediately, not only after the next scene change).</summary>
        public static KSP.Game.GameState CurrentGameState { get; set; } = KSP.Game.GameState.Invalid;

        private static KSP.Game.GameState QueryGameState()
        {
            try
            {
                var cfg = KSP.Game.GameManager.Instance?.Game?.GlobalGameState?.GetGameState();
                if (cfg != null) return cfg.GameState;
            }
            catch { }
            return CurrentGameState;
        }

        /// <summary>True in scenes where remote vessels should be injected/rendered/synced.</summary>
        public static bool IsFlightScene
        {
            get
            {
                var s = QueryGameState();
                return s == KSP.Game.GameState.FlightView ||
                       s == KSP.Game.GameState.Map3DView ||
                       s == KSP.Game.GameState.TrackingStation ||
                       s == KSP.Game.GameState.Launchpad ||
                       s == KSP.Game.GameState.Runway;
            }
        }

        public NetworkManager? NetworkManager { get; private set; }
        public VesselSyncManager? VesselSync { get; private set; }
        public VesselLifecycleManager? VesselLifecycle { get; private set; }
        public TimeWarpSyncManager? TimeWarpSync { get; private set; }
        public TimeSyncManager? TimeSync { get; private set; }
        public CampaignSyncManager? CampaignSync { get; private set; }
        public MultiplayerMenu? MultiplayerMenu { get; private set; }
        public PlayerListPanel? PlayerListPanel { get; private set; }
        public WarpVotePanel? WarpVotePanel { get; private set; }

        // SpaceWarp2 only loads the manifest's main_assembly; sibling DLLs in the mod folder
        // (LiteNetLib.dll) are NOT loaded automatically. Without this, every type that touches
        // networking fails with "Could not load file or assembly 'LiteNetLib'" and the mod is dead
        // (NetworkManager can't instantiate, the menu never builds, Ctrl+M does nothing).
        private static bool _dependencyResolverInstalled;

        private static void InstallDependencyResolver()
        {
            if (_dependencyResolverInstalled) return;
            _dependencyResolverInstalled = true;

            string modDir = null;
            try { modDir = System.IO.Path.GetDirectoryName(typeof(KSP2MultiplayerReduxPlugin).Assembly.Location); }
            catch { }

            // If the loader loaded us from memory, Location is empty — fall back to <game>/mods/<ModGuid>.
            if (string.IsNullOrEmpty(modDir) || !System.IO.File.Exists(System.IO.Path.Combine(modDir, "LiteNetLib.dll")))
            {
                try
                {
                    var gameRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(UnityEngine.Application.dataPath, ".."));
                    var fallback = System.IO.Path.Combine(gameRoot, "mods", ModGuid);
                    if (System.IO.File.Exists(System.IO.Path.Combine(fallback, "LiteNetLib.dll")))
                        modDir = fallback;
                }
                catch { }
            }
            if (string.IsNullOrEmpty(modDir))
            {
                Logger?.LogError("[Deps] Could not determine mod folder — networking will not work.");
                return;
            }
            Logger?.LogInfo($"[Deps] Mod folder: {modDir}");

            // 1) Resolve any future request for a DLL that lives next to us.
            System.AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                try
                {
                    var name = new System.Reflection.AssemblyName(args.Name).Name;
                    var candidate = System.IO.Path.Combine(modDir, name + ".dll");
                    if (System.IO.File.Exists(candidate))
                    {
                        Logger?.LogInfo($"[Deps] Resolving '{name}' from mod folder.");
                        return System.Reflection.Assembly.LoadFrom(candidate);
                    }
                }
                catch (System.Exception ex) { Logger?.LogError($"[Deps] Resolve failed for {args.Name}: {ex.Message}"); }
                return null;
            };

            // 2) Eagerly load known dependencies now, before any networking type is touched.
            foreach (var dep in new[] { "LiteNetLib" })
            {
                try
                {
                    var path = System.IO.Path.Combine(modDir, dep + ".dll");
                    if (System.IO.File.Exists(path))
                    {
                        var asm = System.Reflection.Assembly.LoadFrom(path);
                        Logger?.LogInfo($"[Deps] Loaded {asm.GetName().Name} v{asm.GetName().Version} from {path}");
                    }
                    else
                    {
                        Logger?.LogError($"[Deps] MISSING dependency: {path}");
                    }
                }
                catch (System.Exception ex) { Logger?.LogError($"[Deps] Failed to load {dep}: {ex.Message}"); }
            }
        }

        public override void OnPreInitialized()
        {
            Instance = this;
            Logger = SWLogger;
            InstallDependencyResolver();
            Logger.LogInfo($"{ModName} v{ModVer} pre-initialized.");
        }

        public override void OnInitialized()
        {
            base.OnInitialized();
            Logger!.LogInfo($"{ModName} v{ModVer} initializing...");

            // NetworkManager
            var networkGo = new GameObject("NetworkManager");
            UnityEngine.Object.DontDestroyOnLoad(networkGo);
            NetworkManager = networkGo.AddComponent<NetworkManager>();

            // VesselSyncManager
            var vesselSyncGo = new GameObject("VesselSyncManager");
            UnityEngine.Object.DontDestroyOnLoad(vesselSyncGo);
            VesselSync = vesselSyncGo.AddComponent<VesselSyncManager>();

            // VesselSpawnManager
            var vesselSpawnGo = new GameObject("VesselSpawnManager");
            UnityEngine.Object.DontDestroyOnLoad(vesselSpawnGo);
            vesselSpawnGo.AddComponent<KSP2MultiplayerRedux.Sync.VesselSpawnManager>();

            // TimeWarpSyncManager
            var timeWarpSyncGo = new GameObject("TimeWarpSyncManager");
            UnityEngine.Object.DontDestroyOnLoad(timeWarpSyncGo);
            TimeWarpSync = timeWarpSyncGo.AddComponent<TimeWarpSyncManager>();

            // TimeSyncManager (server-authoritative universe clock)
            var timeSyncGo = new GameObject("TimeSyncManager");
            UnityEngine.Object.DontDestroyOnLoad(timeSyncGo);
            TimeSync = timeSyncGo.AddComponent<TimeSyncManager>();

            // CampaignSyncManager
            var campaignSyncGo = new GameObject("CampaignSyncManager");
            UnityEngine.Object.DontDestroyOnLoad(campaignSyncGo);
            CampaignSync = campaignSyncGo.AddComponent<CampaignSyncManager>();

            // VesselLifecycleManager
            var lifecycleGo = new GameObject("VesselLifecycleManager");
            UnityEngine.Object.DontDestroyOnLoad(lifecycleGo);
            VesselLifecycle = lifecycleGo.AddComponent<VesselLifecycleManager>();

            // DedicatedServerManager (always created — it self-disables if -server flag is absent)
            var dedicatedGo = new GameObject("DedicatedServerManager");
            UnityEngine.Object.DontDestroyOnLoad(dedicatedGo);
            dedicatedGo.AddComponent<DedicatedServerManager>();

            // Skip UI creation entirely in dedicated server mode (no GPU / no screen)
            if (DedicatedServerManager.IsServerMode)
            {
                Logger.LogInfo($"{ModName} v{ModVer} running in DEDICATED SERVER mode — UI skipped.");
                return;
            }

            // MultiplayerMenu
            var menuGo = new GameObject("MultiplayerMenu");
            UnityEngine.Object.DontDestroyOnLoad(menuGo);
            MultiplayerMenu = menuGo.AddComponent<MultiplayerMenu>();

            // PlayerListPanel
            var playerListGo = new GameObject("PlayerListPanel");
            UnityEngine.Object.DontDestroyOnLoad(playerListGo);
            PlayerListPanel = playerListGo.AddComponent<PlayerListPanel>();

            // WarpVotePanel
            var warpVoteGo = new GameObject("WarpVotePanel");
            UnityEngine.Object.DontDestroyOnLoad(warpVoteGo);
            WarpVotePanel = warpVoteGo.AddComponent<WarpVotePanel>();

            // Placeholder icon for AppBar buttons
            var icon = new Texture2D(16, 16);
            var pixels = new Color[16 * 16];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = Color.cyan;
            icon.SetPixels(pixels);
            icon.Apply();

            // Register AppBar buttons
            try
            {
                SpaceWarp2.UI.API.Appbar.Appbar.RegisterAppButton(
                    "Multiplayer",
                    BTN_MULTIPLAYER_FLIGHT,
                    icon,
                    isOpen => MultiplayerMenu!.IsWindowOpen = isOpen
                );

                SpaceWarp2.UI.API.Appbar.Appbar.RegisterOABAppButton(
                    "Multiplayer",
                    BTN_MULTIPLAYER_OAB,
                    icon,
                    isOpen => MultiplayerMenu!.IsWindowOpen = isOpen
                );
            }
            catch (System.Exception ex)
            {
                Logger.LogError($"Failed to register AppBar buttons: {ex}");
            }

            Logger.LogInfo($"{ModName} v{ModVer} initialization complete.");
        }
    }
}
