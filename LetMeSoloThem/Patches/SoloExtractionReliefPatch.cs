using System;
using HarmonyLib;

namespace LetMeSoloThem.Patches;

// Solo Extraction Relief: solo-gated easing of the post-final-extraction monster surge.
// Once RoundDirector.allExtractionPointsCompleted is true, vanilla EnemyDirector.Update() (1) forces
// enemy respawn timers toward ~1s and (2) repeatedly pings the player's room to every nearby enemy.
// This file softens both: two EnemyParent Postfixes enforce a respawn-time floor (Task 2), and one
// EnemyDirector.SetInvestigate Prefix drops the "PlayerRoom" pings while keeping the StartRoom
// lure-to-truck (Task 3). All gated on ReliefActive(). Host-authoritative (solo = host); no RPC.
// Auto-registered by Plugin.Awake's _harmony.PatchAll().
public static class SoloExtractionReliefPatch
{
    // Internal vanilla fields are not accessible cross-assembly — read them via FieldRef
    // (same approach as EnemyDirectorPatch.SpawnIdlePauseTimerRef).
    private static readonly AccessTools.FieldRef<RoundDirector, bool> AllExtractionsDoneRef =
        AccessTools.FieldRefAccess<RoundDirector, bool>("allExtractionPointsCompleted");

    // Shared gate: relief is active only when enabled, all extractions are done, and we're solo
    // (or WorksInMultiplayer + host). Returns false rather than throwing if singletons aren't ready.
    internal static bool ReliefActive()
    {
        try
        {
            if (!Plugin.SoloExtractionEnabled.Value) return false;

            var round = RoundDirector.instance;
            if (round == null || !AllExtractionsDoneRef(round)) return false;

            if (Plugin.SoloExtractionWorksInMultiplayer.Value)
                return SemiFunc.IsMasterClientOrSingleplayer();

            int playerCount = SemiFunc.PlayerGetList()?.Count ?? 1;
            return playerCount <= 1;
        }
        catch
        {
            return false;
        }
    }

    // Lever 1a: catch the EnemyDirector "respawn now" command (DespawnedTimerSet(0f)) and raise it to
    // the configured floor.
    [HarmonyPatch(typeof(EnemyParent), nameof(EnemyParent.DespawnedTimerSet))]
    [HarmonyPostfix]
    public static void DespawnedTimerSetPostfix(EnemyParent __instance)
    {
        ApplyRespawnFloor(__instance);
    }

    // Lever 1b: catch the despawnedDecreaseMultiplier=0 collapse-to-1s path. Runs after vanilla
    // computes and clamps DespawnedTimer (including the *=3f valuable-dropper path).
    [HarmonyPatch(typeof(EnemyParent), nameof(EnemyParent.Despawn))]
    [HarmonyPostfix]
    public static void DespawnPostfix(EnemyParent __instance)
    {
        ApplyRespawnFloor(__instance);
    }

    // DespawnedTimer (public float on EnemyParent) is only consumed while the enemy is despawned, so
    // clamping it here is safe regardless of Spawned state. No log — these fire per enemy per respawn
    // cycle and would spam (quiet-mode policy).
    private static void ApplyRespawnFloor(EnemyParent enemyParent)
    {
        try
        {
            int floor = Plugin.SoloExtractionRespawnFloorSeconds.Value;
            if (floor <= 0) return;          // lever disabled
            if (enemyParent == null) return;
            if (!ReliefActive()) return;

            if (enemyParent.DespawnedTimer < floor)
                enemyParent.DespawnedTimer = floor;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[SoloExtraction] respawn-floor postfix threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
