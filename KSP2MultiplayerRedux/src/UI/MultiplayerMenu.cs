using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UitkForKsp2.Controls;
using KSP2MultiplayerRedux.Networking;

namespace KSP2MultiplayerRedux.UI
{
    /// <summary>
    /// Main multiplayer connection menu. Provides host, join, and disconnect controls
    /// rendered with the native KSP2 theme via UIToolkit + UitkForKsp2.
    /// </summary>
    public class MultiplayerMenu : MonoBehaviour
    {
        private UIDocument _document;
        private AppShell _appShell;
        private Label _statusLabel;
        private TextField _ipField;
        private TextField _portField;
        private TextField _nameField;
        private Button _joinButton;
        private Button _disconnectButton;
        private Button _playerListButton;
        
        // New elements

        private ProgressBar _transferProgress;
        private ScrollView _logScrollView;
        private Label _logText;

        private bool _isWindowOpen;

        /// <summary>
        /// Gets or sets whether the menu window is visible.
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
            }
        }

        private void Start()
        {
            // Create UIDocument component on this GameObject.
            _document = gameObject.AddComponent<UIDocument>();

            // Locate the game's PanelSettings to inherit the KSP2 theme.
            var panelSettings = Resources.FindObjectsOfTypeAll<PanelSettings>()
                .FirstOrDefault(p => p.name.Contains("Kerbal") || p.name.Contains("Theme"));

            if (panelSettings == null)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError(
                    "[MultiplayerMenu] Could not find KSP2 PanelSettings. UI will not be created.");
                return;
            }

            _document.panelSettings = panelSettings;

            // --- Build the AppShell window ---
            _appShell = new AppShell
            {
                Title = "KSP2 Multiplayer",
                UppercaseTitle = true
            };

            _appShell.style.width = 400;
            _appShell.style.height = Length.Auto();
            _appShell.style.position = Position.Absolute;
            _appShell.style.left = 100;
            _appShell.style.top = 100;

            _appShell.CloseClicked += () => IsWindowOpen = false;
            
            // Make the window draggable
            _appShell.AddManipulator(new DragManipulator(_appShell));

            // --- Content container ---
            var content = new VisualElement();
            content.style.paddingTop = 8;
            content.style.paddingBottom = 12;
            content.style.paddingLeft = 12;
            content.style.paddingRight = 12;

            // Welcome label
            string playerName = "Player";
            try
            {
                var localName = NetworkManager.Instance?.LocalPlayerName;
                if (!string.IsNullOrEmpty(localName))
                    playerName = localName;
            }
            catch (Exception)
            {
                // NetworkManager may not be ready yet; fall back to default.
            }

            var welcomeLabel = new Label($"Welcome, {playerName} (v{Networking.Constants.VERSION})");
            welcomeLabel.style.fontSize = 16;
            welcomeLabel.style.marginBottom = 8;
            welcomeLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            content.Add(welcomeLabel);

            // Status label
            _statusLabel = new Label("DISCONNECTED");
            _statusLabel.style.fontSize = 14;
            _statusLabel.style.color = Color.yellow;
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _statusLabel.style.marginBottom = 12;
            _statusLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            content.Add(_statusLabel);

            // --- Version Label ---
            var versionLabel = new Label($"Version: {Constants.VERSION}");
            versionLabel.style.fontSize = 11;
            versionLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
            versionLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            versionLabel.style.marginBottom = 12;
            content.Add(versionLabel);

            // --- IP Address field ---
            _ipField = new TextField("IP Address")
            {
                value = "127.0.0.1"
            };
            _ipField.style.marginBottom = 6;
            content.Add(_ipField);

            // --- Port field ---
            _portField = new TextField("Port")
            {
                value = "7777"
            };
            _portField.style.marginBottom = 6;
            content.Add(_portField);

            // --- Name field ---
            _nameField = new TextField("Player Name")
            {
                value = NetworkManager.Instance?.LocalPlayerName ?? Environment.UserName
            };
            _nameField.style.marginBottom = 12;
            content.Add(_nameField);

            // --- Action buttons ---
            var buttonContainer = new VisualElement();
            buttonContainer.style.flexDirection = FlexDirection.Row;
            buttonContainer.style.justifyContent = Justify.SpaceBetween;

            _joinButton = new Button(OnJoinClicked) { text = "Connect" };
            _joinButton.AddToClassList("button");
            _joinButton.style.backgroundColor = new Color(0.2f, 0.8f, 0.4f); // Greenish
            buttonContainer.Add(_joinButton);

            _disconnectButton = new Button(OnDisconnectClicked) { text = "Disconnect" };
            _disconnectButton.AddToClassList("button");
            _disconnectButton.style.backgroundColor = new Color(0.8f, 0.2f, 0.2f); // Reddish
            buttonContainer.Add(_disconnectButton);

            content.Add(buttonContainer);



            // --- Progress Bar ---
            _transferProgress = new ProgressBar();
            _transferProgress.title = "Save Transfer: 0%";
            _transferProgress.value = 0f;
            _transferProgress.highValue = 100f;
            _transferProgress.style.display = DisplayStyle.None;
            _transferProgress.style.marginTop = 12;
            content.Add(_transferProgress);

            // --- Log Window ---
            _logScrollView = new ScrollView(ScrollViewMode.Vertical);
            _logScrollView.style.height = 100;
            _logScrollView.style.marginTop = 12;
            _logScrollView.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.8f);
            
            _logText = new Label("Ready.");
            _logText.style.color = new Color(0.7f, 0.7f, 0.7f);
            _logText.style.fontSize = 11;
            _logText.style.whiteSpace = WhiteSpace.Normal;
            _logScrollView.Add(_logText);
            
            content.Add(_logScrollView);

            // --- Player list toggle button ---
            _playerListButton = new Button(OnPlayerListToggle) { text = "Player List" };
            _playerListButton.style.marginTop = 8;
            _playerListButton.AddToClassList("button");
            content.Add(_playerListButton);

            _appShell.Add(content);

            // Attach to the root visual element.
            _document.rootVisualElement.Add(_appShell);

            // Start hidden.
            _appShell.style.display = DisplayStyle.None;
            _isWindowOpen = false;

            // Subscribe to NetworkManager events for automatic status updates.
            SubscribeToNetworkEvents();

            KSP2MultiplayerReduxPlugin.Logger.LogInfo("[MultiplayerMenu] UI initialized.");
        }

        private void Update()
        {
            // Ctrl+M toggles the multiplayer menu.
            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.M))
            {
                IsWindowOpen = !IsWindowOpen;
            }
        }

        // ---------------------------------------------------------------
        // Button callbacks
        // ---------------------------------------------------------------



        public void AddLog(string message)
        {
            if (_logText == null) return;
            string time = DateTime.Now.ToString("HH:mm:ss");
            _logText.text += $"\n[{time}] {message}";
            
            // Auto scroll to bottom
            if (_logScrollView != null)
            {
                _logScrollView.schedule.Execute(() =>
                {
                    _logScrollView.scrollOffset = new Vector2(0, _logScrollView.contentContainer.layout.height);
                }).StartingIn(50);
            }
        }

        private void OnJoinClicked()
        {
            string ip = _ipField.value?.Trim();
            if (string.IsNullOrEmpty(ip))
            {
                SetStatus("INVALID IP", Color.red);
                return;
            }

            if (!int.TryParse(_portField.value, out int port))
            {
                SetStatus("INVALID PORT", Color.red);
                return;
            }
            
            string playerName = _nameField.value?.Trim();
            if (string.IsNullOrEmpty(playerName))
            {
                playerName = Environment.UserName;
            }

            try
            {
                if (NetworkManager.Instance != null)
                {
                    NetworkManager.Instance.LocalPlayerName = playerName;
                    NetworkManager.Instance.ConnectClient(ip, port);
                }
                SetStatus("CONNECTING...", new Color(1f, 0.65f, 0f)); // orange
                KSP2MultiplayerReduxPlugin.Logger.LogInfo(
                    $"[MultiplayerMenu] Connecting to {ip}:{port}...");
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError(
                    $"[MultiplayerMenu] Failed to connect: {ex.Message}");
                SetStatus("CONNECT FAILED", Color.red);
            }
        }

        private void OnDisconnectClicked()
        {
            try
            {
                NetworkManager.Instance.Disconnect();
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError(
                    $"[MultiplayerMenu] Disconnect error: {ex.Message}");
            }

            SetStatus("DISCONNECTED", Color.yellow);
        }

        private void OnPlayerListToggle()
        {
            if (PlayerListPanel.Instance != null)
            {
                PlayerListPanel.Instance.IsWindowOpen = !PlayerListPanel.Instance.IsWindowOpen;
            }
        }

        // ---------------------------------------------------------------
        // Status helpers
        // ---------------------------------------------------------------

        public void SetStatus(string text, Color color)
        {
            if (_statusLabel == null) return;
            _statusLabel.text = $"Status: {text}";
            _statusLabel.style.color = color;
            AddLog($"Status changed: {text}");
        }

        public void SetTransferProgress(float percent)
        {
            if (_transferProgress == null) return;
            if (percent < 0)
            {
                _transferProgress.style.display = DisplayStyle.None;
            }
            else
            {
                _transferProgress.style.display = DisplayStyle.Flex;
                _transferProgress.value = percent * 100f;
                _transferProgress.title = $"Save Transfer: {(percent * 100f):F0}%";
            }
        }



        // ---------------------------------------------------------------
        // NetworkManager event subscriptions
        // ---------------------------------------------------------------

        private void SubscribeToNetworkEvents()
        {
            try
            {
                var nm = NetworkManager.Instance;
                if (nm == null) return;

                nm.OnPlayerJoined += info =>
                {
                    if (nm.IsServer)
                        SetStatus($"HOSTING ({nm.Players.Count} players)", Color.green);
                    else if (nm.IsClient)
                        SetStatus("CONNECTED", Color.green);
                };

                nm.OnPlayerLeft += info =>
                {
                    if (nm.IsServer)
                        SetStatus($"HOSTING ({nm.Players.Count} players)", Color.green);
                };

                nm.OnPlayerListUpdated += () =>
                {
                    if (nm.IsClient && !nm.IsServer)
                    {
                        SetStatus("CONNECTED", Color.green);
                    }

                };

                nm.OnSaveLoadStarted += () =>
                {
                    SetTransferProgress(0f);
                    AddLog("Save transfer started...");
                };

                nm.OnSaveTransferProgress += (progress) =>
                {
                    SetTransferProgress(progress);
                };

                nm.OnSaveLoadComplete += () =>
                {
                    SetTransferProgress(-1f);
                    AddLog("Save transfer complete! Loading game...");
                };
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogWarning(
                    $"[MultiplayerMenu] Could not subscribe to network events: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            // Best-effort unsubscribe (events are delegates, so GC handles it if we miss).
            try
            {
                var nm = NetworkManager.Instance;
                if (nm != null)
                {
                    nm.OnPlayerJoined -= _ => { };
                    nm.OnPlayerLeft -= _ => { };
                    nm.OnPlayerListUpdated -= () => { };
                }
            }
            catch
            {
                // Swallow – the game may be shutting down.
            }
        }
    }
}
