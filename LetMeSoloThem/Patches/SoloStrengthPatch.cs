using System;
using HarmonyLib;
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
    // -1 sentinel = no grant yet this run. We use RunManager.levelsCompleted as the
    // level-detection key rather than EnemyDirector.instance, because ED flickers between
    // two instances during level boot (lobby ED dying while level ED comes online),
    // which caused double-grants in v0.3.1 playtest (+2 fired twice on level 1).
    // levelsCompleted is the atomic per-level counter; only changes once per actual
    // level transition, and resets to 0 via RunManager.ResetProgress on Backspace /
    // game-over / new-game. The ResetProgress Postfix below clears _lastGrantedLevel
    // back to -1 so a fresh run on level 1 (levelsCompleted 0→0) still re-fires.
    internal const int NoLevelGrantedYet = -1;
    internal static int _lastGrantedLevel = NoLevelGrantedYet;

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

        if (RunManager.instance == null) return;
        int currentLevel = RunManager.instance.levelsCompleted;
        if (currentLevel == _lastGrantedLevel) return;

        // Determine grant amount.
        bool isRunStart = currentLevel == 0;
        int grantAmount = isRunStart
            ? Plugin.SoloStrengthStartingStrength.Value
            : Plugin.SoloStrengthPerRound.Value;
        if (grantAmount <= 0)
        {
            // Pin: no grant for this level, don't keep re-checking every frame.
            _lastGrantedLevel = currentLevel;
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
        // assign _lastGrantedLevel in either branch so we don't spam-retry.
        try
        {
            // Ensure dict key exists before calling PunManager API. PunManager reads
            // playerUpgradeStrength[steamID] without ContainsKey — would throw
            // KeyNotFoundException if the local player hasn't been initialized in the
            // dict yet (which can happen on first-frame-of-new-level, before vanilla
            // shop flow has run).
            if (!StatsManager.instance.playerUpgradeStrength.ContainsKey(steamID))
            {
                StatsManager.instance.playerUpgradeStrength[steamID] = 0;
            }

            // Vanilla call: adds grantAmount to playerUpgradeStrength[steamID], updates
            // physGrabber.grabStrength immediately via UpdateGrabStrengthRightAway,
            // broadcasts RPC to clients if in MP.
            PunManager.instance.UpgradePlayerGrabStrength(steamID, grantAmount);

            _lastGrantedLevel = currentLevel; // mark complete only after the grant lands

            int newTotal = StatsManager.instance.playerUpgradeStrength[steamID];
            Plugin.Log.LogDebug($"[SoloStrength] Granted +{grantAmount} Strength to {steamID} (runStart={isRunStart}, newTotal={newTotal})");
        }
        catch (Exception ex)
        {
            _lastGrantedLevel = currentLevel; // pin even on exception — don't retry-spam every frame
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

// Resets the per-level grant tracker on any run-reset event (Backspace, game-over via
// arena-fail, quit-to-menu). Without this, a Backspace-restart from level 1 (where
// levelsCompleted is 0 both before and after) would skip the run-start grant since the
// level number didn't change. Vanilla ResetProgress is the single funnel for all reset
// paths (RunManager.cs:410, called from line 157 Backspace and line 212 arena-fail).
[HarmonyPatch(typeof(RunManager), nameof(RunManager.ResetProgress))]
internal static class RunManagerResetProgressPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        SoloStrengthGranter._lastGrantedLevel = SoloStrengthGranter.NoLevelGrantedYet;
        Plugin.Log.LogDebug("[SoloStrength] ResetProgress: cleared _lastGrantedLevel");
    }
}
