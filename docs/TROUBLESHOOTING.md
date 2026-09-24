# Troubleshooting

The mod logs everything to KSP2's `Player.log`:

```text
%USERPROFILE%\AppData\LocalLow\Intercept Games\Kerbal Space Program 2\Player.log
```

Search for `[KSP2MultiplayerRedux]`. When reporting a bug, attach the whole file.

## Startup checklist

| Log line | Meaning |
|---|---|
| `KSP2 Multiplayer Redux vX.Y.Z pre-initialized` | Mod loaded. The version must match the server's. |
| `The script 'KSP2MultiplayerRedux.Networking.NetworkManager' could not be instantiated!` | Networking failed to load — almost always an old/unmerged DLL. Reinstall from the release zip. |
| `[MultiplayerMenu] UI initialized.` | Ctrl+M window is available. |

## Connecting

| Symptom | Likely cause |
|---|---|
| Ctrl+M does nothing | Mod not loaded (see startup checklist). |
| Status stays `CONNECTING…` | Wrong IP/port, UDP port not forwarded, server not running. |
| Immediately disconnected | Protocol mismatch — update both sides to the same release. |
| `SaveLoadManager is NULL` | You must be inside a campaign (at the KSC) before joining. |

## Join flow

| Log line | Meaning |
|---|---|
| `Save scrubbed for join: StartingGameState->KSC x1 …` | Save rewritten so you land at the KSC. `x0` means the field wasn't found — report it. |
| `Join complete — landed at KerbalSpaceCenter` | Correct outcome. |
| `Joined into a FLIGHT scene … forcing transition to the KSC` | Scrub was ignored; the fallback ran. |

## Seeing other players' vessels

Each remote vessel logs its own diagnostic trail:

| Log line | Meaning |
|---|---|
| `Tracking remote vessel: <player> in <ship>` | Their state packets are arriving. |
| `VIEW DIAG '<ship>': dist=… viewLoaded=… proximityLoader[…]` | One-shot snapshot when the vessel is first evaluated for rendering. |
| `'<ship>' within Nm — view strategy K starting…` | Attempting to build the 3D model (K = 0..3). |
| `✓ RENDERED '<ship>': N parts` | **Success — the craft is visible.** |
| `View for '<ship>' exists but is EMPTY` | Shell created without parts; escalating to the next strategy. |
| `✗ ALL view strategies exhausted for '<ship>'` | No API produced a model. Please report this line with the VIEW DIAG line. |

If a vessel shows in the map but not in flight, it is >15 km away or the view strategies failed.

## Science / tech

`[CampaignSync] Local tech unlocked: … — broadcasting` on the unlocking client and
`[CampaignSync] Applied remote tech unlock: …` on the others. If you see the first but not the
second, the packet was lost or the other client isn't connected.

## Known issues

- Remote vessels are on rails: no collisions or docking with other players' craft yet.
- Reverting a flight while others are watching your vessel can briefly desync until the next
  state packet.
- The listen-server mode (host inside the game) is less tested than the dedicated server.
- If the display name shows as `<username>-1234`, Steam wasn't detected; set a name in the
  multiplayer window.
