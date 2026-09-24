using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UitkForKsp2.Controls;
using KSP2MultiplayerRedux.Networking;

namespace KSP2MultiplayerRedux.UI
{
    /// <summary>
    /// Displays a live list of connected players with host badges and
    /// color-coded ping indicators. Rendered with the KSP2 native theme.
    /// </summary>
    public class PlayerListPanel : MonoBehaviour
    {
        // ---------------------------------------------------------------
        // Singleton
        // ---------------------------------------------------------------
        public static PlayerListPanel Instance { get; private set; }

        private UIDocument _document;
        private AppShell _appShell;
        private ScrollView _playerScrollView;

        private bool _isWindowOpen;

        /// <summary>
        /// Gets or sets whether the player list window is visible.
        /// </summary>
        public bool IsWindowOpen
        {
            get => _isWindowOpen;
            set
            {
                _isWindowOpen = value;
                if (_appShell != null)
                {
                    _appShell.style.display = _isWindowOpen ? DisplayStyle.Flex : DisplayStyle.None;
                }

                // Refresh whenever the panel is shown.
                if (_isWindowOpen)
                    RefreshPlayerList();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            // Create UIDocument component.
            _document = gameObject.AddComponent<UIDocument>();

            // Locate the game's PanelSettings.
            var panelSettings = Resources.FindObjectsOfTypeAll<PanelSettings>()
                .FirstOrDefault(p => p.name.Contains("Kerbal") || p.name.Contains("Theme"));

            if (panelSettings == null)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError(
                    "[PlayerListPanel] Could not find KSP2 PanelSettings. UI will not be created.");
                return;
            }

            _document.panelSettings = panelSettings;

            // --- Build AppShell ---
            _appShell = new AppShell
            {
                Title = "Connected Players",
                UppercaseTitle = true
            };

            _appShell.style.width = 350;
            _appShell.style.height = Length.Auto();
            _appShell.style.position = Position.Absolute;
            _appShell.style.left = 520;
            _appShell.style.top = 100;

            _appShell.CloseClicked += () => IsWindowOpen = false;

            // --- Content ---
            var content = new VisualElement();
            content.style.paddingTop = 8;
            content.style.paddingBottom = 12;
            content.style.paddingLeft = 10;
            content.style.paddingRight = 10;

            // Header row
            var headerRow = new VisualElement();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.marginBottom = 6;

            var nameHeader = new Label("Player");
            nameHeader.style.flexGrow = 1;
            nameHeader.style.fontSize = 13;
            nameHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            headerRow.Add(nameHeader);

            var pingHeader = new Label("Ping");
            pingHeader.style.width = 60;
            pingHeader.style.fontSize = 13;
            pingHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            pingHeader.style.unityTextAlign = TextAnchor.MiddleRight;
            headerRow.Add(pingHeader);

            content.Add(headerRow);

            // Divider
            var divider = new VisualElement();
            divider.style.height = 1;
            divider.style.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            divider.style.marginBottom = 4;
            content.Add(divider);

            // ScrollView for player rows
            _playerScrollView = new ScrollView(ScrollViewMode.Vertical);
            _playerScrollView.style.maxHeight = 400;
            _playerScrollView.style.minHeight = 40;
            content.Add(_playerScrollView);

            _appShell.Add(content);
            _document.rootVisualElement.Add(_appShell);

            // Start hidden.
            _appShell.style.display = DisplayStyle.None;
            _isWindowOpen = false;

            // Subscribe to network events.
            try
            {
                var nm = NetworkManager.Instance;
                if (nm != null)
                {
                    nm.OnPlayerListUpdated += RefreshPlayerList;
                    nm.OnPlayerJoined += _ => RefreshPlayerList();
                    nm.OnPlayerLeft += _ => RefreshPlayerList();
                }
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogWarning(
                    $"[PlayerListPanel] Could not subscribe to network events: {ex.Message}");
            }

            KSP2MultiplayerReduxPlugin.Logger.LogInfo("[PlayerListPanel] UI initialized.");
        }

        // ---------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------

        /// <summary>
        /// Clears and rebuilds the player list from the current NetworkManager state.
        /// </summary>
        public void RefreshPlayerList()
        {
            if (_playerScrollView == null) return;

            _playerScrollView.Clear();

            try
            {
                var nm = NetworkManager.Instance;
                if (nm?.Players == null || nm.Players.Count == 0)
                {
                    var emptyLabel = new Label("No players connected.");
                    emptyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                    emptyLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
                    emptyLabel.style.marginTop = 8;
                    _playerScrollView.Add(emptyLabel);
                    return;
                }

                foreach (var kvp in nm.Players)
                {
                    var player = kvp.Value;
                    _playerScrollView.Add(CreatePlayerRow(player));
                }
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError(
                    $"[PlayerListPanel] Error refreshing player list: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        private VisualElement CreatePlayerRow(PlayerInfo player)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingTop = 3;
            row.style.paddingBottom = 3;

            // Player name
            var nameLabel = new Label(player.SteamName ?? $"Player {player.PeerId}");
            nameLabel.style.flexGrow = 1;
            nameLabel.style.fontSize = 13;
            row.Add(nameLabel);

            // Host badge
            if (player.IsHost)
            {
                var hostBadge = new Label("[HOST]");
                hostBadge.style.color = Color.cyan;
                hostBadge.style.fontSize = 12;
                hostBadge.style.unityFontStyleAndWeight = FontStyle.Bold;
                hostBadge.style.marginRight = 8;
                row.Add(hostBadge);
            }

            // Ping label with color coding
            var pingLabel = new Label($"{player.Ping}ms");
            pingLabel.style.width = 60;
            pingLabel.style.fontSize = 12;
            pingLabel.style.unityTextAlign = TextAnchor.MiddleRight;

            if (player.Ping < 50)
                pingLabel.style.color = Color.green;
            else if (player.Ping < 150)
                pingLabel.style.color = Color.yellow;
            else
                pingLabel.style.color = Color.red;

            row.Add(pingLabel);

            return row;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            try
            {
                var nm = NetworkManager.Instance;
                if (nm != null)
                {
                    nm.OnPlayerListUpdated -= RefreshPlayerList;
                }
            }
            catch
            {
                // Swallow – the game may be shutting down.
            }
        }
    }
}
