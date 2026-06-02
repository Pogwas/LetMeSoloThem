using LetMeSoloThem.Patches;
using LetMeSoloThem.State;
using UnityEngine;

namespace LetMeSoloThem.Hud;

public class SoloGraceHud : MonoBehaviour
{
    private const float FadeOutSeconds = 3f;

    private const float ChassisRevivingSeconds = 2f;
    private const float ChassisUsedFadeSeconds = 5f;

    private GUIStyle _style;
    private float _currentTimer;
    private float _previousTimer;
    private float _fadeOutRemaining;
    private bool _shouldShowGrace;
    private float _diagLogTimer;
    private bool _chassisWasDrawing;
    private int _previousChassisCount;
    private float _chassisRevivingRemaining;
    private float _chassisUsedFadeRemaining;
    private const float ReliefFadeSeconds = 3f;
    private bool _reliefActive;
    private float _reliefFadeRemaining;
    private bool _cartGuardArmed;
    private const float CartGuardFlashSeconds = 1.5f;
    private float _cartGuardFlashRemaining;

    private void OnDestroy()
    {
        Plugin.Log?.LogDebug("[HUD] OnDestroy fired (Plugin will recreate on next sceneLoaded)");
    }

    private void Update()
    {
        _diagLogTimer -= Time.deltaTime;
        bool shouldLog = _diagLogTimer <= 0f;
        if (shouldLog) _diagLogTimer = 5f;

        try
        {
            UpdateBody(shouldLog);
            FreeChassisGranter.TryGrantOnTick();
            ZeroHpReviveTrigger.TryOnTick();
            SoloSwordGranter.TryGrantOnTick();
            SoloTranqGranter.TryGrantOnTick();
            SoloStrengthGranter.TryGrantOnTick();
            CarryEscapeTracker.TryDetectGasCarry();
            CarryEscapeTracker.TryOnTick();
            TrackChassisTransition();
            TrackReliefTransition();
            SoloCartGuardPatch.Tick(Time.deltaTime);
            TrackCartGuardTransition();
            if (shouldLog) ChassisDiagnostic();
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[HUD] Update threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void TrackChassisTransition()
    {
        bool revivingExpiredThisFrame = false;

        if (_chassisRevivingRemaining > 0f)
        {
            _chassisRevivingRemaining -= Time.deltaTime;
            if (_chassisRevivingRemaining <= 0f)
            {
                _chassisRevivingRemaining = 0f;
                revivingExpiredThisFrame = true;
            }
        }
        if (_chassisUsedFadeRemaining > 0f)
        {
            _chassisUsedFadeRemaining -= Time.deltaTime;
            if (_chassisUsedFadeRemaining < 0f) _chassisUsedFadeRemaining = 0f;
        }

        if (!SemiFunc.RunIsLevel())
        {
            _previousChassisCount = 0;
            return;
        }
        var pc = PlayerController.instance;
        if (pc == null || pc.playerAvatarScript == null) return;

        string steamID = SemiFunc.PlayerGetSteamID(pc.playerAvatarScript);
        int currentCount = SpareChassisInventory.Count(steamID);

        if (currentCount < _previousChassisCount && _chassisRevivingRemaining <= 0f)
        {
            _chassisRevivingRemaining = ChassisRevivingSeconds;
            _chassisUsedFadeRemaining = 0f;
            Plugin.Log.LogDebug($"[Chassis HUD] chassis used (count {_previousChassisCount}→{currentCount}) — showing 'Reviving' for 2s");
        }

        if (revivingExpiredThisFrame && currentCount == 0)
        {
            _chassisUsedFadeRemaining = ChassisUsedFadeSeconds;
            Plugin.Log.LogDebug("[Chassis HUD] 'Reviving' done, no chassis left — starting 'Used' fade");
        }

        _previousChassisCount = currentCount;
    }

    private void TrackReliefTransition()
    {
        bool active = SoloExtractionReliefPatch.ReliefActive();
        if (active && !_reliefActive)
        {
            // Relief just armed (final extraction reached, solo-gated). Reset the per-arm ping log and
            // emit a one-time marker so a playtest can confirm relief engaged.
            SoloExtractionReliefPatch.OnReliefArmed();
            Plugin.Log.LogDebug("[SoloExtraction] ARMED — final-extraction relief active (suppressing PlayerRoom pings)");
        }
        else if (!active && _reliefActive)
        {
            // Relief just turned off (e.g. level ended) — start the fade-out.
            _reliefFadeRemaining = ReliefFadeSeconds;
            Plugin.Log.LogDebug("[SoloExtraction] DISARMED — relief ended (left level / extraction state reset)");
        }
        _reliefActive = active;

        if (_reliefFadeRemaining > 0f)
        {
            _reliefFadeRemaining -= Time.deltaTime;
            if (_reliefFadeRemaining < 0f) _reliefFadeRemaining = 0f;
        }
    }

    private void TrackCartGuardTransition()
    {
        bool armed = SoloCartGuardPatch.GuardArmedForHud();
        if (armed && !_cartGuardArmed)
        {
            // Cart guard just armed for this level — reset the per-arm suppression log.
            SoloCartGuardPatch.OnGuardArmed();
            Plugin.Log.LogDebug("[SoloCartGuard] ARMED — reached cart, guard active this level");
        }
        else if (!armed && _cartGuardArmed)
        {
            Plugin.Log.LogDebug("[SoloCartGuard] DISARMED — cart guard inactive (left level / not solo)");
        }
        _cartGuardArmed = armed;

        // "Blocked!" flash: BreakPrefix sets SuppressionPulse each time it actually stops an enemy break.
        if (SoloCartGuardPatch.SuppressionPulse)
        {
            SoloCartGuardPatch.SuppressionPulse = false;
            _cartGuardFlashRemaining = CartGuardFlashSeconds;
        }
        if (_cartGuardFlashRemaining > 0f)
        {
            _cartGuardFlashRemaining -= Time.deltaTime;
            if (_cartGuardFlashRemaining < 0f) _cartGuardFlashRemaining = 0f;
        }
        // Active / Powering Down / Off are derived live in DrawCartGuardLabel from the patch's distance
        // state (SoloCartGuardPatch.NearLoot / ProtectingByDistance / LingerRemaining) — no tracking here.
    }

    private void ChassisDiagnostic()
    {
        if (!Plugin.ReviveEnabled.Value)
        {
            Plugin.Log.LogDebug("[Chassis HUD] state: ReviveEnabled=false (label won't draw)");
            return;
        }
        if (!SemiFunc.RunIsLevel())
        {
            Plugin.Log.LogDebug("[Chassis HUD] state: not in level");
            return;
        }
        var pc = PlayerController.instance;
        if (pc == null || pc.playerAvatarScript == null)
        {
            Plugin.Log.LogDebug("[Chassis HUD] state: PlayerController/avatar not ready");
            return;
        }
        string steamID = SemiFunc.PlayerGetSteamID(pc.playerAvatarScript);
        int count = SpareChassisInventory.Count(steamID);
        Plugin.Log.LogDebug(
            $"[Chassis HUD] state: steamID='{steamID}', count={count}, hudEnabled={Plugin.HudEnabled.Value}, willDraw={(count > 0) && Plugin.HudEnabled.Value}");
    }

    private void UpdateBody(bool shouldLog)
    {
        if (!SemiFunc.RunIsLevel())
        {
            _shouldShowGrace = _fadeOutRemaining > 0f;
            _previousTimer = _currentTimer = 0f;
            TickFade();
            if (shouldLog)
                Plugin.Log.LogDebug("[HUD] gated: SemiFunc.RunIsLevel()=false");
            return;
        }

        int playerCount = SemiFunc.PlayerGetList()?.Count ?? 1;
        if (playerCount != 1)
        {
            _shouldShowGrace = false;
            _fadeOutRemaining = 0f;
            if (shouldLog)
                Plugin.Log.LogDebug($"[HUD] gated grace: not-solo, playerCount={playerCount}");
            return;
        }

        var director = EnemyDirector.instance;
        if (director == null)
        {
            _shouldShowGrace = false;
            _fadeOutRemaining = 0f;
            if (shouldLog)
                Plugin.Log.LogDebug("[HUD] gated grace: EnemyDirector.instance=null");
            return;
        }

        _previousTimer = _currentTimer;
        _currentTimer = EnemyDirectorStartPatch.SpawnIdlePauseTimerRef(director);

        if (_previousTimer > 0f && _currentTimer <= 0f)
        {
            _fadeOutRemaining = FadeOutSeconds;
        }

        TickFade();
        _shouldShowGrace = _currentTimer > 0f || _fadeOutRemaining > 0f;

        if (shouldLog)
            Plugin.Log.LogDebug($"[HUD] in-level: playerCount={playerCount}, timer={_currentTimer:F1}, showGrace={_shouldShowGrace}");
    }

    private void TickFade()
    {
        if (_fadeOutRemaining > 0f)
        {
            _fadeOutRemaining -= Time.deltaTime;
            if (_fadeOutRemaining < 0f) _fadeOutRemaining = 0f;
        }
    }

    private void OnGUI()
    {
        if (!Plugin.HudEnabled.Value) return;

        EnsureStyle();
        if (_shouldShowGrace) DrawGraceTimer();
        DrawChassisLabel();
        DrawReliefLabel();
        DrawCartGuardLabel();
    }

    private void EnsureStyle()
    {
        int desiredFontSize = Plugin.HudFontSize.Value;
        if (_style == null || _style.fontSize != desiredFontSize)
        {
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = desiredFontSize,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                wordWrap = false, // keep each HUD label on one line (longer ones like "Powering Down" wrapped)
            };
        }
    }

    private void DrawGraceTimer()
    {
        string text;
        Color color;

        if (_currentTimer > 0f)
        {
            int minutes = (int)(_currentTimer / 60f);
            int seconds = (int)(_currentTimer % 60f);
            text = $"Solo grace: {minutes}:{seconds:D2}";

            if (_currentTimer > 30f) color = new Color(0.4f, 1f, 0.4f, 1f);
            else if (_currentTimer > 10f) color = new Color(1f, 0.9f, 0.3f, 1f);
            else color = new Color(1f, 0.4f, 0.4f, 1f);
        }
        else
        {
            float alpha = Mathf.Clamp01(_fadeOutRemaining / FadeOutSeconds);
            text = "Solo grace ended";
            color = new Color(1f, 0.3f, 0.3f, alpha);
        }

        var rect = new Rect((Screen.width / 2f) - 150f, 20f, 300f, 30f);

        _style.normal.textColor = new Color(0f, 0f, 0f, color.a * 0.85f);
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, _style);

        _style.normal.textColor = color;
        GUI.Label(rect, text, _style);
    }

