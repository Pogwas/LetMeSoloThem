using System;
using HarmonyLib;
using UnityEngine;

namespace LetMeSoloThem.Patches;

// Solo Cart Guard: stops enemies from destroying / chipping the value of the solo player's valuables
// while the player is away and can't defend them. Intercepts at the valuable's universal value-loss
// chokepoint (PhysGrabObjectImpactDetector.Break) and skips it only for ENEMY-caused breaks.
//
// Enemy causation is the game's own signal: HurtCollider.PhysObjectHurt sets impactDetector
// .enemyInteractionTimer = 2f whenever an enemy hits a (non-held) phys object. This is enemy-agnostic
// (every enemy's attack routes through HurtCollider), so we cover all loot-smashing enemies without
// naming any of them. The player's own drops/throws never set this flag, so they break normally.
//
// Enemy force is still applied (loot gets shoved); only the value loss / break is suppressed.
// Host-authoritative (solo = host); no RPC. Auto-registered by Plugin.Awake's PatchAll().
//
// NOTE: the bare [HarmonyPatch] on the class is REQUIRED. PatchAll() only discovers classes annotated
// with [HarmonyPatch]; without it the per-method attribute below is silently skipped (Quirk 7).
[HarmonyPatch]
public static class SoloCartGuardPatch
{
    private static readonly AccessTools.FieldRef<PhysGrabObjectImpactDetector, float> EnemyInteractionTimerRef =
        AccessTools.FieldRefAccess<PhysGrabObjectImpactDetector, float>("enemyInteractionTimer");

    private static readonly AccessTools.FieldRef<PhysGrabObjectImpactDetector, ValuableObject> ValuableObjectRef =
        AccessTools.FieldRefAccess<PhysGrabObjectImpactDetector, ValuableObject>("valuableObject");

    private static readonly AccessTools.FieldRef<PhysGrabObjectImpactDetector, PhysGrabCart> CurrentCartRef =
        AccessTools.FieldRefAccess<PhysGrabObjectImpactDetector, PhysGrabCart>("currentCart");

    // Verification aid: log the first suppression per arm so a playtest can confirm the patch fires.
    // Reset each new level by OnGuardArmed (called from SoloGraceHud's disarmed->armed transition).
    private static bool _suppressLogged;

    // Set true each time BreakPrefix actually suppresses an enemy break; the HUD consumes it (clears it)
    // to flash a "Blocked!" pulse so the player sees the guard working in the moment.
    internal static bool SuppressionPulse;

    // True in true-solo, or (when WorksInMultiplayer) when we're the authoritative host.
    // A null player list = offline singleplayer session, treated as solo.
    private static bool IsSoloOrAuthorizedHost()
    {
        if (Plugin.SoloCartGuardWorksInMultiplayer.Value)
            return SemiFunc.IsMasterClientOrSingleplayer();
        return (SemiFunc.PlayerGetList()?.Count ?? 1) <= 1;
    }

    // Reset the once-per-arm suppression log so a multi-run playtest sees it each new level.
    // Mirrors SoloExtractionReliefPatch.OnReliefArmed.
    internal static void OnGuardArmed()
    {
        _suppressLogged = false;
    }

    // HUD helper: is the local player currently within AwayDistance of ANY valuable? Used only to show
    // "Standby" on the HUD while you're tending your loot (protection paused). Approximate — keys off the
    // nearest valuable, not per-protected-item. Scans the scene, so the caller MUST throttle it. Returns
    // false on uncertainty.
    internal static bool LocalPlayerNearLoot()
    {
        try
        {
            var pc = PlayerController.instance;
            if (pc == null || pc.playerAvatarScript == null) return false;
            Vector3 p = pc.playerAvatarScript.transform.position;
            float away = Plugin.SoloCartGuardAwayDistance.Value;
            float awaySq = away * away;
            var valuables = UnityEngine.Object.FindObjectsOfType<ValuableObject>();
            for (int i = 0; i < valuables.Length; i++)
            {
                var v = valuables[i];
                if (v == null) continue;
                if ((v.transform.position - p).sqrMagnitude <= awaySq) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // True when the guard should suppress THIS break. Returns false (vanilla) on any uncertainty.
    internal static bool GuardActive(PhysGrabObjectImpactDetector detector)
    {
        try
        {
            if (!Plugin.SoloCartGuardEnabled.Value) return false;
            if (detector == null) return false;

            // Enemy-caused only. Player drops/throws never set this flag.
            if (EnemyInteractionTimerRef(detector) <= 0f) return false;

            // Valuables only (not generic breakable props).
            if (ValuableObjectRef(detector) == null) return false;

            // Solo / host gate.
            if (!IsSoloOrAuthorizedHost()) return false;

            // Cart-only scope.
            if (Plugin.SoloCartGuardCartOnly.Value && CurrentCartRef(detector) == null)
                return false;

            // Away gate: protect only when the local player is NOT near the loot.
            if (Plugin.SoloCartGuardOnlyWhenAway.Value)
            {
                var pc = PlayerController.instance;
                if (pc == null || pc.playerAvatarScript == null) return false; // can't tell -> vanilla
                float dist = Vector3.Distance(
                    pc.playerAvatarScript.transform.position,
                    detector.transform.position);
                if (dist <= Plugin.SoloCartGuardAwayDistance.Value) return false; // present -> defend it
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // Lightweight "is the guard armed for this run" check for the HUD label (no per-hit fields).
    internal static bool GuardArmedForHud()
    {
        try
        {
            if (!Plugin.SoloCartGuardEnabled.Value) return false;
            if (!SemiFunc.RunIsLevel()) return false;
            return IsSoloOrAuthorizedHost();
        }
        catch
        {
            return false;
        }
    }

    // Skip the value-loss/destroy when the guard is active. Returning false skips the original Break.
    [HarmonyPatch(typeof(PhysGrabObjectImpactDetector), "Break")]
    [HarmonyPrefix]
    public static bool BreakPrefix(PhysGrabObjectImpactDetector __instance)
    {
        try
        {
            if (!GuardActive(__instance)) return true;

            SuppressionPulse = true; // HUD reads this to flash "Blocked!"
            if (!_suppressLogged)
            {
                _suppressLogged = true;
                Plugin.Log.LogDebug("[SoloCartGuard] suppressing enemy-caused valuable break (first this arm)");
            }
            return false;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[SoloCartGuard] Break prefix threw: {ex.GetType().Name}: {ex.Message}");
            return true; // on any error, let vanilla run
        }
    }
}
