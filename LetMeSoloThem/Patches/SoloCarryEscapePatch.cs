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

internal enum EscapeReason
{
    TimerExpired,
    StruggleThreshold
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
    private static bool ShouldTrigger()
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
    private static float _timeRemaining;
    private static int _strugglePresses;
    private static float _lastInputTime;

    private static bool IsArmed => _currentKind != EnemyKind.None;

    public static void OnCarryStart(PlayerAvatar local)
    {
        // Identifier wiring comes in Task 5; for now log + early-return.
        Plugin.Log.LogDebug($"[CarryEscape] OnCarryStart fired (identifier not yet wired)");
        _localPlayer = local;
    }

    public static void TryOnTick()
    {
        if (!IsArmed) return;
        // Tick logic comes in Task 8.
    }

    private static void ClearState()
    {
        _currentEnemy = null;
        _currentKind = EnemyKind.None;
        _localPlayer = null;
        _timeRemaining = 0f;
        _strugglePresses = 0;
        _lastInputTime = 0f;
    }
}