    private void DrawChassisLabel()
    {
        if (!Plugin.ReviveEnabled.Value) { OnChassisGate(); return; }
        if (!SemiFunc.RunIsLevel()) { OnChassisGate(); return; }

        var pc = PlayerController.instance;
        if (pc == null || pc.playerAvatarScript == null) { OnChassisGate(); return; }

        string steamID = SemiFunc.PlayerGetSteamID(pc.playerAvatarScript);
        int count = SpareChassisInventory.Count(steamID);

        string text;
        Color color;
        if (_chassisRevivingRemaining > 0f)
        {
            text = "Spare Chassis: Reviving";
            color = new Color(0.4f, 1f, 0.4f, 1f);
        }
        else if (count > 0)
        {
            text = count > 1
                ? $"Spare Chassis: Ready ({count})"
                : "Spare Chassis: Ready";
            color = new Color(0.55f, 0.85f, 1f, 1f);

            if (!_chassisWasDrawing)
            {
                _chassisWasDrawing = true;
                Plugin.Log.LogDebug($"[Chassis HUD] drawing label START 'Ready' count={count} (steamID='{steamID}')");
            }
        }
        else if (_chassisUsedFadeRemaining > 0f)
        {
            text = "Spare Chassis: Used";
            float alpha = Mathf.Clamp01(_chassisUsedFadeRemaining / ChassisUsedFadeSeconds);
            color = new Color(1f, 0.4f, 0.4f, alpha);
        }
        else
        {
            OnChassisGate();
            return;
        }

        var chassisRect = new Rect((Screen.width / 2f) - 150f, 55f, 300f, 30f);

        _style.normal.textColor = new Color(0f, 0f, 0f, color.a * 0.85f);
        GUI.Label(new Rect(chassisRect.x + 1, chassisRect.y + 1, chassisRect.width, chassisRect.height),
            text, _style);

        _style.normal.textColor = color;
        GUI.Label(chassisRect, text, _style);
    }

