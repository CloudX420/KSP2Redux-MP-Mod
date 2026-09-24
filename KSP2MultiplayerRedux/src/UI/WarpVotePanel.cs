using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using UitkForKsp2.Controls;
using KSP2MultiplayerRedux.Networking;

namespace KSP2MultiplayerRedux.UI
{
    /// <summary>
    /// Displays a time-limited warp vote dialog. Players can approve or deny a
    /// warp-rate change request. Shows per-player vote status and a countdown timer.
    /// </summary>
    public class WarpVotePanel : MonoBehaviour
    {
        // ---------------------------------------------------------------
        // Singleton
        // ---------------------------------------------------------------
        public static WarpVotePanel Instance { get; private set; }

        private UIDocument _document;
        private AppShell _appShell;

        // Content elements
        private Label _requestLabel;
        private ScrollView _voterScrollView;
        private Label _countdownLabel;
        private Button _approveButton;
        private Button _denyButton;
        private Label _resultLabel;

        // Vote state
        private bool _isWindowOpen;
        private bool _voteActive;
        private float _countdownRemaining;
        private const float VoteDurationSeconds = 30f;

        private string _currentRequesterName;
        private int _currentTargetRate;

        /// <summary>
        /// Gets or sets whether the warp vote window is visible.
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
            _document = gameObject.AddComponent<UIDocument>();

            var panelSettings = Resources.FindObjectsOfTypeAll<PanelSettings>()
                .FirstOrDefault(p => p.name.Contains("Kerbal") || p.name.Contains("Theme"));

            if (panelSettings == null)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError(
                    "[WarpVotePanel] Could not find KSP2 PanelSettings. UI will not be created.");
                return;
            }

            _document.panelSettings = panelSettings;

            // --- Build AppShell ---
            _appShell = new AppShell
            {
                Title = "Warp Vote",
                UppercaseTitle = true
            };

            _appShell.style.width = 380;
            _appShell.style.height = Length.Auto();
            _appShell.style.position = Position.Absolute;
            _appShell.style.left = 400;
            _appShell.style.top = 300;

            _appShell.CloseClicked += () =>
            {
                IsWindowOpen = false;
                _voteActive = false;
            };

            // --- Content ---
            var content = new VisualElement();
            content.style.paddingTop = 10;
            content.style.paddingBottom = 12;
            content.style.paddingLeft = 12;
            content.style.paddingRight = 12;

            // Request description label
            _requestLabel = new Label("Waiting for vote request...");
            _requestLabel.style.fontSize = 14;
            _requestLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _requestLabel.style.whiteSpace = WhiteSpace.Normal;
            _requestLabel.style.marginBottom = 10;
            content.Add(_requestLabel);

            // Divider
            var divider = new VisualElement();
            divider.style.height = 1;
            divider.style.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            divider.style.marginBottom = 6;
            content.Add(divider);

            // Voter status list header
            var voterHeader = new Label("Votes");
            voterHeader.style.fontSize = 13;
            voterHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            voterHeader.style.marginBottom = 4;
            content.Add(voterHeader);

            // ScrollView for voter status rows
            _voterScrollView = new ScrollView(ScrollViewMode.Vertical);
            _voterScrollView.style.maxHeight = 200;
            _voterScrollView.style.minHeight = 30;
            _voterScrollView.style.marginBottom = 10;
            content.Add(_voterScrollView);

            // Countdown timer label
            _countdownLabel = new Label("Time remaining: 30s");
            _countdownLabel.style.fontSize = 14;
            _countdownLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _countdownLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _countdownLabel.style.marginBottom = 10;
            _countdownLabel.style.color = Color.white;
            content.Add(_countdownLabel);

            // Result label (shown briefly after vote completes)
            _resultLabel = new Label();
            _resultLabel.style.fontSize = 16;
            _resultLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _resultLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _resultLabel.style.marginBottom = 8;
            _resultLabel.style.display = DisplayStyle.None;
            content.Add(_resultLabel);

