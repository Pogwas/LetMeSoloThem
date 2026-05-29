using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace LetMeSoloThem.Patches;

internal enum EnemyKind
{
    None,
    Hidden,
    Oogly,
    Spinny,
    HeartHuggerGas,
    Upscream,
    Spewer
}

// Solo Carry Escape — break out of enemy lockdown states in solo R.E.P.O. play.
// Covers EnemyHidden, EnemyOogly, EnemySpinny, EnemyHeartHugger (gas), EnemyUpscream,
// and mid-carry EnemySlowMouth (Spewer). Hybrid mechanic: hard timer ceiling + struggle
// mash, with attack-button presses also dealing damage to the carrier. Solo-only by default.
[HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.TumbleRequest))]
public static class PlayerTumbleRequestPatch
{
    [HarmonyPostfix]
    public static void Postfix(PlayerTumble __instance, bool _isTumbling, bool _playerInput)
    {
        try
        {
            if (!Plugin.SoloCarryEscapeEnabled.Value) return;
            if (!_isTumbling || _playerInput) return; // only fire on "carry-start" tumbles (input-disabled)
            if (__instance == null || __instance.playerAvatar == null) return;
            if (!RepoRefs.AvatarIsLocal(__instance.playerAvatar)) return;
            if (!ShouldTrigger()) return;

            CarryEscapeTracker.OnCarryStart(__instance.playerAvatar);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[CarryEscape] Postfix threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Solo-gating: fire in solo, OR when WorksInMultiplayer is on, OR when all other players are dead.
    // internal so the poll-based gas detector (CarryEscapeTracker.TryDetectGasCarry) can reuse it.
    internal static bool ShouldTrigger()
    {
        // Solo check via Photon room count — authoritative, avoids load-in race where
        // SemiFunc.PlayerGetList() may return just 1 while a 2nd client is still joining.
        bool isSolo = PhotonNetwork.CurrentRoom == null
            || PhotonNetwork.CurrentRoom.PlayerCount <= 1;
        if (isSolo) return true;
        if (Plugin.SoloCarryEscapeWorksInMultiplayer.Value) return true;
        // MP with WorksInMultiplayer=false: only fire if all OTHER players are dead
        var players = SemiFunc.PlayerGetList();
        if (players == null) return true;
        foreach (var p in players)
        {
            if (p == null) continue;
            if (RepoRefs.AvatarIsLocal(p)) continue;
            if (!RepoRefs.AvatarDeadSet(p)) return false;
        }
        return true;
    }
}

// State machine for the local-player's active carry escape. Static — only one local-player
// carry can be active at a time. Driven by the Postfix above (start) and by SoloGraceHud.Update
// (per-frame tick).
internal static class CarryEscapeTracker
{
    private static Enemy _currentEnemy;
    private static EnemyKind _currentKind = EnemyKind.None;
    private static PlayerAvatar _localPlayer;
    private static int _strugglePresses;
    private static float _lastInputTime;
    private static float _lastNoCarrierLogTime = -999f;

    private static bool IsArmed => _currentKind != EnemyKind.None;

    public static void OnCarryStart(PlayerAvatar local)
    {
        try
        {
            var (enemy, kind) = EnemyKindIdentifier.IdentifyCarrier(local);
            if (kind == EnemyKind.None || enemy == null)
            {
                // TumbleRequest can fire many times per frame during a single fall/tumble.
                // Debounce this diagnostic so a lone tumble doesn't spam ~40 identical lines.
                if (Time.time - _lastNoCarrierLogTime >= 1f)
                {
                    Plugin.Log.LogDebug("[CarryEscape] OnCarryStart fired but no carry enemy found (likely fall or PhysGrabber tumble) — ignoring.");
                    _lastNoCarrierLogTime = Time.time;
                }
                return;
            }

            // Re-arm dedupe: sustained carries (Oogly ceiling-wrestle, Spewer attach) re-fire
            // PlayerTumble.TumbleRequest repeatedly while still holding the player. Without this
            // guard each re-fire reset _timeRemaining back to full, so the timer-escape could
            // never count down to 0 (the Oogly killed the player this way during playtest).
            // If we're already tracking THIS SAME enemy instance, keep the original countdown.
            if (IsArmed && ReferenceEquals(enemy, _currentEnemy)) return;

            Arm(enemy, kind, local);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[CarryEscape] OnCarryStart threw: {ex.GetType().Name}: {ex.Message}");
            ClearState();
        }
    }

    // Poll-based HeartHugger gas detection, driven per-frame from SoloGraceHud.Update.
    // The gas's EnemyHeartHuggerGasChecker calls PlayerTumble.TumbleRequest ONE statement
    // before adding the player to playersColliding, so the synchronous OnCarryStart hook always
    // sees an empty list and misses the gas (returns None → "ignoring"). Polling each frame
    // catches it once playersColliding is populated, decoupling gas detection from that race.
    public static void TryDetectGasCarry()
    {
        if (IsArmed) return;
        if (!Plugin.SoloCarryEscapeEnabled.Value) return;

        try
        {
            // Cheap gate: only scan for gas while the local player is actually tumbling.
            // A gas-carry always coincides with isTumbling (the gas applies the tumble), so this
            // skips the per-frame FindObjectsOfType scan on the ~99% of frames with no carry, and
            // avoids arming off a lingering gas cloud's playersColliding list when the player
            // isn't actually being carried (which TryOnTick would just clear the next frame).
            var pc = PlayerController.instance;
            var local = pc != null ? pc.playerAvatarScript : null;
            if (local == null || !RepoRefs.AvatarIsLocal(local)) return;
            var tumble = RepoRefs.AvatarTumble(local);
            if (tumble == null || !RepoRefs.TumbleIsTumbling(tumble)) return;
            if (!PlayerTumbleRequestPatch.ShouldTrigger()) return;

            foreach (var gc in UnityEngine.Object.FindObjectsOfType<EnemyHeartHuggerGasChecker>())
            {
                if (gc == null) continue;
                var list = RepoRefs.GasCheckerPlayersColliding(gc);
                var hh = RepoRefs.GasCheckerHeartHugger(gc);
                if (list == null || hh == null || !list.Contains(local)) continue;
                var e = hh.GetComponentInParent<Enemy>();
                if (e == null) continue;
                Arm(e, EnemyKind.HeartHuggerGas, local);
                return;
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[CarryEscape] TryDetectGasCarry threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Shared arming logic for both the event-driven OnCarryStart and the polled gas detector.
    private static void Arm(Enemy enemy, EnemyKind kind, PlayerAvatar local)
    {
        _currentEnemy = enemy;
        _currentKind = kind;
        _localPlayer = local;
        _strugglePresses = 0;
        _lastInputTime = 0f;

        Plugin.Log.LogDebug($"[CarryEscape] armed: enemy={kind}, threshold={Plugin.SoloCarryEscapeStrugglePresses.Value} presses (struggle to escape)");
    }

    public static void TryOnTick()
    {
        if (!IsArmed) return;
        if (!Plugin.SoloCarryEscapeEnabled.Value) return;

        try
        {
            // 1. Exit check: vanilla released the tumble early (player died, distance broke, enemy died).
            var tumble = _localPlayer != null ? RepoRefs.AvatarTumble(_localPlayer) : null;
            if (_localPlayer == null || tumble == null || !RepoRefs.TumbleIsTumbling(tumble))
            {
                Plugin.Log.LogDebug("[CarryEscape] tumble ended naturally — clearing state.");
                ClearState();
                return;
            }

            // 2. Input poll.
            var input = InputProbe.Sample(ref _lastInputTime);
            if (input.Struggled)
            {
                _strugglePresses++;
                Plugin.Log.LogDebug($"[CarryEscape] struggle press #{_strugglePresses}/{Plugin.SoloCarryEscapeStrugglePresses.Value} (wasAttack={input.WasAttack})");
            }

            // 3. Damage on attack press.
            if (input.WasAttack && Plugin.SoloCarryEscapeAttackPressDamage.Value > 0)
            {
                ApplyAttackDamage();
            }

            // 4. Resolution — struggle-only. The carry only releases once the player has
            // actively mashed up to the press threshold (no passive timer escape).
            if (_strugglePresses >= Plugin.SoloCarryEscapeStrugglePresses.Value) ResolveEscape();
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[CarryEscape] TryOnTick threw: {ex.GetType().Name}: {ex.Message}");
            ClearState();
        }
    }

    private static void ApplyAttackDamage()
    {
        if (_currentEnemy == null) return;
        try
        {
            var health = RepoRefs.EnemyHealthRef(_currentEnemy);
            if (health == null) return;
            health.Hurt(Plugin.SoloCarryEscapeAttackPressDamage.Value, Vector3.zero);
            Plugin.Log.LogDebug($"[CarryEscape] applied {Plugin.SoloCarryEscapeAttackPressDamage.Value} damage to {_currentKind}");
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[CarryEscape] ApplyAttackDamage threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ResolveEscape()
    {
        var kindForLog = _currentKind;
        var pressesForLog = _strugglePresses;
        try
        {
            ForceLeaveHelpers.ForceLeave(_currentEnemy, _currentKind);

            // Cancel the player's tumble override so they regain control immediately.
            if (_localPlayer != null)
            {
                var t = RepoRefs.AvatarTumble(_localPlayer);
                if (t != null) t.TumbleOverrideTime(0f);
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[CarryEscape] ResolveEscape force-leave threw: {ex.GetType().Name}: {ex.Message}");
        }

        ClearState();
        Plugin.Log.LogDebug($"[CarryEscape] escaped: reason=StruggleThreshold, enemy={kindForLog}, presses={pressesForLog}");
    }

    private static void ClearState()
    {
        _currentEnemy = null;
        _currentKind = EnemyKind.None;
        _localPlayer = null;
        _strugglePresses = 0;
        _lastInputTime = 0f;
    }
}

// Walks the scene to find which carry-capable enemy is currently targeting the local player.
// Returns first match using the priority order: Hidden -> Oogly -> Spinny -> HeartHugger gas
// -> Upscream -> Spewer. If nothing matches, returns EnemyKind.None (means the tumble fired
// from a non-carry source like a fall or PhysGrabber interaction — no-op).
internal static class EnemyKindIdentifier
{
    public static (Enemy enemy, EnemyKind kind) IdentifyCarrier(PlayerAvatar local)
    {
        if (local == null) return (null, EnemyKind.None);

        // Helper — resolves the Enemy parent regardless of access modifier on each enemy's
        // private/internal back-reference field. Walks up the GameObject hierarchy.
        static Enemy GetEnemyParent(Component c) => c == null ? null : c.GetComponentInParent<Enemy>();

        // 1. Hidden
        foreach (var h in UnityEngine.Object.FindObjectsOfType<EnemyHidden>())
        {
            if (h == null) continue;
            try
            {
                if (RepoRefs.HiddenPlayerTarget(h) == local)
                {
                    var e = GetEnemyParent(h);
                    if (e != null) return (e, EnemyKind.Hidden);
                }
            }
            catch { /* benign — field missing means we skip this candidate */ }
        }

        // 2. Oogly
        foreach (var o in UnityEngine.Object.FindObjectsOfType<EnemyOogly>())
        {
            if (o == null) continue;
            try
            {
                if (RepoRefs.OoglyGrabbedPlayer(o) == local)
                {
                    var e = GetEnemyParent(o);
                    if (e != null) return (e, EnemyKind.Oogly);
                }
            }
            catch { }
        }

        // 3. Spinny
        foreach (var s in UnityEngine.Object.FindObjectsOfType<EnemySpinny>())
        {
            if (s == null) continue;
            try
            {
                if (RepoRefs.SpinnyPlayerTarget(s) == local)
                {
                    var e = GetEnemyParent(s);
                    if (e != null) return (e, EnemyKind.Spinny);
                }
            }
            catch { }
        }

        // 4. HeartHugger gas — both fields are non-public, access via FieldRef.
        foreach (var gc in UnityEngine.Object.FindObjectsOfType<EnemyHeartHuggerGasChecker>())
        {
            if (gc == null) continue;
            try
            {
                var list = RepoRefs.GasCheckerPlayersColliding(gc);
                var hh = RepoRefs.GasCheckerHeartHugger(gc);
                if (list != null && list.Contains(local) && hh != null)
                {
                    var e = GetEnemyParent(hh);
                    if (e != null) return (e, EnemyKind.HeartHuggerGas);
                }
            }
            catch { }
        }

        // 5. Upscream — animation-driven brief tumble (1.5s). Skip identification — by the
        // time we'd resolve+escape, the tumble has typically already auto-ended. EnemyKind.None
        // is returned for Upscream-driven tumbles, the OnCarryStart "no match" path logs and
        // no-ops. (Refine if playtest shows Upscream-locks are actually a problem.)

        // 6. Spewer (mid-carry). playerTarget is private — use FieldRef.
        foreach (var sm in UnityEngine.Object.FindObjectsOfType<EnemySlowMouth>())
        {
            if (sm == null) continue;
            try
            {
                if (RepoRefs.SlowMouthPlayerTarget(sm) == local)
                {
                    var e = GetEnemyParent(sm);
                    if (e != null) return (e, EnemyKind.Spewer);
                }
            }
            catch { }
        }

        return (null, EnemyKind.None);
    }
}

// Per-kind force-leave helpers. Each one writes the carrier's currentState to its Leave
// equivalent, nulls the per-enemy player target so the AI doesn't immediately re-acquire,
// then applies the configured Freeze + DisableChase deaggro window. Mirrors the
// ForceReleaseSpewer pattern (in SelfRevivePatch.cs) but generalized to all 6 carry enemies.
internal static class ForceLeaveHelpers
{
    public static void ForceLeave(Enemy enemy, EnemyKind kind)
    {
        if (enemy == null) return;
        try
        {
            switch (kind)
            {
                case EnemyKind.Hidden: ForceHiddenLeave(enemy); break;
                case EnemyKind.Oogly: ForceOoglyLeave(enemy); break;
                case EnemyKind.Spinny: ForceSpinnyLeave(enemy); break;
                case EnemyKind.HeartHuggerGas: ForceHeartHuggerGasLeave(enemy); break;
                case EnemyKind.Upscream: ForceUpscreamLeave(enemy); break;
                case EnemyKind.Spewer: ForceSpewerLeave(enemy); break;
                // EnemyKind.None: no per-kind state to flip; deaggro pass still runs below.
            }

            // Common deaggro pass.
            var freeze = Plugin.SoloCarryEscapeDeaggroFreezeSeconds.Value;
            var chaseDisable = Plugin.SoloCarryEscapeDeaggroChaseDisableSeconds.Value;
            if (freeze > 0f) enemy.Freeze(freeze);
            if (chaseDisable > 0f) enemy.DisableChase(chaseDisable);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[CarryEscape] ForceLeave({kind}) threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ForceHiddenLeave(Enemy enemy)
    {
        var h = enemy.GetComponentInChildren<EnemyHidden>();
        if (h == null) { Plugin.Log.LogDebug("[CarryEscape] Hidden component not found on enemy"); return; }
        h.currentState = EnemyHidden.State.Leave;
        RepoRefs.HiddenPlayerTarget(h) = null;
    }

    private static void ForceOoglyLeave(Enemy enemy)
    {
        var o = enemy.GetComponentInChildren<EnemyOogly>();
        if (o == null) { Plugin.Log.LogDebug("[CarryEscape] Oogly component not found on enemy"); return; }
        o.currentState = EnemyOogly.State.Leave;
        RepoRefs.OoglyGrabbedPlayer(o) = null;
    }

    private static void ForceSpinnyLeave(Enemy enemy)
    {
        var s = enemy.GetComponentInChildren<EnemySpinny>();
        if (s == null) { Plugin.Log.LogDebug("[CarryEscape] Spinny component not found on enemy"); return; }
        s.currentState = EnemySpinny.State.Leave;
        RepoRefs.SpinnyPlayerTarget(s) = null;
    }

    private static void ForceHeartHuggerGasLeave(Enemy enemy)
    {
        var hh = enemy.GetComponentInChildren<EnemyHeartHugger>();
        if (hh == null) { Plugin.Log.LogDebug("[CarryEscape] HeartHugger component not found on enemy"); return; }
        // Degrow cancels the active gas + Aggro state cleanly per the decompile.
        hh.currentState = EnemyHeartHugger.State.Degrow;
    }

    private static void ForceUpscreamLeave(Enemy _enemy)
    {
        // Best-effort: Upscream's tumble is animation-driven and brief (1.5s).
        // Force-leaving usually arrives after the tumble already expired. Log + no-op.
        Plugin.Log.LogDebug("[CarryEscape] ForceUpscreamLeave called — no force-state available (animation-driven tumble); deaggro pass will still apply.");
    }

    private static void ForceSpewerLeave(Enemy enemy)
    {
        var sm = enemy.GetComponentInChildren<EnemySlowMouth>();
        if (sm == null) { Plugin.Log.LogDebug("[CarryEscape] SlowMouth component not found on enemy"); return; }
        sm.currentState = EnemySlowMouth.State.Leave;
        RepoRefs.SlowMouthPlayerTarget(sm) = null;
    }
}

// Per-frame "did the player struggle this frame?" detector. Returns (struggled, wasAttack)
// where wasAttack is true if the struggle was specifically a mouse-click (used for the
// damage-on-attack mechanic). Debounced via _lastInputTime to prevent auto-fire keyboards
// from instant-escaping in one frame.
internal struct InputProbeResult
{
    public bool Struggled;
    public bool WasAttack;
}

internal static class InputProbe
{
    public static InputProbeResult Sample(ref float lastInputTime)
    {
        var result = new InputProbeResult();
        var debounce = Plugin.SoloCarryEscapeStruggleInputDebounceSeconds.Value;
        if (Time.time - lastInputTime < debounce) return result; // debounced — skip

        // Attack (mouse click) - separate flag so the caller can dispatch damage.
        if (Input.GetMouseButtonDown(0))
        {
            result.Struggled = true;
            result.WasAttack = true;
            lastInputTime = Time.time;
            return result;
        }

        // Movement keys + jump key — generic struggle, no damage.
        if (Input.GetKeyDown(KeyCode.Space) ||
            Input.GetKeyDown(KeyCode.W) ||
            Input.GetKeyDown(KeyCode.A) ||
            Input.GetKeyDown(KeyCode.S) ||
            Input.GetKeyDown(KeyCode.D))
        {
            result.Struggled = true;
            lastInputTime = Time.time;
        }

        return result;
    }
}