    private void DrawReliefLabel()
    {
        string text;
        Color color;

        if (_reliefActive)
        {
            text = "Extraction Relief: Active";
            color = new Color(0.55f, 0.85f, 1f, 1f);
        }
        else if (_reliefFadeRemaining > 0f)
        {
            text = "Extraction Relief: Active";
            float alpha = Mathf.Clamp01(_reliefFadeRemaining / ReliefFadeSeconds);
            color = new Color(0.55f, 0.85f, 1f, alpha);
        }
        else
        {
            return;
        }

        // Third row: grace timer is at y=20, chassis label at y=55, relief at y=90.
        var rect = new Rect((Screen.width / 2f) - 150f, 90f, 300f, 30f);

        _style.normal.textColor = new Color(0f, 0f, 0f, color.a * 0.85f);
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, _style);

        _style.normal.textColor = color;
        GUI.Label(rect, text, _style);
    }

    private void DrawCartGuardLabel()
    {
        // Display states, in priority order:
        //   Blocked!       — green pulse the instant the guard stops an enemy hit
        //   Powering Down  — orange, counting the linger seconds down while you stand near your loot
        //   Off            — red, protection paused because you're present (near + linger elapsed)
        //   Active         — blue, armed and protecting (you're away, or always-on mode)
        if (!_cartGuardArmed) return;

        string text;
        Color color;

        bool nearMode = Plugin.SoloCartGuardOnlyWhenAway.Value && SoloCartGuardPatch.NearLoot;

        if (_cartGuardFlashRemaining > 0f)
        {
            text = "Cart Guard: Blocked!";
            float alpha = Mathf.Clamp01(_cartGuardFlashRemaining / CartGuardFlashSeconds);
            color = new Color(0.45f, 1f, 0.45f, alpha);
        }
        else if (nearMode && SoloCartGuardPatch.ProtectingByDistance)
        {
            int secs = Mathf.CeilToInt(SoloCartGuardPatch.LingerRemaining);
            if (secs < 0) secs = 0;
            text = $"Cart Guard: Powering Down: {secs}s";
            color = new Color(1f, 0.6f, 0.2f, 1f);
        }
        else if (nearMode)
        {
            // Off (present, linger elapsed): hide the label entirely.
            return;
        }
        else
        {
            text = "Cart Guard: Active";
            color = new Color(0.55f, 0.85f, 1f, 1f);
        }

        // Fourth row (grace y=20, chassis y=55, relief y=90, cart guard y=120). Wider than the others so the
        // longer "Powering Down: Ns" text doesn't clip; still centered on screen.
        var rect = new Rect((Screen.width / 2f) - 260f, 120f, 520f, 30f);

        _style.normal.textColor = new Color(0f, 0f, 0f, color.a * 0.85f);
        GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), text, _style);

        _style.normal.textColor = color;
        GUI.Label(rect, text, _style);
    }

    private void OnChassisGate()
    {
        if (_chassisWasDrawing)
        {
            _chassisWasDrawing = false;
            Plugin.Log.LogDebug("[Chassis HUD] drawing label STOP");
        }
    }
}
