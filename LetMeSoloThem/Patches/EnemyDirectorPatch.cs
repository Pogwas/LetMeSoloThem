using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace LetMeSoloThem.Patches;

[HarmonyPatch(typeof(EnemyDirector), "Start")]
public static class EnemyDirectorStartPatch
{
    internal static readonly AccessTools.FieldRef<EnemyDirector, float> SpawnIdlePauseTimerRef =
        AccessTools.FieldRefAccess<EnemyDirector, float>("spawnIdlePauseTimer");

    [HarmonyPostfix]
    public static void Postfix(EnemyDirector __instance)
    {
        int roomPlayerCount = PhotonNetwork.CurrentRoom != null
            ? PhotonNetwork.CurrentRoom.PlayerCount
            : 1;
        bool isSolo = roomPlayerCount <= 1;

        float vanilla = SpawnIdlePauseTimerRef(__instance);
        float floor = Plugin.SoloGraceFloor.Value;
        bool overrideMode = Plugin.SoloGraceOverrideMode.Value;

        if (!isSolo)
        {
            Plugin.Log.LogInfo(
                $"MP run (PhotonRoom.PlayerCount={roomPlayerCount}): vanilla spawnIdlePauseTimer={vanilla:F1}s — not boosting");
            return;
        }

        if (overrideMode)
        {
            SpawnIdlePauseTimerRef(__instance) = floor;
            Plugin.Log.LogInfo(
                $"Solo grace OVERRIDDEN: vanilla {vanilla:F1}s → forced {floor:F1}s (OverrideMode=true)");
            return;
        }

        float adjusted = Mathf.Max(vanilla, floor);
        if (adjusted == vanilla)
        {
            Plugin.Log.LogInfo(
                $"Solo run, vanilla already adequate: {vanilla:F1}s ≥ floor {floor}s (PhotonRoom.PlayerCount={roomPlayerCount})");
            return;
        }

        SpawnIdlePauseTimerRef(__instance) = adjusted;
        Plugin.Log.LogInfo(
            $"Solo grace rescued from slasher: {vanilla:F1}s → {adjusted:F1}s (PhotonRoom.PlayerCount={roomPlayerCount})");
    }

}

[HarmonyPatch(typeof(EnemyDirector), "Update")]
public static class EnemyDirectorMenuPausePatch
{
    private static float _preUpdateValue;
    private static bool _preUpdateCaptured;

    [HarmonyPrefix]
    public static void Prefix(EnemyDirector __instance)
    {
        if (!ShouldFreeze())
        {
            _preUpdateCaptured = false;
            return;
        }
        _preUpdateValue = EnemyDirectorStartPatch.SpawnIdlePauseTimerRef(__instance);
        _preUpdateCaptured = true;
    }

    [HarmonyPostfix]
    public static void Postfix(EnemyDirector __instance)
    {
        if (!_preUpdateCaptured) return;
        EnemyDirectorStartPatch.SpawnIdlePauseTimerRef(__instance) = _preUpdateValue;
        _preUpdateCaptured = false;
    }

    private static bool ShouldFreeze()
    {
        if (PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.PlayerCount > 1) return false;
        if (!SemiFunc.RunIsLevel()) return false;
        return MenuPageEsc.instance != null;
    }
}
