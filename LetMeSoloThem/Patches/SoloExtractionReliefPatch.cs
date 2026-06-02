using System;
using HarmonyLib;

namespace LetMeSoloThem.Patches;

// Solo Extraction Relief: solo-gated easing of the post-final-extraction monster swarm.
// Once RoundDirector.allExtractionPointsCompleted is true, vanilla EnemyDirector.Update() repeatedly
// "pings" the player's current room to every nearby enemy (a 100f pathfind broadcast), herding the
// whole map onto the lone player as they escape to the truck. This suppresses that omniscient
// "pathfind to YOUR room" broadcast while leaving intact: the initial ~10s StartRoom lure toward the
// truck, the baseline (float.MaxValue) investigate, and ALL noise reactions (gunshots, dropped/bumped
// objects, the extraction point) — so enemies still hear you, they just aren't magically herded onto
// your exact location. Gated on ReliefActive(). Host-authoritative (solo = host); no RPC.
// Auto-registered by Plugin.Awake's _harmony.PatchAll().
//
// NOTE: the bare [HarmonyPatch] on the class is REQUIRED. PatchAll() only discovers classes annotated
// with [HarmonyPatch]; without it, the per-method [HarmonyPatch(...)] attribute below is silently
// skipped (no error) and the patch never applies.
[HarmonyPatch]
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
    // investigate (enemyActionAmount overflow) uses float.MaxValue. We only suppress the PlayerRoom
    // ping, so anything above this threshold (the baseline) is left alone.
    private const float ExtractionPingRangeMax = 150f;

    // Latched so the "first ping suppressed" debug fires once per arm (reset by OnReliefArmed, which
    // SoloGraceHud calls on the false->true relief transition). Verification aid only.
    private static bool _pingSuppressLogged;

    // Called by SoloGraceHud when relief arms (final extraction reached) so the per-arm ping log resets.
    internal static void OnReliefArmed()
    {
        _pingSuppressLogged = false;
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

    // Suppress the repeating PlayerRoom pings (the "pathfind to YOUR room" broadcasts). Patched by
    // string name because SetInvestigate's accessibility is not guaranteed public. Returning false
    // skips the original.
    [HarmonyPatch(typeof(EnemyDirector), "SetInvestigate")]
    [HarmonyPrefix]
    public static bool SetInvestigatePrefix(EnemyDirector __instance, float radius, bool pathfindOnly)
    {
        try
        {
            if (__instance == null) return true;
            // Only EnemyDirector's deliberate herding pings (StartRoom/PlayerRoom/baseline) pass
            // pathfindOnly=true. Every NOISE reaction — gunshots (ItemGun), dropped/bumped objects
            // (PhysGrabObjectImpactDetector/PhysGrabHinge), the extraction point, valuables — uses the
            // 2-arg call with pathfindOnly=false. Let all of those through so enemies still react to the
            // player's noise; we only want to kill the omniscient "pathfind to your room" broadcast.
            if (!pathfindOnly) return true;
            if (radius > ExtractionPingRangeMax) return true;   // baseline float.MaxValue investigate — leave it
            if (!ReliefActive()) return true;
            if (ExtractionStateRef(__instance) != EnemyDirector.ExtractionsDoneState.PlayerRoom)
                return true;                                   // StartRoom lure-to-truck — leave it

            // PlayerRoom ping — suppress. Log once per arm so a playtest can confirm it fired.
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