            // --- Approve / Deny buttons ---
            var buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            buttonRow.style.justifyContent = Justify.SpaceAround;

            _approveButton = new Button(OnApproveClicked) { text = "✓  Approve" };
            _approveButton.AddToClassList("button");
            _approveButton.style.flexGrow = 1;
            _approveButton.style.marginRight = 6;
            _approveButton.style.color = new Color(0.2f, 1f, 0.3f);
            buttonRow.Add(_approveButton);

            _denyButton = new Button(OnDenyClicked) { text = "✗  Deny" };
            _denyButton.AddToClassList("button");
            _denyButton.style.flexGrow = 1;
            _denyButton.style.marginLeft = 6;
            _denyButton.style.color = new Color(1f, 0.35f, 0.3f);
            buttonRow.Add(_denyButton);

            content.Add(buttonRow);

            _appShell.Add(content);
            _document.rootVisualElement.Add(_appShell);

            // Start hidden.
            _appShell.style.display = DisplayStyle.None;
            _isWindowOpen = false;
            _voteActive = false;

            // Subscribe to network events.
            SubscribeToNetworkEvents();

            KSP2MultiplayerReduxPlugin.Logger.LogInfo("[WarpVotePanel] UI initialized.");
        }

        private void Update()
        {
            if (!_voteActive) return;

            _countdownRemaining -= Time.deltaTime;

            if (_countdownRemaining <= 0f)
            {
                _countdownRemaining = 0f;
                _countdownLabel.text = "Time remaining: 0s";
                AutoCloseVote();
                return;
            }

            // Update countdown display. Color shifts from white → yellow → red.
            int secondsLeft = Mathf.CeilToInt(_countdownRemaining);
            _countdownLabel.text = $"Time remaining: {secondsLeft}s";

            if (_countdownRemaining < 10f)
                _countdownLabel.style.color = Color.red;
            else if (_countdownRemaining < 20f)
                _countdownLabel.style.color = Color.yellow;
            else
                _countdownLabel.style.color = Color.white;
        }

        // ---------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------

        /// <summary>
        /// Shows the vote dialog, populates the request info, and starts the countdown.
        /// </summary>
        public void ShowVote(string requesterName, int targetRate)
        {
            _currentRequesterName = requesterName;
            _currentTargetRate = targetRate;

            _requestLabel.text = $"{requesterName} wants to change warp to {targetRate}x";
            _countdownRemaining = VoteDurationSeconds;
            _countdownLabel.text = $"Time remaining: {Mathf.CeilToInt(VoteDurationSeconds)}s";
            _countdownLabel.style.color = Color.white;

            _resultLabel.style.display = DisplayStyle.None;

            // Re-enable buttons.
            _approveButton.SetEnabled(true);
            _denyButton.SetEnabled(true);

            _voteActive = true;
            IsWindowOpen = true;

            // Populate initial voter status.
            try
            {
                UpdateVoterStatus(NetworkManager.Instance?.Players);
            }
            catch
            {
                // Non-critical; list will update on next event.
            }

            KSP2MultiplayerReduxPlugin.Logger.LogInfo(
                $"[WarpVotePanel] Vote started: {requesterName} -> {targetRate}x");
        }

        /// <summary>
        /// Refreshes the per-player vote status display.
        /// </summary>
        public void UpdateVoterStatus(Dictionary<int, PlayerInfo> players)
        {
            if (_voterScrollView == null) return;

            _voterScrollView.Clear();

            if (players == null || players.Count == 0)
                return;

            foreach (var kvp in players)
            {
                var player = kvp.Value;
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.paddingTop = 2;
                row.style.paddingBottom = 2;

                // Player name
                var nameLabel = new Label(player.SteamName ?? $"Player {player.PeerId}");
                nameLabel.style.flexGrow = 1;
                nameLabel.style.fontSize = 13;
                row.Add(nameLabel);

                // Vote status icon
                var statusLabel = new Label();
                statusLabel.style.fontSize = 14;
                statusLabel.style.width = 30;
                statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;

                // WarpVote: true = approved, false = denied, null = pending
                // (Assuming WarpVote is a nullable bool: bool?)
                if (player.WarpVote == true)
                {
                    statusLabel.text = "✓";
                    statusLabel.style.color = Color.green;
                }
                else if (player.WarpVote == false)
                {
                    statusLabel.text = "✗";
                    statusLabel.style.color = Color.red;
                }
                else
                {
                    statusLabel.text = "⏳";
                    statusLabel.style.color = Color.yellow;
                }

                row.Add(statusLabel);
                _voterScrollView.Add(row);
            }
        }

        /// <summary>
        /// Shows the vote result briefly, then hides the panel.
        /// </summary>
        public void CloseVote(bool passed)
        {
            _voteActive = false;

            // Disable buttons.
            _approveButton.SetEnabled(false);
            _denyButton.SetEnabled(false);

            // Show result.
            _resultLabel.style.display = DisplayStyle.Flex;
            if (passed)
            {
                _resultLabel.text = "VOTE PASSED ✓";
                _resultLabel.style.color = Color.green;
            }
            else
            {
                _resultLabel.text = "VOTE FAILED ✗";
                _resultLabel.style.color = Color.red;
            }

            _countdownLabel.text = "Vote complete";
            _countdownLabel.style.color = new Color(0.7f, 0.7f, 0.7f);

            KSP2MultiplayerReduxPlugin.Logger.LogInfo(
                $"[WarpVotePanel] Vote result: {(passed ? "PASSED" : "FAILED")}");

            // Hide panel after a short delay.
            Invoke(nameof(HideAfterResult), 3f);
        }

        // ---------------------------------------------------------------
        // Button callbacks
        // ---------------------------------------------------------------

        private void OnApproveClicked()
        {
            try
            {
                NetworkManager.Instance.CastWarpVote(true);
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError(
                    $"[WarpVotePanel] Error casting approve vote: {ex.Message}");
            }

            _approveButton.SetEnabled(false);
            _denyButton.SetEnabled(false);
        }

        private void OnDenyClicked()
        {
            try
            {
                NetworkManager.Instance.CastWarpVote(false);
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError(
                    $"[WarpVotePanel] Error casting deny vote: {ex.Message}");
            }

            _approveButton.SetEnabled(false);
            _denyButton.SetEnabled(false);
        }

        // ---------------------------------------------------------------
        // Internal helpers
        // ---------------------------------------------------------------

        private void AutoCloseVote()
        {
            _voteActive = false;
            _approveButton.SetEnabled(false);
            _denyButton.SetEnabled(false);
            _countdownLabel.text = "Vote timed out";
            _countdownLabel.style.color = new Color(0.7f, 0.7f, 0.7f);

            KSP2MultiplayerReduxPlugin.Logger.LogInfo("[WarpVotePanel] Vote timed out locally.");

            Invoke(nameof(HideAfterResult), 2f);
        }

        private void HideAfterResult()
        {
            IsWindowOpen = false;
        }

        private void SubscribeToNetworkEvents()
        {
            try
            {
                var nm = NetworkManager.Instance;
                if (nm == null) return;

                nm.OnWarpVoteRequested += packet =>
                {
                    ShowVote(packet.RequesterName, packet.TargetWarpRate);
                };

                nm.OnWarpVoteCompleted += passed =>
                {
                    CloseVote(passed);
                };

                nm.OnPlayerListUpdated += () =>
                {
                    if (_voteActive)
                    {
                        try
                        {
                            UpdateVoterStatus(NetworkManager.Instance?.Players);
                        }
                        catch
                        {
                            // Non-critical.
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogWarning(
                    $"[WarpVotePanel] Could not subscribe to network events: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            CancelInvoke();
        }
    }
}
