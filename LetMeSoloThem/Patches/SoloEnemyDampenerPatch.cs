using System;
using HarmonyLib;
using UnityEngine;

namespace LetMeSoloThem.Patches;

// Player-count-keyed dampening of enemy detection + pursuit, so a solo player faces less
// "laser-focused" enemies. A Harmony Postfix on Enemy.Awake scales public tuning fields on
// each freshly-spawned enemy's EnemyVision (detection) and EnemyStateChase (pursuit) components
// by the configured Detection / Pursuit multipliers for the current player count. 1.0 = vanilla;
// lower = gentler; 0 = effectively off. At 4+ players the multipliers default to 1.0, so the
// feature naturally no-ops in full lobbies. Same per-instance field-modification pattern used by
// Solo Sword (HurtCollider.playerDamage) and Solo Tranq (ItemGun.shootCooldown). Auto-registered
// by Plugin.Awake's _harmony.PatchAll().
[HarmonyPatch(typeof(Enemy), "Awake")]
public static class SoloEnemyDampenerPatch
{
    // A multiplier at or below this is treated as zero (fully off).
    private const float MultEpsilon = 0.001f;

    // Counter-type fields (VisionsToTrigger*, VisionsToReset) are set to this when the multiplier
    // is ~0 — high enough that the in-game accumulator (which ticks every 0.25s) never reaches it.
    private const int InverseZeroSentinel = 9999;

    [HarmonyPostfix]
    public static void Postfix(Enemy __instance)
    {
        try
        {
            if (!Plugin.SoloEnemyEnabled.Value) return;

            // Enemy AI (EnemyVision's vision coroutine, EnemyStateChase.Update) runs only on the
            // master client / singleplayer host — scaling fields anywhere else has no effect.
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
            float pursuitMult = playerCount switch
            {
                1 => Plugin.SoloEnemyPursuitSolo.Value,
                2 => Plugin.SoloEnemyPursuitDuo.Value,
                3 => Plugin.SoloEnemyPursuitTrio.Value,
                _ => Plugin.SoloEnemyPursuitQuad.Value,
            };

            // Both dials at vanilla — nothing to scale (also avoids rounding drift on the
            // int counter fields when dividing by exactly 1.0).
            if (detectionMult >= 1f && pursuitMult >= 1f) return;

            string detLog = "detection vanilla";
            string purLog = "pursuit vanilla";

            // --- Detection: EnemyVision ---
            if (detectionMult < 1f)
            {
                var vision = __instance.GetComponent<EnemyVision>();
                if (vision != null)
                {
                    float origDist = vision.VisionDistance;
                    int origT = vision.VisionsToTrigger;
                    int origTC = vision.VisionsToTriggerCrouch;
                    int origTCr = vision.VisionsToTriggerCrawl;

                    vision.VisionDistance = origDist * detectionMult;
                    vision.VisionsToTrigger = ScaleInverse(origT, detectionMult);
                    vision.VisionsToTriggerCrouch = ScaleInverse(origTC, detectionMult);
                    vision.VisionsToTriggerCrawl = ScaleInverse(origTCr, detectionMult);

                    detLog = $"VisionDistance {origDist:F1}->{vision.VisionDistance:F1}, " +
                             $"VisionsToTrigger {origT}->{vision.VisionsToTrigger} " +
                             $"{origTC}->{vision.VisionsToTriggerCrouch} " +
                             $"{origTCr}->{vision.VisionsToTriggerCrawl}";
                }
                else
                {
                    detLog = "detection skipped (no EnemyVision)";
                }
            }

            // --- Pursuit: EnemyStateChase ---
            if (pursuitMult < 1f)
            {
                var chase = __instance.GetComponent<EnemyStateChase>();
                if (chase != null)
                {
                    float origMin = chase.StateTimeMin;
                    float origMax = chase.StateTimeMax;
                    float origVT = chase.VisionTime;
                    int origVR = chase.VisionsToReset;

                    chase.StateTimeMin = origMin * pursuitMult;
                    chase.StateTimeMax = origMax * pursuitMult;
                    chase.VisionTime = origVT * pursuitMult;
                    chase.VisionsToReset = ScaleInverse(origVR, pursuitMult);

                    purLog = $"StateTime {origMin:F1}/{origMax:F1}->" +
                             $"{chase.StateTimeMin:F1}/{chase.StateTimeMax:F1}, " +
                             $"VisionTime {origVT:F1}->{chase.VisionTime:F1}, " +
                             $"VisionsToReset {origVR}->{chase.VisionsToReset}";
                }
                else
                {
                    purLog = "pursuit skipped (no EnemyStateChase)";
                }
            }

            Plugin.Log.LogDebug(
                $"[SoloEnemy] {__instance.gameObject.name} players={playerCount} " +
                $"det={detectionMult:F2} pur={pursuitMult:F2} | {detLog} | {purLog}");
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
