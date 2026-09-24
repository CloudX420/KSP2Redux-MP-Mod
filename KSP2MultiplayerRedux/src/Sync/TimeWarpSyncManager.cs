using UnityEngine;
using KSP.Game;
using KSP2MultiplayerRedux.Networking;

namespace KSP2MultiplayerRedux.Sync
{
    public class TimeWarpSyncManager : MonoBehaviour
    {
        private int _lastWarpRate;
        private bool _suppressLocalWarpDetection;

        private void Start()
        {
            KSP2MultiplayerReduxPlugin.Logger.LogInfo("TimeWarpSyncManager initialized.");
            NetworkManager.Instance.OnWarpApply += ApplyRemoteTimeWarp;
        }

        private void Update()
        {
            if (GameManager.Instance?.Game?.ViewController?.TimeWarp is null)
                return;

            if (NetworkManager.Instance is null || (!NetworkManager.Instance.IsServer && !NetworkManager.Instance.IsClient))
                return;

            int currentRate = GameManager.Instance.Game.ViewController.TimeWarp.CurrentRateIndex;

            if (currentRate != _lastWarpRate)
            {
                if (_suppressLocalWarpDetection)
                {
                    _lastWarpRate = currentRate;
                    _suppressLocalWarpDetection = false;
                    return;
                }

                if (NetworkManager.Instance.Players.Count <= 1)
                {
                    _lastWarpRate = currentRate;
                    return;
                }

                // DROPPING to 1x (index 0) or lower is always allowed — no vote needed.
                // Any player can immediately slow down warp for safety.
                if (currentRate <= 0)
                {
                    _lastWarpRate = currentRate;
                    // Broadcast to all clients to also drop out of warp
                    if (NetworkManager.Instance.IsServer)
                    {
                        var packet = new WarpApplyPacket { WarpRateIndex = 0 };
                        byte[] data = NetworkManager.Instance.Server.SerializePacket(w => packet.Serialize(w));
                        NetworkManager.Instance.Server.SendToAll(data);
                    }
                    else
                    {
                        // Client can just request it — server will relay
                        NetworkManager.Instance.RequestWarpVote(0);
                    }
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo("Warp dropped to 1x — no vote required.");
                    return;
                }

                // INCREASING warp requires a vote
                GameManager.Instance.Game.ViewController.TimeWarp.SetRateIndex(_lastWarpRate, true);
                NetworkManager.Instance.RequestWarpVote(currentRate);
                KSP2MultiplayerReduxPlugin.Logger.LogInfo(
                    $"Local warp change detected (index {currentRate}). Reverted to {_lastWarpRate} and requested warp vote.");
            }
        }

        private void ApplyRemoteTimeWarp(int rateIndex)
        {
            _suppressLocalWarpDetection = true;
            GameManager.Instance.Game.ViewController.TimeWarp.SetRateIndex(rateIndex, true);
            _lastWarpRate = rateIndex;
            KSP2MultiplayerReduxPlugin.Logger.LogInfo($"Applied remote time warp rate index: {rateIndex}");
        }
    }
}
