using HarmonyLib;
using UnityEngine;

namespace LetMeSoloThem.Patches;

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
        var players = SemiFunc.PlayerGetList();
        if (players == null || players.Count <= 1) return true; // solo
        if (Plugin.SoloCarryEscapeWorksInMultiplayer.Value) return true;
        // MP with WorksInMultiplayer=false: only fire if all OTHER players are dead
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
    // To be implemented in Task 4 + Task 8.
    public static void OnCarryStart(PlayerAvatar local)
    {
        Plugin.Log.LogDebug($"[CarryEscape] OnCarryStart (skeleton — identifier not yet wired)");
    }

    public static void TryOnTick()
    {
        // No-op until Task 8 wires in the tick logic.
    }
}
