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
    // (same approach as EnemyDirectorStartPatch.SpawnIdlePauseTimerRef).
    private static readonly AccessTools.FieldRef<RoundDirector, bool> AllExtractionsDoneRef =
        AccessTools.FieldRefAccess<RoundDirector, bool>("allExtractionPointsCompleted");

    // EnemyDirector.extractionsDoneState is internal; the enum type EnemyDirector.ExtractionsDoneState
    // is public, so we can name it and read the field via FieldRef.
    private static readonly AccessTools.FieldRef<EnemyDirector, EnemyDirector.ExtractionsDoneState> ExtractionStateRef =
        AccessTools.FieldRefAccess<EnemyDirector, EnemyDirector.ExtractionsDoneState>("extractionsDoneState");

    // The extraction pings (StartRoom + PlayerRoom) use range 100f; the baseline EnemyDirector
    // investigate (enemyActionAmount overflow) uses float.MaxValue. Anything at/under this threshold is
    // an extraction ping; we only suppress those, and only in the PlayerRoom phase.
    private const float ExtractionPingRangeMax = 150f;

    // Latched so the "first ping suppressed" debug fires once per arm (reset by OnReliefArmed, which
    // SoloGraceHud calls on the false->true relief transition). Verification aid only.
    private static bool _pingSuppressLogged;

    // TEMP diagnostic: tracks the last extraction-ping state we logged, so we emit one line each time the
    // StartRoom->PlayerRoom transition is observed during a SetInvestigate call. Lets a playtest see
    // whether the PlayerRoom phase is ever reached. -1 = sentinel (not yet seen). Remove once confirmed.
    private static int _lastDiagState = -1;

    // Called by SoloGraceHud when relief arms (final extraction reached) so the per-arm ping log resets.
    internal static void OnReliefArmed()
    {
        _pingSuppressLogged = false;
        _lastDiagState = -1;
    }

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

    // Lever 2: suppress the repeating PlayerRoom pings (the "pathfind to YOUR room" broadcasts) while
    // keeping the StartRoom lure-to-truck and the baseline (float.MaxValue) investigate. Patched by
    // string name because SetInvestigate's accessibility is not guaranteed public. Returning false
    // skips the original.
    [HarmonyPatch(typeof(EnemyDirector), "SetInvestigate")]
    [HarmonyPrefix]
    public static bool SetInvestigatePrefix(EnemyDirector __instance, float radius)
    {
        try
        {
            if (!Plugin.SoloExtractionSuppressPings.Value) return true;
            if (__instance == null) return true;
            if (radius > ExtractionPingRangeMax) return true;   // baseline float.MaxValue investigate — leave it
            if (!ReliefActive()) return true;

            var state = ExtractionStateRef(__instance);
            // TEMP diagnostic: log each extraction-ping state the first time we see it during an arm, so a
            // playtest can tell whether the PlayerRoom phase is ever reached. Remove once confirmed.
            if ((int)state != _lastDiagState)
            {
                _lastDiagState = (int)state;
                Plugin.Log.LogDebug($"[SoloExtraction] extraction-ping observed in state {state} (radius={radius:F0})");
            }

            if (state != EnemyDirector.ExtractionsDoneState.PlayerRoom)
                return true;                                   // StartRoom lure-to-truck — leave it

            // PlayerRoom ping — suppress. Log once per arm so a playtest can confirm it actually fired.
            if (!_pingSuppressLogged)
            {
                _pingSuppressLogged = true;
                Plugin.Log.LogDebug("[SoloExtraction] suppressing PlayerRoom investigate ping(s) — first this arm");
            }
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[SoloExtraction] SetInvestigate prefix threw: {ex.GetType().Name}: {ex.Message}");
            return true;    // on any error, let vanilla run
        }
    }
}
