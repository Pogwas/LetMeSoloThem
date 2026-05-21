using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace LetMeSoloThem.Patches;

// Shared helpers for the Solo Sword + Solo Tranq grant systems. Each grant re-spawns its item on
// every new level if the carried one was destroyed in the transition. Spawn location is config-
// driven (`[Solo Sword] SpawnLocation`): "Player" (default) = in front of the local player;
// "ExtractionPoint" = at an extraction point's safetySpawn (falling back to the player's position
// after a short wait if no extraction point is in the scene yet).
internal static class SoloGrantHelper
{
    private const float ExtractionPointWaitTimeout = 3f;
    private const float PlayerSettleSeconds = 2f;

    // Returns true if a spawn target was resolved. Sets pos/rot/spawnLoc out-params.
    // Returns false (caller should `return` and retry next tick) only if we're still within
    // the extraction-point wait window. After the window expires we always resolve to the
    // player position so something visible spawns.
    internal static bool TryGetSpawnTarget(
        PlayerAvatar avatar,
        ref float firstWaitTime,
        Vector3 offset,
        out Vector3 pos,
        out Quaternion rot,
        out string spawnLoc)
    {
        // Player mode (default): spawn in front of the local player — but wait a couple of seconds
        // first so the game has teleported the player to the level's spawn point (the position
        // right after Generated=true can be a stale load-in position the player never passes).
        if (Plugin.SoloItemSpawnLocation.Value == "Player")
        {
            if (firstWaitTime < 0f) firstWaitTime = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - firstWaitTime < PlayerSettleSeconds)
            {
                pos = default;
                rot = default;
                spawnLoc = null;
                return false; // not settled yet — caller retries next tick
            }
            var pt = avatar.transform;
            pos = pt.position + pt.forward * 1.0f + Vector3.up * 0.5f + offset;
            rot = pt.rotation;
            spawnLoc = $"player position {pt.position}";
            return true;
        }

        ExtractionPoint extPt = null;
        var allExtPts = Object.FindObjectsOfType<ExtractionPoint>();
        foreach (var ep in allExtPts)
        {
            if (ep != null && ep.safetySpawn != null) { extPt = ep; break; }
        }

        if (extPt != null)
        {
            pos = extPt.safetySpawn.position + Vector3.up * 0.5f + offset;
            rot = extPt.safetySpawn.rotation;
            spawnLoc = $"extraction point {extPt.safetySpawn.position}";
            return true;
        }

        if (firstWaitTime < 0f) firstWaitTime = Time.realtimeSinceStartup;
        float waited = Time.realtimeSinceStartup - firstWaitTime;
        if (waited < ExtractionPointWaitTimeout)
        {
            pos = default;
            rot = default;
            spawnLoc = null;
            return false;
        }

