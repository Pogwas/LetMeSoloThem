using System;
using HarmonyLib;
using UnityEngine;

namespace LetMeSoloThem.Patches;

// Solo Cart Guard: stops enemies from damaging OR shoving the solo player's valuables while the player
// is away and can't defend them. Two enemy-agnostic seams:
//   1. HurtCollider.PhysObjectHurt prefix — the deliberate-attack seam. Skipping it for an enemy-owned
//      collider hitting a guarded valuable stops the break impulse (no value loss/destroy) AND the
//      knockback force (enemies can't shove your loot around) in one place.
//   2. PhysGrabObjectImpactDetector.Break prefix — a backstop for any other enemy-caused value loss that
//      doesn't route through (1); gated on the game's own enemyInteractionTimer (set by PhysObjectHurt on
//      enemy hits to non-held objects). The player's own drops/throws never set it, so they break normally.
//
// Neither names a specific enemy, so all loot-smashing enemies are covered. The away gate is global
// (proximity to the nearest valuable) with a LingerSeconds window so protection doesn't drop the instant
// you arrive. Host-authoritative (solo = host); no RPC. Auto-registered by Plugin.Awake's PatchAll().
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

    // Set true each time a prefix actually suppresses an enemy hit; the HUD consumes it (clears it)
    // to flash a "Blocked!" pulse so the player sees the guard working in the moment.
    internal static bool SuppressionPulse;

    // ---- Away-distance protection with "linger" -----------------------------------------------------
    // Maintained once per frame by Tick() (called from SoloGraceHud.Update). Two distances are involved:
    //   * present (small, = CartTouchDistance): you're right AT the cart/loot. Power-down only happens here.
    //   * "have you left?" (large, = AwayDistance): you must get this far from the cart/loot at least once
    //     after arming before power-down is allowed — so setting up at the cart shows Active, not Off, and
    //     pushing the cart (which travels with you) never powers down.
    // When you RETURN to the cart after leaving, protection powers down over LingerSeconds, then hands
    // defense back (off). Hysteresis on "present" stops on/off flicker at the boundary.
    private const float NearScanInterval = 0.25f;
    private const float NearHysteresis = 1.5f; // extra meters past the present radius before you count as left it
    private static bool _protectByDistance = true;
    private static float _lingerRemaining;
    private static bool _present;            // right at the cart/loot (small radius, with hysteresis)
    private static bool _hasLeftSinceArm;    // have you been AwayDistance from the cart/loot since arming?
    private static float _nearScanTimer;

    // Per-level latch: the guard stays OFF until the local player has reached their cart at least once this
    // level (you can't be "guarding the cart" before you've been to it). Set by Tick, reset on leaving the
    // level. Gating both the protection (GuardConditions) and the HUD (GuardArmedForHud) on it.
    private static bool _cartTouchedThisLevel;

    // Whether away-distance protection is currently in effect (always true in always-on mode). Read by HUD.
    internal static bool ProtectingByDistance => _protectByDistance;

    // HUD readouts (away-mode only): are you back at the cart in the power-down/off phase, and seconds left.
    internal static bool PresentAtCart => _present && _hasLeftSinceArm;
    internal static float LingerRemaining => _lingerRemaining;

    internal static void Tick(float dt)
    {
        try
        {
            // Reset everything when we're not in a live solo/host level.
            bool inSoloLevel = Plugin.SoloCartGuardEnabled.Value && SemiFunc.RunIsLevel() && IsSoloOrAuthorizedHost();
            if (!inSoloLevel)
            {
                _cartTouchedThisLevel = false;
                _protectByDistance = true;
                _lingerRemaining = Plugin.SoloCartGuardLingerSeconds.Value;
                _present = false;
                _hasLeftSinceArm = false;
                return;
            }

            // Throttle the scene scans (both the cart-touch latch and the away-gate near-loot check).
            _nearScanTimer -= dt;
            bool doScan = _nearScanTimer <= 0f;
            if (doScan) _nearScanTimer = NearScanInterval;

            // Arm once you've reached the cart this level; latched until you leave the level.
            if (!_cartTouchedThisLevel && doScan && LocalPlayerTouchingCart())
                _cartTouchedThisLevel = true;

            // Away-distance protection (with linger). In always-on mode it's always protecting.
            if (!Plugin.SoloCartGuardOnlyWhenAway.Value)
            {
                _protectByDistance = true;
                _lingerRemaining = Plugin.SoloCartGuardLingerSeconds.Value;
                _present = false;
                return;
            }

            if (doScan)
            {
                float dsq = NearestPresentDistanceSq();
                if (dsq != float.MaxValue)
                {
                    // "Present" = right at the cart/loot (small radius), with hysteresis so it doesn't
                    // flicker. Power-down only happens here.
                    float pr = Plugin.SoloCartGuardCartTouchDistance.Value;
                    float prNearSq = pr * pr;
                    float prFar = pr + NearHysteresis;
                    float prFarSq = prFar * prFar;
                    if (_present) { if (dsq > prFarSq) _present = false; }
                    else if (dsq <= prNearSq) _present = true;

                    // You count as having "left" once you get AwayDistance from the cart/loot — but ONLY
                    // after arming (you spawn far from the cart, so without this guard the walk over to it
                    // would pre-set "left" and it'd power down the instant you first touch the cart). Until
                    // you've left post-arm, being at the cart shows Active; pushing it never trips power-down.
                    float away = Plugin.SoloCartGuardAwayDistance.Value;
                    if (_cartTouchedThisLevel && dsq > away * away) _hasLeftSinceArm = true;
                }
            }

            if (_present && _hasLeftSinceArm)
            {
                // Returned to the cart: power down over the linger window, then hand defense back (off).
                if (_lingerRemaining > 0f) { _lingerRemaining -= dt; _protectByDistance = true; }
                else _protectByDistance = false;
            }
            else
            {
                // Away, or at the cart but haven't left yet → keep protecting; reset linger for next return.
                _protectByDistance = true;
                _lingerRemaining = Plugin.SoloCartGuardLingerSeconds.Value;
            }
        }
        catch
        {
            _protectByDistance = true; // fail safe: keep loot protected
        }
    }

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

    // Squared distance from the local player to the nearest "present" anchor — any valuable OR any cart.
    // Including carts keeps the state stable while you PUSH the cart (you're always right next to it).
    // Returns float.MaxValue if nothing is found / on uncertainty, so the caller can ignore that sample
    // (avoids a transient empty scan flipping the state). Scans the scene, so the caller MUST throttle it.
    private static float NearestPresentDistanceSq()
    {
        try
        {
            var pc = PlayerController.instance;
            if (pc == null || pc.playerAvatarScript == null) return float.MaxValue;
            Vector3 p = pc.playerAvatarScript.transform.position;
            float best = float.MaxValue;

            var valuables = UnityEngine.Object.FindObjectsOfType<ValuableObject>();
            for (int i = 0; i < valuables.Length; i++)
            {
                var v = valuables[i];
                if (v == null) continue;
                float d = (v.transform.position - p).sqrMagnitude;
                if (d < best) best = d;
            }

            var carts = UnityEngine.Object.FindObjectsOfType<PhysGrabCart>();
            for (int i = 0; i < carts.Length; i++)
            {
                var c = carts[i];
                if (c == null) continue;
                float d = (c.transform.position - p).sqrMagnitude;
                if (d < best) best = d;
            }

            return best;
        }
        catch
        {
            return float.MaxValue;
        }
    }

    // Latch helper: is the local player within CartTouchDistance of any cart? Throttle the call (it scans
    // the scene). Returns false on uncertainty.
    private static bool LocalPlayerTouchingCart()
    {
        try
        {
            var pc = PlayerController.instance;
            if (pc == null || pc.playerAvatarScript == null) return false;
            Vector3 p = pc.playerAvatarScript.transform.position;
            float touch = Plugin.SoloCartGuardCartTouchDistance.Value;
            float touchSq = touch * touch;
            var carts = UnityEngine.Object.FindObjectsOfType<PhysGrabCart>();
            for (int i = 0; i < carts.Length; i++)
            {
                var c = carts[i];
                if (c == null) continue;
                if ((c.transform.position - p).sqrMagnitude <= touchSq) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    // Shared gate (everything except the per-seam "enemy-caused" discriminator). Returns false (vanilla)
    // on any uncertainty. Distance is handled globally via _protectByDistance (maintained by Tick).
    private static bool GuardConditions(PhysGrabObjectImpactDetector detector)
    {
        if (!Plugin.SoloCartGuardEnabled.Value) return false;
        if (!_cartTouchedThisLevel) return false;                                       // not armed yet
        if (detector == null) return false;
        if (ValuableObjectRef(detector) == null) return false;                          // valuables only
        if (!IsSoloOrAuthorizedHost()) return false;                                    // solo / host
        if (Plugin.SoloCartGuardCartOnly.Value && CurrentCartRef(detector) == null)     // cart-only scope
            return false;
        if (Plugin.SoloCartGuardOnlyWhenAway.Value && !_protectByDistance)              // away (+ linger)
            return false;
        return true;
    }

    // Lightweight "is the guard armed for this run" check for the HUD label (no per-hit fields).
    internal static bool GuardArmedForHud()
    {
        try
        {
            if (!Plugin.SoloCartGuardEnabled.Value) return false;
            if (!SemiFunc.RunIsLevel()) return false;
            if (!IsSoloOrAuthorizedHost()) return false;
            return _cartTouchedThisLevel; // armed only after you've reached the cart this level
        }
        catch
        {
            return false;
        }
    }

    // Seam 1: stop enemies from BOTH damaging and shoving guarded loot. Skip HurtCollider.PhysObjectHurt
    // (which applies the break impulse AND the knockback force/torque) for an enemy-owned collider hitting
    // a guarded valuable. enemyHost is non-null only for enemy attack colliders — player weapons (sword,
    // etc.) have it null, so player hits pass through untouched.
    [HarmonyPatch(typeof(HurtCollider), "PhysObjectHurt")]
    [HarmonyPrefix]
    public static bool PhysObjectHurtPrefix(HurtCollider __instance, PhysGrabObject physGrabObject)
    {
        try
        {
            if (__instance == null || __instance.enemyHost == null) return true; // not an enemy attack
            if (physGrabObject == null) return true;
            var detector = physGrabObject.GetComponent<PhysGrabObjectImpactDetector>();
            if (detector == null) return true;
            if (!GuardConditions(detector)) return true;

            SuppressionPulse = true; // HUD "Blocked!" pulse
            if (!_suppressLogged)
            {
                _suppressLogged = true;
                Plugin.Log.LogDebug("[SoloCartGuard] suppressing enemy attack on valuable (no damage, no knockback) — first this arm");
            }
            return false; // skip: no break impulse, no force/torque, no destroy-launch, no enemyInteractionTimer
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[SoloCartGuard] PhysObjectHurt prefix threw: {ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    // Seam 2 (backstop): skip any other enemy-caused value loss that doesn't route through PhysObjectHurt.
    // Gated on the game's enemyInteractionTimer so player-caused breaks (drops/throws) still apply.
    [HarmonyPatch(typeof(PhysGrabObjectImpactDetector), "Break")]
    [HarmonyPrefix]
    public static bool BreakPrefix(PhysGrabObjectImpactDetector __instance)
    {
        try
        {
            if (!GuardConditions(__instance)) return true;
            if (EnemyInteractionTimerRef(__instance) <= 0f) return true; // enemy-caused breaks only

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
