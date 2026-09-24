using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using KSP.Game;
using KSP.Game.Science;
using KSP2MultiplayerRedux.Networking;

namespace KSP2MultiplayerRedux.Sync
{
    /// <summary>
    /// Syncs tech-tree unlocks and submitted science reports between players by STATE DIFFING.
    ///
    /// The old approach matched KSP2 message type-names by substring ("TechNode"+"Unlock"), but the
    /// real messages are named differently (e.g. TechResearchedMessage), so nothing was ever detected.
    /// Instead we poll the authoritative science state each second and broadcast the delta:
    ///   - Tech:    ScienceManager.IsNodeUnlocked(id) over every node in TechNodeDataStore.
    ///   - Science: ScienceManager.GetSubmittedResearchReports() keyed by ResearchReportKey.
    /// Applying a remote change updates our "known" set BEFORE the next poll so it isn't re-broadcast
    /// (no echo loop). Verified API via KSP.Game.Science.ScienceManager.
    /// </summary>
    public class CampaignSyncManager : MonoBehaviour
    {
        private const float PollInterval = 1f;
        private float _pollTimer;
        private bool _hooked;
        private bool _baselineSeeded;   // first poll records existing state silently (no broadcast)

        private readonly HashSet<string> _knownUnlockedNodes = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _knownReportKeys = new HashSet<string>(StringComparer.Ordinal);

        // Reflection fallbacks for enumerating all tech node IDs (TryGetTechNodeDataCollection)
        private MethodInfo _tryGetNodeCollection;

        private void Update()
        {
            var net = NetworkManager.Instance;
            if (net == null) return;
            if (!net.IsClient && !net.IsServer) return;

            if (!_hooked)
            {
                net.OnTechUnlockReceived += OnRemoteTechUnlock;
                net.OnScienceEarnedReceived += OnRemoteScienceEarned;
                _hooked = true;
                KSP2MultiplayerReduxPlugin.Logger.LogInfo("[CampaignSync] Hooked network events (state-diff sync).");
            }

            _pollTimer += Time.deltaTime;
            if (_pollTimer < PollInterval) return;
            _pollTimer = 0f;

            var science = GameManager.Instance?.Game?.ScienceManager;
            if (science == null) return;

            PollTechNodes(science);
            PollScienceReports(science);

            // After the first successful poll, everything currently unlocked is our baseline —
            // don't rebroadcast the whole existing tree, only future changes.
            _baselineSeeded = true;
        }

        // ── Tech tree ───────────────────────────────────────────────

        private void PollTechNodes(ScienceManager science)
        {
            var nodeIds = GetAllTechNodeIds(science);
            if (nodeIds == null) return;

            foreach (var id in nodeIds)
            {
                bool unlocked;
                try { unlocked = science.IsNodeUnlocked(id); }
                catch { continue; }

                if (!unlocked) continue;
                if (_knownUnlockedNodes.Contains(id)) continue;

                // Newly unlocked locally
                _knownUnlockedNodes.Add(id);
                if (_baselineSeeded)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[CampaignSync] Local tech unlocked: {id} — broadcasting.");
                    NetworkManager.Instance.BroadcastTechUnlock(new TechUnlockPacket { NodeId = id });
                }
            }
        }

        private IEnumerable<string> GetAllTechNodeIds(ScienceManager science)
        {
            try
            {
                var store = science.TechNodeDataStore;
                if (store == null) return null;

                if (_tryGetNodeCollection == null)
                    _tryGetNodeCollection = store.GetType().GetMethod("TryGetTechNodeDataCollection",
                        BindingFlags.Public | BindingFlags.Instance);

                if (_tryGetNodeCollection != null)
                {
                    var args = new object[] { null };
                    bool ok = (bool)_tryGetNodeCollection.Invoke(store, args);
                    if (ok && args[0] is System.Collections.IEnumerable coll)
                    {
                        var ids = new List<string>();
                        foreach (var node in coll)
                        {
                            var idField = node.GetType().GetField("ID");
                            if (idField != null && idField.GetValue(node) is string id && !string.IsNullOrEmpty(id))
                                ids.Add(id);
                        }
                        return ids;
                    }
                }
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogWarning($"[CampaignSync] Could not enumerate tech nodes: {ex.Message}");
            }
            return null;
        }