        var avatarT = avatar.transform;
        pos = avatarT.position + avatarT.forward * 1.0f + Vector3.up * 0.5f + offset;
        rot = avatarT.rotation;
        spawnLoc = $"player position {avatarT.position} (waited {waited:F1}s, no extraction point)";
        return true;
    }

    // Searches StatsManager.itemDictionary for an item whose SO/display name normalizes/contains
    // the given key (e.g. "sword", "tranq"). Returns null on no match (caller decides what to do).
    internal static Item FindItemByKey(string key)
    {
        if (StatsManager.instance == null || StatsManager.instance.itemDictionary == null) return null;
        if (StatsManager.instance.itemDictionary.Count == 0) return null;
        string lower = key.ToLower();
        foreach (var item in StatsManager.instance.itemDictionary.Values)
        {
            if (item == null) continue;
            string soName = item.name ?? "";
            string displayName = item.itemName ?? "";
            string normalized = soName.Replace("Item ", "").ToLower();
            if (normalized == lower
                || displayName.ToLower() == lower
                || soName.ToLower().Contains(lower)
                || displayName.ToLower().Contains(lower))
            {
                return item;
            }
        }
        return null;
    }

    // Common spawn step. Returns the spawned GameObject (or null on failure).
    internal static GameObject SpawnItem(Item item, Vector3 pos, Quaternion rot, string tag)
    {
        GameObject spawned;
        if (GameManager.instance.gameMode == 0)
        {
            spawned = Object.Instantiate(item.prefab.Prefab, pos, rot);
        }
        else
        {
            spawned = PhotonNetwork.InstantiateRoomObject(item.prefab.ResourcePath, pos, rot, 0);
        }
        if (spawned == null)
        {
            Plugin.Log.LogWarning($"[{tag}] Spawn returned null — will retry next tick");
            return null;
        }
        if (!spawned.activeSelf)
        {
            spawned.SetActive(true);
            Plugin.Log.LogInfo($"[{tag}] Spawned object was inactive — set active");
        }
        Plugin.Log.LogDebug($"[{tag}] Diagnostic: activeSelf={spawned.activeSelf}, activeInHierarchy={spawned.activeInHierarchy}, scene='{spawned.scene.name}', pos={spawned.transform.position}");
        return spawned;
    }

    // Adds a blue point light child to the spawned item so the player can identify
    // their granted solo tools at a glance. Caller picks intensity + range so the sword
    // can be brighter than the tranq.
    internal static void AttachBlueGlow(GameObject parent, float intensity, float range)
    {
        try
        {
            var glow = new GameObject("SoloItemGlow");
            glow.transform.SetParent(parent.transform, worldPositionStays: false);
            glow.transform.localPosition = Vector3.zero;
            var light = glow.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.25f, 0.6f, 1.0f);
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[SoloGrant] Glow attach threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

// Grants the local player ONE sword with unlimited durability. Originally once-per-session,
// but REPO destroys items between levels (e.g. through the shop transition), so we re-grant
// on each new level if the previously-granted GameObject was destroyed. If the player is
// still carrying it, the reference stays valid and we don't double-grant.
// The granted sword is tracked by PhotonView.ViewID; only that specific instance has its
// durability sustained. Any other sword the player obtains breaks normally.
public static class SoloSwordGranter
{
    // Permanent giveup flag — set only when the sword Item can't be found in itemDictionary
    // (would be a fatal lookup failure, not worth retrying every frame).
    private static bool _permanentGiveup;
    private static GameObject _grantedSwordGO;
    private static Item _cachedSwordItem;
    private static float _firstWaitTime = -1f;
    private static EnemyDirector _lastSeenDirector;

    public static bool IsOurSword(GameObject go)
    {
        return _grantedSwordGO != null && go != null && _grantedSwordGO == go;
    }

    public static void TryGrantOnTick()
    {
        if (!Plugin.SoloSwordEnabled.Value) return;
        if (_permanentGiveup) return;
        if (!SemiFunc.RunIsLevel()) return;
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated) return;

        // Reset the per-grant wait timer on each new gameplay level (new EnemyDirector instance),
        // so _firstWaitTime doesn't carry over and instantly fall back on re-grant.
        var ed = EnemyDirector.instance;
        if (ed != null && !ReferenceEquals(ed, _lastSeenDirector))
        {
            _firstWaitTime = -1f;
            _lastSeenDirector = ed;
        }

        // If the previously granted sword still exists, don't double-grant. Unity-null catches
        // destroyed objects so we naturally re-grant after a level transition that dropped it.
        if (_grantedSwordGO != null) return;

        var pc = PlayerController.instance;
        if (pc == null || pc.playerAvatarScript == null) return;

        if (_cachedSwordItem == null)
        {
            _cachedSwordItem = SoloGrantHelper.FindItemByKey("sword");
            if (_cachedSwordItem == null)
            {
                // Only give up permanently once the dictionary is actually populated and still
                // has no sword — a null/empty dictionary just means "not loaded yet, retry".
                if (StatsManager.instance != null && StatsManager.instance.itemDictionary != null
                    && StatsManager.instance.itemDictionary.Count > 0)
                {
                    Plugin.Log.LogWarning("[SoloSword] No sword found in itemDictionary; skipping grant");
                    _permanentGiveup = true;
                }
                return;
            }
            Plugin.Log.LogInfo($"[SoloSword] Matched sword item — name='{_cachedSwordItem.name}', itemName='{_cachedSwordItem.itemName}'");
        }

        // Resolve where to spawn (see `[Solo Sword] SpawnLocation`). Zero offset — the sword is
        // the primary item; the tranq spawns off this same point with a small side-offset.
        var avatar = pc.playerAvatarScript;
        Vector3 pos;
        Quaternion rot;
        string spawnLoc;
        if (!SoloGrantHelper.TryGetSpawnTarget(avatar, ref _firstWaitTime, Vector3.zero, out pos, out rot, out spawnLoc))
        {
            return; // still within the wait window
        }

        var spawned = SoloGrantHelper.SpawnItem(_cachedSwordItem, pos, rot, "SoloSword");
        if (spawned == null) return;

        // CRITICAL: assign _grantedSwordGO IMMEDIATELY, before the throw-prone glow + damage-
        // reduction below. If those threw with _grantedSwordGO still null, the next tick would
        // see null and re-spawn — the v0.2.2 "486 swords on the floor" bug.
        _grantedSwordGO = spawned;

        // Sword glow — brighter than the tranq so it stands out as the hero item.
        SoloGrantHelper.AttachBlueGlow(spawned, intensity: 5f, range: 6f);

        // Reduce damage on this instance only — modifying the spawned GameObject's HurtCollider
        // component, not the prefab/SO. Other swords keep full damage.
        try
        {
            var hurt = spawned.GetComponentInChildren<HurtCollider>();
            if (hurt != null)
            {
                int pct = Plugin.SoloSwordDamagePercent.Value;
                int origPlayer = hurt.playerDamage;
                int origEnemy = hurt.enemyDamage;
                // Preserve 0 (e.g. swords have playerDamage=0 so they can't hurt teammates).
                // Only floor non-zero originals at 1 so we don't accidentally null-out an active stat.
                hurt.playerDamage = origPlayer == 0 ? 0 : Mathf.Max(1, origPlayer * pct / 100);
                hurt.enemyDamage = origEnemy == 0 ? 0 : Mathf.Max(1, origEnemy * pct / 100);
                Plugin.Log.LogInfo($"[SoloSword] Reduced HurtCollider damage to {pct}%: player {origPlayer}→{hurt.playerDamage}, enemy {origEnemy}→{hurt.enemyDamage}");
            }
            else
            {
                Plugin.Log.LogWarning("[SoloSword] No HurtCollider found on spawned sword — damage reduction skipped");
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[SoloSword] Damage reduction threw: {ex.GetType().Name}: {ex.Message}");
        }

        Plugin.Log.LogInfo($"[SoloSword] Granted unlimited-durability sword at {pos} (spawn={spawnLoc}). Pick it up to equip.");
    }
}

// Pegs batteryLife to 100 every FixedUpdate tick on the tracked sword. Runs after the
// hit-RPC drain logic, so the next ItemMelee.Update sees a non-zero battery and skips
// MeleeBreak(). Other swords are unaffected.
[HarmonyPatch(typeof(ItemMelee), "FixedUpdate")]
public static class ItemMeleeFixedUpdatePatch
{
    [HarmonyPostfix]
    public static void Postfix(ItemMelee __instance)
    {
        if (!Plugin.SoloSwordEnabled.Value) return;
        if (!SoloSwordGranter.IsOurSword(__instance.gameObject)) return;
        var battery = __instance.GetComponent<ItemBattery>();
        if (battery != null && battery.batteryLife < 100f)
        {
            battery.batteryLife = 100f;
        }
    }
}

// Grants the local player ONE Tranq Gun, re-granted each new level if the carried one was lost, at the same time
// and location as the Solo Sword (with a small offset so they don't overlap). No special
// damage modification — Tranq Gun is non-lethal by design and useful as a stun tool against
// Critical-tier enemies (Robe, Clown, Huntsman). Standard battery drains as normal.
public static class SoloTranqGranter
{
    // Permanent giveup flag — set only when the tranq Item can't be found in itemDictionary.
    private static bool _permanentGiveup;
    private static GameObject _grantedTranqGO;
    private static Item _cachedTranqItem;
    private static float _firstWaitTime = -1f;
    private static EnemyDirector _lastSeenDirector;

    public static bool IsOurTranq(GameObject go)
    {
        return _grantedTranqGO != null && go != null && _grantedTranqGO == go;
    }

    public static void TryGrantOnTick()
    {
        if (!Plugin.SoloTranqEnabled.Value) return;
        if (_permanentGiveup) return;
        if (!SemiFunc.RunIsLevel()) return;
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated) return;

        // Reset per-grant wait timer on each new gameplay level (new EnemyDirector instance).
        var ed = EnemyDirector.instance;
        if (ed != null && !ReferenceEquals(ed, _lastSeenDirector))
        {
            _firstWaitTime = -1f;
            _lastSeenDirector = ed;
        }

        // If the previously granted tranq still exists, don't double-grant. Unity-null catches
        // destroyed objects so we naturally re-grant after a level transition that dropped it.
        if (_grantedTranqGO != null) return;

        var pc = PlayerController.instance;
        if (pc == null || pc.playerAvatarScript == null) return;

        if (_cachedTranqItem == null)
        {
            _cachedTranqItem = SoloGrantHelper.FindItemByKey("tranq");
            if (_cachedTranqItem == null)
            {
                if (StatsManager.instance != null && StatsManager.instance.itemDictionary != null
                    && StatsManager.instance.itemDictionary.Count > 0)
                {
                    Plugin.Log.LogWarning("[SoloTranq] No tranq gun found in itemDictionary; skipping grant");
                    _permanentGiveup = true;
                }
                return;
            }
            Plugin.Log.LogInfo($"[SoloTranq] Matched tranq item — name='{_cachedTranqItem.name}', itemName='{_cachedTranqItem.itemName}'");
        }

        // 0.8m offset to the side of the sword spawn so they don't physics-collide.
        var avatar = pc.playerAvatarScript;
        Vector3 pos;
        Quaternion rot;
        string spawnLoc;
        if (!SoloGrantHelper.TryGetSpawnTarget(avatar, ref _firstWaitTime, Vector3.right * 0.8f, out pos, out rot, out spawnLoc))
        {
            return; // still within wait window
        }

        var spawned = SoloGrantHelper.SpawnItem(_cachedTranqItem, pos, rot, "SoloTranq");
        if (spawned == null) return;

        _grantedTranqGO = spawned;

        // Tranq glow — standard intensity, dimmer than the sword.
        SoloGrantHelper.AttachBlueGlow(spawned, intensity: 2f, range: 3f);

        // Drain doubling is handled in the shot Postfix instead of here — the tranq prefab
        // uses the RemoveFullBar(1) path, not the float `batteryDrain` field, so modifying that
        // field has no effect. Compensation in the Postfix is mechanism-agnostic.

        // Override fire-rate cooldown on this instance only. ItemGun.shootCooldown is the
        // post-shot reload-state duration (StateReloading at line 689). Public float, instance
        // write doesn't affect other tranqs.
        try
        {
            var gun = spawned.GetComponent<ItemGun>();
            if (gun != null)
            {
                float origCooldown = gun.shootCooldown;
                gun.shootCooldown = Plugin.SoloTranqShootCooldownSeconds.Value;
                Plugin.Log.LogInfo($"[SoloTranq] shootCooldown {origCooldown:F2}s → {gun.shootCooldown:F2}s");

                // Modify the bullet PREFAB's HurtColliders at grant time using includeInactive=true.
                // ShootBulletRPC's GetComponentInChildren<HurtCollider>() (default false) returns null
                // because the dart's HurtCollider lives on a deactivated GameObject — so per-shot
                // Postfix patching has nothing to write to. Modifying the prefab itself sets the
                // value once for every dart this gun ever fires this session. Session-scoped change
                // (Unity reloads prefabs from disk on next launch).
                if (gun.bulletPrefab != null)
                {
                    var prefabHcs = gun.bulletPrefab.GetComponentsInChildren<HurtCollider>(includeInactive: true);
                    int patched = 0;
                    foreach (var c in prefabHcs)
                    {
                        if (c != null) { c.enemyStunTime = Plugin.SoloTranqStunSeconds.Value; patched++; }
                    }
                    Plugin.Log.LogInfo($"[SoloTranq] Patched {patched} HurtCollider(s) on bullet prefab to enemyStunTime={Plugin.SoloTranqStunSeconds.Value:F1}s");
                }
                else
                {
                    Plugin.Log.LogWarning("[SoloTranq] gun.bulletPrefab is null — stun override skipped");
                }
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[SoloTranq] gun-config override threw: {ex.GetType().Name}: {ex.Message}");
        }

        Plugin.Log.LogInfo($"[SoloTranq] Granted Tranq Gun at {pos} (spawn={spawnLoc}, stun={Plugin.SoloTranqStunSeconds.Value:F1}s). Pick it up to equip.");
    }
}

// Patches the dart fired BY our granted Tranq Gun so its enemyStunTime matches the configured
// value (default 3s instead of vanilla 18s). ItemGun.ShootBulletRPC instantiates a fresh
// ItemGunBullet from bulletPrefab and caches its HurtCollider into the gun's `hurtCollider`
// field (ItemGun.cs:459). Postfix grabs that reference and rewrites the stun duration on the
// just-spawned dart. Other tranq guns are unaffected.
[HarmonyPatch(typeof(ItemGun), nameof(ItemGun.ShootBulletRPC))]
public static class ItemGunShootBulletPatch
{
    private static readonly AccessTools.FieldRef<ItemGun, HurtCollider> ItemGunHurtColliderRef =
        AccessTools.FieldRefAccess<ItemGun, HurtCollider>("hurtCollider");
    // Internal int counterpart of batteryLife — gun's "can fire" gate uses batteryLifeInt,
    // not the float. Resetting only the float lets the int decay independently and the gun
    // eventually refuses to fire even though the visible bars would be full.
    private static readonly AccessTools.FieldRef<ItemBattery, int> BatteryLifeIntRef =
        AccessTools.FieldRefAccess<ItemBattery, int>("batteryLifeInt");

    private static int _shotCount;

    [HarmonyPostfix]
    public static void Postfix(ItemGun __instance)
    {
        if (!Plugin.SoloTranqEnabled.Value) return;
        if (!SoloTranqGranter.IsOurTranq(__instance.gameObject)) return;

        // The gun's cached hurtCollider (set via GetComponentInChildren in ShootBulletRPC) may
        // not be the same as ItemGunBullet's serialized `hurtCollider` field that actually
        // activates on impact. Walk the spawned bullet's full hierarchy and override every
        // HurtCollider's enemyStunTime so whichever one fires uses our value.
        float stunValue = Plugin.SoloTranqStunSeconds.Value;
        int hcCount = 0;
        var hc = ItemGunHurtColliderRef(__instance);
        if (hc != null)
        {
            var bulletRoot = hc.transform.root;
            var allHcs = bulletRoot.GetComponentsInChildren<HurtCollider>(includeInactive: true);
            foreach (var c in allHcs)
            {
                if (c != null) { c.enemyStunTime = stunValue; hcCount++; }
            }
        }

        _shotCount++;
        var battery = __instance.GetComponent<ItemBattery>();
        float lifeBefore = battery != null ? battery.batteryLife : -1f;
        int intBefore = battery != null ? BatteryLifeIntRef(battery) : -1;

        // Reset BOTH the float (visible) and int (gating) counterparts to full.
        if (battery != null)
        {
            battery.batteryLife = 100f;
            BatteryLifeIntRef(battery) = battery.batteryBars;
        }
        float lifeAfter = battery != null ? battery.batteryLife : -1f;
        int intAfter = battery != null ? BatteryLifeIntRef(battery) : -1;

        Plugin.Log.LogDebug($"[SoloTranq] Shot #{_shotCount} fired (stun={stunValue:F1}s patched on {hcCount} HurtCollider(s), battery={lifeBefore:F1}/{intBefore}→{lifeAfter:F1}/{intAfter})");
    }
}
