using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LetMeSoloThem.Hud;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LetMeSoloThem;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.pogwas.letmesolothem";
    public const string PluginName = "Let me Solo Them";
    public const string PluginVersion = "0.5.0";

    internal static Plugin Instance;
    internal static ManualLogSource Log;

    internal static ConfigEntry<float> SoloGraceFloor;
    internal static ConfigEntry<bool> SoloGraceOverrideMode;
    internal static ConfigEntry<bool> HudEnabled;
    internal static ConfigEntry<int> HudFontSize;
    internal static ConfigEntry<bool> ReviveEnabled;
    internal static ConfigEntry<int> ReviveHpPercent;
    internal static ConfigEntry<string> ReviveRespawnLocation;
    internal static ConfigEntry<bool> ReviveWorksInMultiplayer;
    internal static ConfigEntry<bool> ReviveFreeChassisOnLevelStart;
    internal static ConfigEntry<int> ReviveStartingLives;
    internal static ConfigEntry<int> ReviveLivesPerRound;
    internal static ConfigEntry<float> ReviveStuckAtZeroSeconds;
    internal static ConfigEntry<bool> SoloSwordEnabled;
    internal static ConfigEntry<int> SoloSwordDamagePercent;
    internal static ConfigEntry<string> SoloItemSpawnLocation;
    internal static ConfigEntry<bool> SoloTranqEnabled;
    internal static ConfigEntry<float> SoloTranqStunSeconds;
    internal static ConfigEntry<float> SoloTranqShootCooldownSeconds;
    internal static ConfigEntry<bool> SoloDamageEnabled;
    internal static ConfigEntry<float> SoloDamageSoloMult;
    internal static ConfigEntry<float> SoloDamageDuoMult;
    internal static ConfigEntry<float> SoloDamageTrioMult;
    internal static ConfigEntry<float> SoloDamageQuadMult;
    internal static ConfigEntry<bool> SoloStrengthEnabled;
    internal static ConfigEntry<int> SoloStrengthStartingStrength;
    internal static ConfigEntry<int> SoloStrengthPerRound;
    internal static ConfigEntry<bool> SoloStrengthWorksInMultiplayer;
    internal static ConfigEntry<bool> SoloEnemyEnabled;
    internal static ConfigEntry<float> SoloEnemyDetectionSolo;
    internal static ConfigEntry<float> SoloEnemyDetectionDuo;
    internal static ConfigEntry<float> SoloEnemyDetectionTrio;
    internal static ConfigEntry<float> SoloEnemyDetectionQuad;

    private Harmony _harmony;
    private static GameObject _hudGO;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        Log.LogInfo($"{PluginName} v{PluginVersion} is loading...");

        SoloGraceFloor = Config.Bind(
            "Spawn Grace", "FloorSeconds", 105f,
            new ConfigDescription(
                "Solo grace-timer length in seconds. With OverrideMode=true (default): every solo level starts with EXACTLY this many seconds before enemies spawn. Set to 0 to remove the spawn-grace timer entirely (great for testing monsters). With OverrideMode=false: this value is used as a minimum floor only — vanilla wins if it rolls higher. No effect in multiplayer. Changes apply at the start of the next level.",
                new AcceptableValueRange<float>(0f, 600f)));

        SoloGraceOverrideMode = Config.Bind(
            "Spawn Grace", "OverrideMode", true,
            "When true (default): FloorSeconds is the EXACT grace-timer length applied to every solo level — set FloorSeconds=0 for instant monster spawn (testing), or 180 for a guaranteed 3-minute grace, etc. When false: FloorSeconds is just a minimum — vanilla rolls (12-180s) win if higher.");

        HudEnabled = Config.Bind(
            "HUD", "Enabled", true,
            "Show the on-screen Solo Grace timer during solo runs.");

        HudFontSize = Config.Bind(
            "HUD", "FontSize", 20,
            new ConfigDescription(
                "Pixel font size of the HUD text.",
                new AcceptableValueRange<int>(8, 60)));

        ReviveEnabled = Config.Bind(
            "Self-Revive", "Enabled", true,
            "Master toggle for the Spare Chassis self-revive system. When false, the free-on-level-start grant is skipped and the death-intercept patch no-ops.");

        ReviveHpPercent = Config.Bind(
            "Self-Revive", "HpPercent", 50,
            new ConfigDescription(
                "Percent of max HP the player has after self-revive.",
                new AcceptableValueRange<int>(1, 100)));

        ReviveRespawnLocation = Config.Bind(
            "Self-Revive", "RespawnLocation", "ExtractionPoint",
            new ConfigDescription(
                "Where the player respawns after self-revive.",
                new AcceptableValueList<string>("ExtractionPoint", "Truck", "DeathLocation")));

        ReviveWorksInMultiplayer = Config.Bind(
            "Self-Revive", "WorksInMultiplayer", true,
            "When true (default) self-revive also triggers in MP if every other player is dead. When false, it only triggers in solo (Photon room player count <= 1).");

        ReviveFreeChassisOnLevelStart = Config.Bind(
            "Self-Revive", "FreeChassisOnLevelStart", true,
            "Master on/off for the free Spare Chassis grants (both StartingLives and LivesPerRound). When true (default) you get StartingLives chassis at the start of a run and LivesPerRound more at the start of each subsequent level. When false, no chassis are ever granted — disable replenishment without flipping the master Enabled toggle.");

        ReviveStartingLives = Config.Bind(
            "Self-Revive", "StartingLives", 1,
            new ConfigDescription(
                "How many Spare Chassis (extra lives) you start each new run with. Applied once, on the first level of a fresh expedition — it sets your chassis count to exactly this. 0 = start with none. 1 (default) = start with one. Range 0–99.",
                new AcceptableValueRange<int>(0, 99)));

        ReviveLivesPerRound = Config.Bind(
            "Self-Revive", "LivesPerRound", 1,
            new ConfigDescription(
                "Extra Spare Chassis granted at the start of each level AFTER the first — added on top of however many you've still got (carry-over). 1 (default) = +1 each level. 0 = no per-round replenishment (you just keep whatever's left of your StartingLives). Range 0–99.",
                new AcceptableValueRange<int>(0, 99)));

        ReviveStuckAtZeroSeconds = Config.Bind(
            "Self-Revive", "StuckAtZeroSeconds", 0.5f,
            new ConfigDescription(
                "Backup trigger: if your HP is at 0 but vanilla PlayerDeath never fires (e.g. self-destruct paths that bypass PlayerHealth.Hurt), force the death pipeline after this many seconds so the chassis can revive you. 0.5 (default) gives vanilla a half-second to fire normally before we step in. Set to 0 to disable the backup entirely (you'll stay stuck at 0 HP in those edge cases).",
                new AcceptableValueRange<float>(0f, 5f)));

        SoloSwordEnabled = Config.Bind(
            "Solo Sword", "Enabled", true,
            "When true (default), the local player is granted ONE sword with unlimited durability. It re-spawns at the start of each new level if the one you were carrying got destroyed in the level transition; if you still have it, no new one spawns. Only that specific sword instance has its durability sustained — other swords break normally. Toggle off to disable the grant entirely.");

        SoloSwordDamagePercent = Config.Bind(
            "Solo Sword", "DamagePercent", 50,
            new ConfigDescription(
                "Percent of the granted sword's original damage values (both playerDamage and enemyDamage on its HurtCollider). 50 (default) halves it as a balance for the unlimited durability. 100 = no reduction. Only affects the granted sword instance — other swords keep their full damage.",
                new AcceptableValueRange<int>(1, 100)));

        SoloItemSpawnLocation = Config.Bind(
            "Solo Sword", "SpawnLocation", "Player",
            new ConfigDescription(
                "Where the granted Solo Sword and Solo Tranq spawn at level start. 'Player' (default) = right in front of you, where you can actually pick them up. 'ExtractionPoint' = at the level's extraction point (the old behavior — can be far from where you start the level, so you may never find them).",
                new AcceptableValueList<string>("Player", "ExtractionPoint")));

        SoloTranqEnabled = Config.Bind(
            "Solo Tranq", "Enabled", true,
            "When true (default), the local player is granted ONE Tranq Gun, spawned alongside the Solo Sword (same re-grant-per-level logic, same SpawnLocation). Stuns enemies on hit; pairs well with the sword for handling Critical-tier enemies (Robe, Clown, Huntsman) that one-shot you in melee. Toggle off to skip the grant.");

        SoloTranqStunSeconds = Config.Bind(
            "Solo Tranq", "StunSeconds", 3f,
            new ConfigDescription(
                "Stun duration applied by darts fired from the granted Tranq Gun. Vanilla = 18s. Default 3 = brief disable, encourages chained shots from the unlimited-ammo gun rather than one-shot lockdowns. Only affects darts from the granted gun — other tranqs in MP keep vanilla 18s.",
                new AcceptableValueRange<float>(0.5f, 60f)));

        SoloTranqShootCooldownSeconds = Config.Bind(
            "Solo Tranq", "ShootCooldownSeconds", 2f,
            new ConfigDescription(
                "Cooldown between shots in seconds. Vanilla ItemGun.shootCooldown defaults to 1s (1 shot/sec). 2 (default) = 1 shot every 2s. Higher values slow fire rate further. Combined with unlimited ammo + short stun, this makes the tranq feel like a steady utility rather than a panic-spam tool.",
                new AcceptableValueRange<float>(0.1f, 10f)));

        SoloDamageEnabled = Config.Bind(
            "Solo Damage", "Enabled", true,
            "Master toggle for player-incoming-damage scaling by player count. When false, vanilla damage applies to all damage paths (Hurt / HurtOther / HurtOtherRPC).");

        SoloDamageSoloMult = Config.Bind(
            "Solo Damage", "SoloMultiplier", 0.5f,
            new ConfigDescription(
                "Damage multiplier when 1 player is in the run (solo). 1.0 = vanilla, 0.5 (default) = take half damage, 0.0 = invuln. Applied as a Prefix on PlayerHealth.Hurt before health subtraction.",
                new AcceptableValueRange<float>(0f, 2f)));

        SoloDamageDuoMult = Config.Bind(
            "Solo Damage", "DuoMultiplier", 0.75f,
            new ConfigDescription(
                "Damage multiplier when 2 players are in the run. 0.75 (default) = take 75% damage. 1.0 = vanilla.",
                new AcceptableValueRange<float>(0f, 2f)));

        SoloDamageTrioMult = Config.Bind(
            "Solo Damage", "TrioMultiplier", 0.9f,
            new ConfigDescription(
                "Damage multiplier when 3 players are in the run. 0.9 (default) = mild discount. 1.0 = vanilla.",
                new AcceptableValueRange<float>(0f, 2f)));

        SoloDamageQuadMult = Config.Bind(
            "Solo Damage", "QuadMultiplier", 1f,
            new ConfigDescription(
                "Damage multiplier when 4 or more players are in the run. 1.0 (default) = vanilla. Set above 1.0 to make full lobbies harder.",
                new AcceptableValueRange<float>(0f, 2f)));

        SoloStrengthEnabled = Config.Bind(
            "Solo Strength", "Enabled", true,
            "Master toggle for the Solo Strength grant system. When false, no Strength upgrade levels are granted on level start.");

        SoloStrengthStartingStrength = Config.Bind(
            "Solo Strength", "StartingStrength", 3,
            new ConfigDescription(
                "Strength upgrade levels granted ONCE on the first level of each new run. Each level = +0.2 grab strength (vanilla math). 3 (default) = +0.6 grab strength bonus at run start. 0 = disable the run-start grant. Range 0-10.",
                new AcceptableValueRange<int>(0, 10)));

        SoloStrengthPerRound = Config.Bind(
            "Solo Strength", "StrengthPerRound", 0,
            new ConfigDescription(
                "Strength upgrade levels granted on EACH subsequent level start (after level 1). 0 (default) = front-loaded grant only, no per-round drip. 1+ = steady accumulation across the run. Range 0-10.",
                new AcceptableValueRange<int>(0, 10)));

        SoloStrengthWorksInMultiplayer = Config.Bind(
            "Solo Strength", "WorksInMultiplayer", false,
            "When false (default), the grant only fires in true solo (Photon room player count <= 1). When true, the host (master client) also gets the grant in MP lobbies. Default false because Strength as a personal-stat buff doesn't fit the mod's solo-rebalance theme when teammates can share carrying duty.");

        SoloEnemyEnabled = Config.Bind(
            "Solo Enemy Awareness", "Enabled", true,
            "Master toggle for Solo Enemy Awareness. When false, enemy detection is left at vanilla values.");

        SoloEnemyDetectionSolo = Config.Bind(
            "Solo Enemy Awareness", "DetectionSolo", 0.5f,
            new ConfigDescription(
                "Detection intensity when 1 player is in the run. 1.0 = vanilla; lower = enemies detect you slower and from shorter range (vision-cone range scaled down, more consecutive sightings needed before they aggro); 0.0 = enemies effectively cannot spot you via their vision cone. 0.5 (default) is roughly half as easily detected. Point-blank close-range detection is deliberately left intact.",
                new AcceptableValueRange<float>(0f, 1f)));

        SoloEnemyDetectionDuo = Config.Bind(
            "Solo Enemy Awareness", "DetectionDuo", 0.75f,
            new ConfigDescription(
                "Detection intensity when 2 players are in the run. 0.75 (default) = mild dampening. 1.0 = vanilla.",
                new AcceptableValueRange<float>(0f, 1f)));

        SoloEnemyDetectionTrio = Config.Bind(
            "Solo Enemy Awareness", "DetectionTrio", 0.9f,
            new ConfigDescription(
                "Detection intensity when 3 players are in the run. 0.9 (default) = slight dampening. 1.0 = vanilla.",
                new AcceptableValueRange<float>(0f, 1f)));

        SoloEnemyDetectionQuad = Config.Bind(
            "Solo Enemy Awareness", "DetectionQuad", 1f,
            new ConfigDescription(
                "Detection intensity when 4 or more players are in the run. 1.0 (default) = exact vanilla — the feature no-ops in full lobbies.",
                new AcceptableValueRange<float>(0f, 1f)));

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll();

        SceneManager.sceneLoaded += OnSceneLoaded;

        Log.LogInfo($"{PluginName} loaded successfully. Solo runs incoming.");
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Log.LogDebug($"[HUD] sceneLoaded: '{scene.name}' (mode={mode}). HUD alive={_hudGO != null}");

        if (_hudGO == null)
        {
            _hudGO = new GameObject("LetMeSoloThem.SoloGraceHud", typeof(SoloGraceHud));
            DontDestroyOnLoad(_hudGO);
            Log.LogDebug($"[HUD] (re)created after scene '{scene.name}': scene='{_hudGO.scene.name}', activeInHierarchy={_hudGO.activeInHierarchy}");
        }
    }
}
