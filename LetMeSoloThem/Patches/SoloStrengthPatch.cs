using System;
using UnityEngine;

namespace LetMeSoloThem.Patches;

// Auto-grants free Strength upgrade level(s) at run start, plus a configurable per-round drip.
// Uses the vanilla PunManager.UpgradePlayerGrabStrength API — same path the in-game shop's
// "Item Upgrade Player Grab Strength" upgrade takes when picked up, so the grant is mechanically
// indistinguishable from buying Strength in the shop. Vanilla StatsUI panel pops in for 5s after
// each grant as visual feedback (same UI the shop pickup triggers). Solo-gated by default
// (player count == 1); WorksInMultiplayer config exposes host-grant in MP lobbies.
public static class SoloStrengthGranter
{
    private static EnemyDirector _lastSeenDirector;

    public static void TryGrantOnTick()
    {
        // Gating (silent early returns — fire every frame, no spam).
        if (!Plugin.SoloStrengthEnabled.Value) return;
        if (!SemiFunc.RunIsLevel()) return;
        if (LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated) return;
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        if (!Plugin.SoloStrengthWorksInMultiplayer.Value)
        {
            var players = SemiFunc.PlayerGetList();
            if (players != null && players.Count > 1) return;
        }

        // New-level detection. Do NOT assign _lastSeenDirector yet — only after we've
        // decided this tick owns this level's grant (or determined nothing's to be done).
        var ed = EnemyDirector.instance;
        if (ed == null || ReferenceEquals(ed, _lastSeenDirector)) return;

        // Determine grant amount.
        bool isRunStart = RunManager.instance != null && RunManager.instance.levelsCompleted == 0;
        int grantAmount = isRunStart
            ? Plugin.SoloStrengthStartingStrength.Value
            : Plugin.SoloStrengthPerRound.Value;
        if (grantAmount <= 0)
        {
            // Pin: no grant for this level, don't keep re-checking every frame.
            _lastSeenDirector = ed;
            Plugin.Log.LogDebug($"[SoloStrength] Skipped grant (amount=0, runStart={isRunStart})");
            return;
        }

        // Pre-grant null guards — these are transient frame-state issues (player can load in
        // a frame late), so return silently and retry next tick rather than logging + pinning.
        var pc = PlayerController.instance;
        if (pc == null || pc.playerAvatarScript == null) return;

        string steamID = SemiFunc.PlayerGetSteamID(pc.playerAvatarScript);
        if (string.IsNullOrEmpty(steamID)) return;

        if (StatsManager.instance == null) return;
        if (PunManager.instance == null) return;

        // Commit to the grant. Both success and exception are terminal for this level —
        // assign _lastSeenDirector in either branch so we don't spam-retry.
        try
        {
            // Ensure dict key exists before calling PunManager API. PunManager reads
            // playerUpgradeStrength[steamID] without ContainsKey — would throw
            // KeyNotFoundException if the local player hasn't been initialized in the
            // dict yet (which can happen on first-frame-of-new-EnemyDirector, before
            // vanilla shop flow has run).
            if (!StatsManager.instance.playerUpgradeStrength.ContainsKey(steamID))
            {
                StatsManager.instance.playerUpgradeStrength[steamID] = 0;
            }

            // Vanilla call: adds grantAmount to playerUpgradeStrength[steamID], updates
            // physGrabber.grabStrength immediately via UpdateGrabStrengthRightAway,
            // broadcasts RPC to clients if in MP.
            PunManager.instance.UpgradePlayerGrabStrength(steamID, grantAmount);

            _lastSeenDirector = ed; // mark complete only after the grant lands

            int newTotal = StatsManager.instance.playerUpgradeStrength[steamID];
            Plugin.Log.LogInfo($"[SoloStrength] Granted +{grantAmount} Strength to {steamID} (runStart={isRunStart}, newTotal={newTotal})");
        }
        catch (Exception ex)
        {
            _lastSeenDirector = ed; // pin even on exception — don't retry-spam every frame
            Plugin.Log.LogWarning($"[SoloStrength] Grant threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // UI feedback (inner try-catch — UI failure must not roll back the grant).
        // StatsUI.Fetch() rebuilds the panel's text from current StatsManager state;
        // ShowStats() triggers the 5s pop-in animation. Same two calls vanilla makes
        // after a shop upgrade pickup (ItemUpgrade.cs:172-173).
        try
        {
            if (StatsUI.instance != null)
            {
                StatsUI.instance.Fetch();
                StatsUI.instance.ShowStats();
            }
            else
            {
                Plugin.Log.LogWarning("[SoloStrength] StatsUI.instance null — grant applied but no panel shown");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[SoloStrength] StatsUI display threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