        private void OnRemoteTechUnlock(TechUnlockPacket packet)
        {
            if (string.IsNullOrEmpty(packet.NodeId)) return;

            var science = GameManager.Instance?.Game?.ScienceManager;
            if (science == null) return;

            // Record BEFORE applying so the next poll doesn't treat it as a local change and echo it back.
            _knownUnlockedNodes.Add(packet.NodeId);

            try
            {
                if (!science.IsNodeUnlocked(packet.NodeId))
                {
                    science.UnlockTechNode(packet.NodeId);
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[CampaignSync] Applied remote tech unlock: {packet.NodeId}");
                }
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError($"[CampaignSync] Error applying remote tech unlock {packet.NodeId}: {ex.Message}");
            }
        }

        // ── Science reports ─────────────────────────────────────────

        private void PollScienceReports(ScienceManager science)
        {
            List<CompletedResearchReport> reports;
            try { reports = science.GetSubmittedResearchReports(); }
            catch { return; }
            if (reports == null) return;

            foreach (var report in reports)
            {
                string key;
                try { key = report.ResearchReportKey; }
                catch { continue; }
                if (string.IsNullOrEmpty(key)) key = $"{report.ExperimentID}:{report.ResearchLocationID}";

                if (_knownReportKeys.Contains(key)) continue;
                _knownReportKeys.Add(key);

                if (_baselineSeeded)
                {
                    KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[CampaignSync] Local science report: {report.ExperimentID} ({report.FinalScienceValue}) — broadcasting.");
                    NetworkManager.Instance.BroadcastScienceEarned(new ScienceEarnedPacket
                    {
                        Amount = report.FinalScienceValue,
                        ExperimentID = report.ExperimentID ?? "",
                        ResearchLocationID = report.ResearchLocationID ?? "",
                        ReportType = (byte)report.ResearchReportType
                    });
                }
            }
        }

        private void OnRemoteScienceEarned(ScienceEarnedPacket packet)
        {
            if (string.IsNullOrEmpty(packet.ExperimentID)) return;

            var science = GameManager.Instance?.Game?.ScienceManager;
            if (science == null) return;

            string key = $"{packet.ExperimentID}:{packet.ResearchLocationID}";
            // Record before applying so our own poll doesn't rebroadcast it.
            _knownReportKeys.Add(key);

            try
            {
                var report = new CompletedResearchReport
                {
                    ExperimentID = packet.ExperimentID,
                    ResearchLocationID = packet.ResearchLocationID ?? "",
                    FinalScienceValue = packet.Amount,
                    ResearchReportType = (ScienceReportType)(packet.ReportType == 0 ? 1 : packet.ReportType),
                    WasRecovered = true
                };
                // Remember the real key too, once known
                try { if (!string.IsNullOrEmpty(report.ResearchReportKey)) _knownReportKeys.Add(report.ResearchReportKey); } catch { }

                science.TrySubmitCompletedResearchReport(report);
                KSP2MultiplayerReduxPlugin.Logger.LogInfo($"[CampaignSync] Applied remote science report: {packet.ExperimentID} (+{packet.Amount})");
            }
            catch (Exception ex)
            {
                KSP2MultiplayerReduxPlugin.Logger.LogError($"[CampaignSync] Error applying remote science: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            if (NetworkManager.Instance != null && _hooked)
            {
                NetworkManager.Instance.OnTechUnlockReceived -= OnRemoteTechUnlock;
                NetworkManager.Instance.OnScienceEarnedReceived -= OnRemoteScienceEarned;
            }
        }
    }
}
