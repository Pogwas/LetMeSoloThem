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
        // Step 1: Gating (silent early returns — these fire every frame, no log spam).
        if (!Plugin.SoloStrengthEnabled.Value) return;
        if (!SemiFunc.RunIsLevel()) return;
        if (LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated) return;
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        // Solo-only mode in MP lobby check: if WorksInMultiplayer is false AND there are 2+ players,
        // bail. SemiFunc.PlayerGetList includes all players in the current room.
        if (!Plugin.SoloStrengthWorksInMultiplayer.Value)
        {
            var players = SemiFunc.PlayerGetList();
            if (players != null && players.Count > 1) return;
        }

        // Step 2: New-level detection via EnemyDirector instance change.
        // A new level instantiates a fresh EnemyDirector; ReferenceEquals catches that.
        var ed = EnemyDirector.instance;
        if (ed == null || ReferenceEquals(ed, _lastSeenDirector)) return;
        _lastSeenDirector = ed;

        // Step 3: Determine grant amount.
        bool isRunStart = RunManager.instance != null && RunManager.instance.levelsCompleted == 0;
        int grantAmount = isRunStart
            ? Plugin.SoloStrengthStartingStrength.Value
            : Plugin.SoloStrengthPerRound.Value;
        if (grantAmount <= 0)
        {
            Plugin.Log.LogDebug($"[SoloStrength] Skipped grant (amount=0, runStart={isRunStart})");
            return;
        }

        // Step 4-6: Resolve player, init dict, grant via vanilla API.
        try
        {
            var pc = PlayerController.instance;
            if (pc == null || pc.playerAvatarScript == null)
            {
                Plugin.Log.LogDebug("[SoloStrength] PlayerController not ready — skipping grant");
                return;
            }

            string steamID = SemiFunc.PlayerGetSteamID(pc.playerAvatarScript);
            if (string.IsNullOrEmpty(steamID))
            {
                Plugin.Log.LogDebug("[SoloStrength] SteamID empty — skipping grant");
                return;
            }

            if (StatsManager.instance == null)
            {
                Plugin.Log.LogDebug("[SoloStrength] StatsManager.instance null — skipping grant");
                return;
            }
            // Ensure dict key exists before calling PunManager API. PunManager reads
            // playerUpgradeStrength[steamID] without ContainsKey — would throw KeyNotFoundException
            // if the local player hasn't been initialized in the dict yet (which can happen on
            // first-frame-of-new-EnemyDirector, before vanilla shop flow has run).
            if (!StatsManager.instance.playerUpgradeStrength.ContainsKey(steamID))
            {
                StatsManager.instance.playerUpgradeStrength[steamID] = 0;
            }

            if (PunManager.instance == null)
            {
                Plugin.Log.LogDebug("[SoloStrength] PunManager.instance null — skipping grant");
                return;
            }

            // Vanilla call: adds grantAmount to playerUpgradeStrength[steamID], updates
            // physGrabber.grabStrength immediately via UpdateGrabStrengthRightAway, broadcasts
            // RPC to clients if in MP.
            PunManager.instance.UpgradePlayerGrabStrength(steamID, grantAmount);

            int newTotal = StatsManager.instance.playerUpgradeStrength[steamID];
            Plugin.Log.LogInfo($"[SoloStrength] Granted +{grantAmount} Strength to {steamID} (runStart={isRunStart}, newTotal={newTotal})");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[SoloStrength] Grant threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Step 7: UI feedback (inner try-catch — UI failure must not roll back the grant).
        // StatsUI.Fetch() rebuilds the panel's text from current StatsManager state; ShowStats()
        // triggers the 5s pop-in animation. Same two calls vanilla makes after a shop upgrade
        // pickup (ItemUpgrade.cs:172-173).
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
