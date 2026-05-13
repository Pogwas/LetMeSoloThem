using HarmonyLib;
using UnityEngine;

namespace LetMeSoloThem.Patches;

// Scales incoming player damage by a multiplier keyed to active player count.
// Solo (1 player) takes Plugin.SoloDamageSoloMult of vanilla damage; duo / trio / quad+
// each have their own multipliers. Hooks PlayerHealth.Hurt as a Prefix and modifies the
// `damage` argument before health subtraction. Multiplier of 0 gives effective invuln
// (vanilla Hurt early-returns when damage <= 0); 1.0 is vanilla. Covers all damage paths
// because HurtOther / HurtOtherRPC both fan into Hurt.
[HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Hurt))]
public static class SoloDamageMultiplierPatch
{
    [HarmonyPrefix]
    public static void Prefix(ref int damage)
    {
        if (!Plugin.SoloDamageEnabled.Value) return;
        if (damage <= 0) return;

        int count = SemiFunc.PlayerGetList()?.Count ?? 1;
        if (count < 1) count = 1;

        float mult = count switch
        {
            1 => Plugin.SoloDamageSoloMult.Value,
            2 => Plugin.SoloDamageDuoMult.Value,
            3 => Plugin.SoloDamageTrioMult.Value,
            _ => Plugin.SoloDamageQuadMult.Value,
        };

        if (Mathf.Approximately(mult, 1f)) return;

        int orig = damage;
        damage = Mathf.Max(0, Mathf.RoundToInt(orig * mult));
        Plugin.Log.LogDebug($"[SoloDamage] players={count} mult={mult:F2} damage {orig} → {damage}");
    }
}
