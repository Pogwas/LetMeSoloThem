using System;
using HarmonyLib;
using UnityEngine;

namespace LetMeSoloThem.Patches;

// Player-count-keyed dampening of enemy DETECTION, so a solo player faces less "laser-focused"
// enemies. A Harmony Postfix on Enemy.Awake scales public tuning fields on each freshly-spawned
// enemy's EnemyVision component by the configured Detection multiplier for the current player
// count. 1.0 = vanilla; lower = gentler; 0 = effectively blind. At 4+ players the multiplier
// defaults to 1.0, so the feature naturally no-ops in full lobbies.
//
// Originally scoped to also dampen "pursuit" via EnemyStateChase, but a 2026-05-20 playtest +
// decompile audit found EnemyStateChase is used by only ONE enemy (the Headman) — REPO enemy AI
// is bespoke per-type with no universal pursuit knob, so the pursuit half was cut. Detection
// dampening still indirectly eases pursuit: a shorter vision range means bespoke enemies lose
// sight of you sooner and give up on their own. See the design spec.
//
// Same per-instance field-modification pattern used by Solo Sword (HurtCollider.playerDamage).
// Auto-registered by Plugin.Awake's _harmony.PatchAll().
[HarmonyPatch(typeof(Enemy), "Awake")]
public static class SoloEnemyDampenerPatch
{
    // A multiplier at or below this is treated as zero (fully off).
    private const float MultEpsilon = 0.001f;

    // VisionsToTrigger* counters are set to this when the multiplier is ~0 — high enough that the
    // in-game accumulator (which ticks every 0.25s) never reaches it.
    private const int InverseZeroSentinel = 9999;

    [HarmonyPostfix]
    public static void Postfix(Enemy __instance)
    {
        try
        {
            if (!Plugin.SoloEnemyEnabled.Value) return;

            // EnemyVision's vision coroutine runs only on the master client / singleplayer host —
            // scaling fields anywhere else has no effect.
            if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

            int playerCount = SemiFunc.PlayerGetList()?.Count ?? 1;
            if (playerCount < 1) playerCount = 1;

            float detectionMult = playerCount switch
            {
                1 => Plugin.SoloEnemyDetectionSolo.Value,
                2 => Plugin.SoloEnemyDetectionDuo.Value,
                3 => Plugin.SoloEnemyDetectionTrio.Value,
                _ => Plugin.SoloEnemyDetectionQuad.Value,
            };

            // At vanilla — nothing to scale (also avoids rounding drift on the int counter
            // fields when dividing by exactly 1.0).
            if (Mathf.Approximately(detectionMult, 1f)) return;

            var vision = __instance.GetComponent<EnemyVision>();
            if (vision == null) return;

            float origDist = vision.VisionDistance;
            int origT = vision.VisionsToTrigger;
            int origTC = vision.VisionsToTriggerCrouch;
            int origTCr = vision.VisionsToTriggerCrawl;

            vision.VisionDistance = origDist * detectionMult;
            vision.VisionsToTrigger = ScaleInverse(origT, detectionMult);
            vision.VisionsToTriggerCrouch = ScaleInverse(origTC, detectionMult);
            vision.VisionsToTriggerCrawl = ScaleInverse(origTCr, detectionMult);

            // __instance.gameObject is typically named "Controller"; the parent GameObject
            // carries the enemy-type name, which is far more useful in the log.
            Transform parent = __instance.transform.parent;
            string enemyName = parent != null ? parent.name : __instance.gameObject.name;

            Plugin.Log.LogDebug(
                $"[SoloEnemy] {enemyName} players={playerCount} det={detectionMult:F2} | " +
                $"VisionDistance {origDist:F1}->{vision.VisionDistance:F1}, " +
                $"VisionsToTrigger {origT}->{vision.VisionsToTrigger} " +
                $"{origTC}->{vision.VisionsToTriggerCrouch} {origTCr}->{vision.VisionsToTriggerCrawl}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[SoloEnemy] Postfix threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Counter-type fields get LARGER (harder to reach) as the multiplier goes DOWN. A multiplier
    // at/below MultEpsilon returns the sentinel — avoids divide-by-zero and makes the threshold
    // effectively unreachable.
    private static int ScaleInverse(int original, float mult)
    {
        if (mult <= MultEpsilon) return InverseZeroSentinel;
        return Mathf.Max(1, Mathf.RoundToInt(original / mult));
    }
}
