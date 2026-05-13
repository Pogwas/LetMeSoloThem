using System.Collections;
using HarmonyLib;
using LetMeSoloThem.State;
using Photon.Pun;
using UnityEngine;

namespace LetMeSoloThem.Patches;

internal static class RepoRefs
{
    internal static readonly AccessTools.FieldRef<PlayerAvatar, bool> AvatarIsLocal =
        AccessTools.FieldRefAccess<PlayerAvatar, bool>("isLocal");
    internal static readonly AccessTools.FieldRef<PlayerAvatar, bool> AvatarDeadSet =
        AccessTools.FieldRefAccess<PlayerAvatar, bool>("deadSet");
    internal static readonly AccessTools.FieldRef<PlayerAvatar, bool> AvatarIsDisabled =
        AccessTools.FieldRefAccess<PlayerAvatar, bool>("isDisabled");
    internal static readonly AccessTools.FieldRef<PlayerHealth, int> HealthValue =
        AccessTools.FieldRefAccess<PlayerHealth, int>("health");
    internal static readonly AccessTools.FieldRef<PlayerHealth, int> HealthMax =
        AccessTools.FieldRefAccess<PlayerHealth, int>("maxHealth");
    internal static readonly AccessTools.FieldRef<RoundDirector, bool> RoundExtractionActive =
        AccessTools.FieldRefAccess<RoundDirector, bool>("extractionPointActive");
    internal static readonly AccessTools.FieldRef<RoundDirector, ExtractionPoint> RoundExtractionCurrent =
        AccessTools.FieldRefAccess<RoundDirector, ExtractionPoint>("extractionPointCurrent");
    internal static readonly AccessTools.FieldRef<PlayerAvatar, PlayerDeathHead> AvatarDeathHead =
        AccessTools.FieldRefAccess<PlayerAvatar, PlayerDeathHead>("playerDeathHead");
    internal static readonly AccessTools.FieldRef<PlayerAvatar, PlayerTumble> AvatarTumble =
        AccessTools.FieldRefAccess<PlayerAvatar, PlayerTumble>("tumble");
    internal static readonly AccessTools.FieldRef<PlayerDeathHead, PhysGrabObject> DeathHeadPhysGrab =
        AccessTools.FieldRefAccess<PlayerDeathHead, PhysGrabObject>("physGrabObject");
    internal static readonly AccessTools.FieldRef<EnemyParent, Enemy> ParentEnemy =
        AccessTools.FieldRefAccess<EnemyParent, Enemy>("Enemy");
    internal static readonly AccessTools.FieldRef<EnemySlowMouthAttaching, EnemySlowMouth> AttachingSlowMouth =
        AccessTools.FieldRefAccess<EnemySlowMouthAttaching, EnemySlowMouth>("enemySlowMouth");
    internal static readonly AccessTools.FieldRef<EnemySlowMouthAttached, EnemySlowMouth> CameraVisualsSlowMouth =
        AccessTools.FieldRefAccess<EnemySlowMouthAttached, EnemySlowMouth>("enemySlowMouth");
    internal static readonly AccessTools.FieldRef<Enemy, int> EnemyTargetViewID =
        AccessTools.FieldRefAccess<Enemy, int>("TargetPlayerViewID");
}

public static class FreeChassisGranter
{
    private static EnemyDirector _lastSeenDirector;

    public static void TryGrantOnTick()
    {
        if (!Plugin.ReviveEnabled.Value) return;
        if (!Plugin.ReviveFreeChassisOnLevelStart.Value) return;
        if (!SemiFunc.RunIsLevel()) return;
        if (RoundDirector.instance == null) return;
        if (UnityEngine.Object.FindObjectsOfType<ExtractionPoint>().Length == 0) return;

        var ed = EnemyDirector.instance;
        if (ed == null) return;
        if (ReferenceEquals(ed, _lastSeenDirector)) return;

        var pc = PlayerController.instance;
        if (pc == null || pc.playerAvatarScript == null) return;

        string steamID = SemiFunc.PlayerGetSteamID(pc.playerAvatarScript);
        if (string.IsNullOrEmpty(steamID)) return;

        int extPtCount = UnityEngine.Object.FindObjectsOfType<ExtractionPoint>().Length;
        bool levelGen = LevelGenerator.Instance != null && LevelGenerator.Instance.Generated;
        Plugin.Log.LogDebug($"[Revive] grant context: scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}', extPts={extPtCount}, levelGen={levelGen}, roundDir={RoundDirector.instance != null}, edRef={ed.GetInstanceID()}");

        // First level of a run (levelsCompleted == 0) -> set chassis to exactly StartingLives.
        // Any subsequent level -> add LivesPerRound on top of carry-over.
        bool firstLevelOfRun = RunManager.instance == null || RunManager.instance.levelsCompleted == 0;
        if (firstLevelOfRun)
        {
            int target = Plugin.ReviveStartingLives.Value;
            int current = SpareChassisInventory.Count(steamID);
            SpareChassisInventory.Set(steamID, target);
            Plugin.Log.LogDebug($"[Revive] Spare Chassis (run start): {current} → {target} (steamID={steamID})");
        }
        else
        {
            int grant = Plugin.ReviveLivesPerRound.Value;
            if (grant <= 0)
            {
                Plugin.Log.LogDebug($"[Revive] Spare Chassis (per-round) grant=0, carry-over only (steamID={steamID})");
            }
            else
            {
                int current = SpareChassisInventory.Count(steamID);
                int newTotal = current + grant;
                SpareChassisInventory.Set(steamID, newTotal);
                Plugin.Log.LogDebug($"[Revive] Spare Chassis +{grant} (per-round, carry-over): {current} → {newTotal} (steamID={steamID})");
            }
        }
        _lastSeenDirector = ed;
    }
}

public static class ZeroHpReviveTrigger
{
    private static float _stuckTimer;

    public static void TryOnTick()
    {
        if (!Plugin.ReviveEnabled.Value) { _stuckTimer = 0f; return; }
        if (Plugin.ReviveStuckAtZeroSeconds.Value <= 0f) { _stuckTimer = 0f; return; }
        if (!SemiFunc.RunIsLevel()) { _stuckTimer = 0f; return; }

        var pc = PlayerController.instance;
        if (pc == null || pc.playerAvatarScript == null) { _stuckTimer = 0f; return; }

        var avatar = pc.playerAvatarScript;
        if (!RepoRefs.AvatarIsLocal(avatar)) { _stuckTimer = 0f; return; }

        // Vanilla death pipeline already engaged — PlayerDeathDonePatch will handle it.
        if (RepoRefs.AvatarDeadSet(avatar)) { _stuckTimer = 0f; return; }

        var ph = avatar.playerHealth;
        if (ph == null) { _stuckTimer = 0f; return; }

        int hp = RepoRefs.HealthValue(ph);
        if (hp > 0) { _stuckTimer = 0f; return; }

        // Only intervene if a chassis is available — otherwise forcing PlayerDeath would
        // just end the run without recovery (worse UX than the stuck state).
        string steamID = SemiFunc.PlayerGetSteamID(avatar);
        if (!SpareChassisInventory.Has(steamID)) { _stuckTimer = 0f; return; }

        _stuckTimer += Time.deltaTime;
        if (_stuckTimer < Plugin.ReviveStuckAtZeroSeconds.Value) return;

        Plugin.Log.LogWarning($"[Revive] HP={hp} stuck at zero for {_stuckTimer:F2}s without vanilla PlayerDeath; forcing death pipeline so chassis can trigger");
        _stuckTimer = 0f;

        try { avatar.PlayerDeath(-1); }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[Revive] Forced PlayerDeath threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(PlayerAvatar), "PlayerDeathDone")]
public static class PlayerDeathDonePatch
{
    [HarmonyPostfix]
    public static void Postfix(PlayerAvatar __instance)
    {
        if (!Plugin.ReviveEnabled.Value) return;
        if (__instance == null) return;
        if (!RepoRefs.AvatarIsLocal(__instance)) return;

        string steamID = SemiFunc.PlayerGetSteamID(__instance);
        if (!SpareChassisInventory.Has(steamID))
        {
            Plugin.Log.LogDebug("[Revive] Local player died without Spare Chassis — vanilla death proceeds");
            return;
        }

        if (!ShouldTrigger())
        {
            Plugin.Log.LogDebug("[Revive] Trigger conditions not met (MP teammate alive). Chassis preserved.");
            return;
        }

        if (!SpareChassisInventory.Consume(steamID)) return;

        Plugin.Log.LogDebug("[Revive] Triggering Spare Chassis self-revive (sync mode)");
        TryRescueSync(__instance);
    }

    private static void TryRescueSync(PlayerAvatar avatar)
    {
        try
        {
            // Capture death position BEFORE we resolve respawn (which can be DeathLocation,
            // i.e. avatar.transform.position) and BEFORE DoCustomRevive moves the avatar.
            Vector3 deathPos = avatar.transform.position;
            Plugin.Log.LogDebug($"[Revive] sync step 1: deathPos={deathPos}, computing respawn position");
            Vector3 respawnPos = ResolveRespawnPosition(avatar);
            Quaternion respawnRot = avatar.transform.rotation;

            var avatarDeathHead = RepoRefs.AvatarDeathHead(avatar);
            if (avatarDeathHead == null)
            {
                Plugin.Log.LogError("[Revive] sync ABORT: playerDeathHead is null");
                return;
            }
            var deathHeadPhys = RepoRefs.DeathHeadPhysGrab(avatarDeathHead);
            if (deathHeadPhys == null)
            {
                Plugin.Log.LogError("[Revive] sync ABORT: deathHead.physGrabObject is null");
                return;
            }

            Plugin.Log.LogDebug($"[Revive] sync step 2: teleporting death head to {respawnPos}");
            deathHeadPhys.Teleport(respawnPos, respawnRot);

            Plugin.Log.LogDebug("[Revive] sync step 3: custom revive (bypass vanilla to avoid singleton NREs)");
            DoCustomRevive(avatar, respawnPos, respawnRot);
            DetachNearbyEnemies(deathPos, respawnPos, 20f);
            ForceReleaseSpewer(avatar);

            Plugin.Log.LogDebug("[Revive] sync step 5: applying HP top-up + i-frames");
            ApplyHeal(avatar);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[Revive] sync rescue EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void DoCustomRevive(PlayerAvatar avatar, Vector3 position, Quaternion rotation)
    {
        TrySafe("avatar.gameObject.SetActive(true)", () => avatar.gameObject.SetActive(true));

        TrySafe("set avatar transform", () =>
        {
            avatar.transform.position = position;
            avatar.transform.rotation = rotation;
        });

        TrySafe("clear isDisabled + deadSet", () =>
        {
            RepoRefs.AvatarIsDisabled(avatar) = false;
            RepoRefs.AvatarDeadSet(avatar) = false;
        });

        TrySafe("playerAvatarVisuals", () =>
        {
            if (avatar.playerAvatarVisuals != null)
            {
                avatar.playerAvatarVisuals.gameObject.SetActive(true);
                avatar.playerAvatarVisuals.transform.position = position;
                avatar.playerAvatarVisuals.Revive();
            }
        });

        TrySafe("playerDeathHead.Reset", () => { var dh = RepoRefs.AvatarDeathHead(avatar); if (dh != null) dh.Reset(); });
        TrySafe("playerDeathEffects.Reset", () => { if (avatar.playerDeathEffects != null) avatar.playerDeathEffects.Reset(); });
        TrySafe("playerReviveEffects.Trigger", () => { if (avatar.playerReviveEffects != null) avatar.playerReviveEffects.Trigger(); });

        TrySafe("playerHealth.HealOther(1)", () => { if (avatar.playerHealth != null) avatar.playerHealth.HealOther(1, effect: true); });

        TrySafe("playerTransform + parent active", () =>
        {
            if (avatar.playerTransform != null)
            {
                avatar.playerTransform.position = position;
                if (avatar.playerTransform.parent != null)
                    avatar.playerTransform.parent.gameObject.SetActive(true);
            }
        });

        TrySafe("CameraPosition", () =>
        {
            if (CameraPosition.instance != null)
                CameraPosition.instance.transform.position = position;
        });

        TrySafe("CameraAim", () =>
        {
            if (CameraAim.Instance != null)
            {
                CameraAim.Instance.SetPlayerAim(Quaternion.Euler(0f, rotation.eulerAngles.y, 0f), _setRotation: true);
                CameraAim.Instance.OverrideNoSmooth(0.25f);
            }
        });

        TrySafe("GameDirector.Revive", () => { if (GameDirector.instance != null) GameDirector.instance.Revive(); });
        TrySafe("SpectateCamera.StopSpectate", () => { if (SpectateCamera.instance != null) SpectateCamera.instance.StopSpectate(); });
        TrySafe("PlayerController.Revive", () => { if (PlayerController.instance != null) PlayerController.instance.Revive(rotation.eulerAngles); });
        TrySafe("CameraGlitch.PlayLongHeal", () => { if (CameraGlitch.Instance != null) CameraGlitch.Instance.PlayLongHeal(); });

        Plugin.Log.LogDebug("[Revive] custom revive done");
    }

    private static void DetachNearbyEnemies(Vector3 deathPos, Vector3 respawnPos, float radius)
    {
        TrySafe("DetachNearbyEnemies", () =>
        {
            var ed = EnemyDirector.instance;
            if (ed == null || ed.enemiesSpawned == null) return;

            // For chase-target gate: any enemy actively targeting the local player gets caught
            // regardless of distance (it's the threat that just killed us or is closing in).
            int localViewID = -1;
            var pcInst = PlayerController.instance;
            if (pcInst != null && pcInst.playerAvatarScript != null && pcInst.playerAvatarScript.photonView != null)
            {
                localViewID = pcInst.playerAvatarScript.photonView.ViewID;
            }

            int considered = 0;
            int processed = 0;
            foreach (var ep in ed.enemiesSpawned)
            {
                if (ep == null) continue;
                considered++;

                // EnemyParent.transform.position is the spawn-root and does NOT track the live enemy
                // body — vanilla code reads Enemy.transform.position for player-distance checks
                // (EnemyParent.cs:185). Using ep.transform here would miss any enemy that wandered
                // away from its spawn point, which is exactly the enemies we want to deaggro.
                var enemy = RepoRefs.ParentEnemy(ep);
                Vector3 enemyPos = enemy != null ? enemy.transform.position : ep.transform.position;

                float distDeath = Vector3.Distance(enemyPos, deathPos);
                float distRespawn = Vector3.Distance(enemyPos, respawnPos);

                // Catch this enemy if EITHER (a) it's near death/respawn, OR (b) it's actively
                // chasing the local player (TargetPlayerViewID match + ChaseTimer > 0). The chase
                // gate ensures the actual threat that killed us is always handled even if it's in
                // an adjacent room or beyond the radius.
                bool inRadius = distDeath <= radius || distRespawn <= radius;
                bool chasingMe = enemy != null
                    && localViewID > 0
                    && RepoRefs.EnemyTargetViewID(enemy) == localViewID
                    && enemy.CheckChase();
                if (!inRadius && !chasingMe) continue;

                // Push outward from the respawn point — that's where the player will be after revive,
                // so this is the side we want a clear gap on.
                Vector3 awayDir = (enemyPos - respawnPos).normalized;
                if (awayDir == Vector3.zero) awayDir = Vector3.right;
                Vector3 pushTarget = respawnPos + awayDir * (radius * 1.5f);

                string enemyName = !string.IsNullOrEmpty(ep.enemyName) ? ep.enemyName : "?";
                string reason = chasingMe ? (inRadius ? "chase+radius" : "chase") : "radius";
                Vector3 prePos = enemyPos;
                string path;

                if (enemy != null)
                {
                    // Freeze: pause AI so current grab/attach/chase action breaks.
                    // DisableChase: blocks the enemy from re-acquiring the player as a chase target
                    //   (vanilla SetChaseTarget guards on DisableChaseTimer > 0).
                    // TeleportToPoint: room-level teleport to a random LevelPoint 100-200m away —
                    //   far stronger than "push 12m straight line" since the enemy now has to
                    //   pathfind through the level to get back. Returns null if no level point
                    //   exists at that distance; we fall back to the 12m push in that case.
                    TrySafe("enemy.Freeze", () => enemy.Freeze(1f));
                    TrySafe("enemy.DisableChase", () => enemy.DisableChase(2f));

                    LevelPoint landed = null;
                    try { landed = enemy.TeleportToPoint(100f, 200f); }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning($"[Revive] TeleportToPoint threw: {ex.GetType().Name}: {ex.Message}");
                    }
                    // If no level point at 100-200m exists (small map), fall back to 30-100m
                    // before resorting to the weak 12m push.
                    if (landed == null)
                    {
                        try { landed = enemy.TeleportToPoint(30f, 100f); }
                        catch (System.Exception ex)
                        {
                            Plugin.Log.LogWarning($"[Revive] TeleportToPoint(30,100) threw: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                    if (landed == null)
                    {
                        TrySafe("enemy.EnemyTeleported (fallback)", () => enemy.EnemyTeleported(pushTarget));
                        path = "fallback-push";
                    }
                    else
                    {
                        path = "level-point";
                    }

                    Vector3 postPos = enemy.transform.position;
                    float moved = Vector3.Distance(prePos, postPos);
                    Plugin.Log.LogDebug(
                        $"[Revive]   {enemyName} pre={prePos} post={postPos} moved={moved:F1}m path={path} " +
                        $"reason={reason} distDeath={distDeath:F1} distRespawn={distRespawn:F1}");
                }
                else
                {
                    ep.transform.position = pushTarget;
                    Vector3 postPos = ep.transform.position;
                    float moved = Vector3.Distance(prePos, postPos);
                    Plugin.Log.LogDebug(
                        $"[Revive]   {enemyName} pre={prePos} post={postPos} moved={moved:F1}m path=parent-only " +
                        $"reason={reason} distDeath={distDeath:F1} distRespawn={distRespawn:F1}");
                }
                processed++;
            }

            // Break the local player out of tumble/grabbed state in case an enemy had them
            // in PlayerTumble.isTumbling at the moment of death.
            var pc = PlayerController.instance;
            if (pc != null && pc.playerAvatarScript != null)
            {
                var playerTumble = RepoRefs.AvatarTumble(pc.playerAvatarScript);
                if (playerTumble != null)
                {
                    TrySafe("player tumble release",
                        () => RepoRefs.AvatarTumble(pc.playerAvatarScript).TumbleRequest(_isTumbling: false, _playerInput: false));
                }
            }

            Plugin.Log.LogDebug(
                $"[Revive] DetachNearbyEnemies: deaggro'd {processed}/{considered} enemy(ies) within {radius:F1}m (death={deathPos}, respawn={respawnPos})");
        });
    }

    // Spewer (EnemySlowMouth) on local player attaches a jaw to playerAvatar.localCamera.transform via
    // EnemySlowMouthAttached. That component has NO isDisabled/OnDisable hook — its only cleanup
    // path is StateOutro, triggered when the SlowMouth body's currentState leaves {Attached, Puke, Detach}
    // (see EnemySlowMouthAttached.StateSynchingWithParentEnemy). Enemy.Freeze only sets FreezeTimer
    // and does NOT change currentState, so the camera jaw persists through revive. Force the body to
    // State.Leave so the natural outro chain runs.
    private static void ForceReleaseSpewer(PlayerAvatar localAvatar)
    {
        TrySafe("ForceReleaseSpewer", () =>
        {
            if (localAvatar == null) return;

            int releasedViaJaw = 0;
            int destroyedJaws = 0;
            var releasedSlowMouths = new System.Collections.Generic.HashSet<EnemySlowMouth>();

            // Primary path: walk the local camera for any EnemySlowMouthAttached
            // (the visible chattering jaw on the player's face). Each jaw holds a back-ref to
            // its parent SlowMouth body. Flip the body's state to Leave FIRST so it can't
            // respawn the jaw on a future frame, then destroy the jaw GameObject so the
            // visual artifact is gone immediately.
            if (localAvatar.localCamera != null)
            {
                var cameraJaws = localAvatar.localCamera
                    .GetComponentsInChildren<EnemySlowMouthAttached>(includeInactive: true);
                foreach (var jaw in cameraJaws)
                {
                    if (jaw == null) continue;
                    var slowMouth = RepoRefs.CameraVisualsSlowMouth(jaw);
                    if (slowMouth != null && FlipSlowMouthIfStuck(slowMouth))
                    {
                        releasedSlowMouths.Add(slowMouth);
                        releasedViaJaw++;
                    }
                    UnityEngine.Object.Destroy(jaw.gameObject);
                    destroyedJaws++;
                }
            }

            // Backup path: scene-wide scan for EnemySlowMouthAttaching with includeInactive=true.
            // Catches Spewers in pursuit (GoToPlayer states) before they've spawned a camera jaw.
            // EnemySlowMouthAttaching is instantiated at scene root by EnemySlowMouth.cs:491 with
            // no parent, so EnemyParent.GetComponentInChildren misses it.
            var attachings = UnityEngine.Object.FindObjectsOfType<EnemySlowMouthAttaching>(includeInactive: true);
            int matchedAttaching = 0;
            int releasedViaAttaching = 0;
            foreach (var a in attachings)
            {
                if (a == null) continue;
                if (a.targetPlayerAvatar != localAvatar) continue;
                matchedAttaching++;

                var slowMouth = RepoRefs.AttachingSlowMouth(a);
                if (slowMouth == null || releasedSlowMouths.Contains(slowMouth)) continue;
                if (FlipSlowMouthIfStuck(slowMouth))
                {
                    releasedSlowMouths.Add(slowMouth);
                    releasedViaAttaching++;
                }
            }

            Plugin.Log.LogDebug(
                $"[Revive] ForceReleaseSpewer: jaws destroyed={destroyedJaws}, released via jaw={releasedViaJaw}; " +
                $"scene Attaching={attachings.Length}, matching local={matchedAttaching}, released via attaching={releasedViaAttaching}");
        });
    }

    // Returns true if state was flipped. State.Leave is vanilla "give up on this player";
    // EnemySlowMouthAttached.StateSynchingWithParentEnemy then transitions to Outro.
    private static bool FlipSlowMouthIfStuck(EnemySlowMouth slowMouth)
    {
        var st = slowMouth.currentState;
        if (st == EnemySlowMouth.State.Attached
            || st == EnemySlowMouth.State.Puke
            || st == EnemySlowMouth.State.Detach
            || st == EnemySlowMouth.State.GoToPlayer
            || st == EnemySlowMouth.State.GoToPlayerOver
            || st == EnemySlowMouth.State.GoToPlayerUnder)
        {
            slowMouth.UpdateState(EnemySlowMouth.State.Leave);
            return true;
        }
        return false;
    }

    private static void TrySafe(string label, System.Action action)
    {
        try { action(); }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Revive] custom step '{label}' failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static void ApplyHeal(PlayerAvatar avatar)
    {
        try
        {
            var ph = avatar.playerHealth;
            if (ph == null)
            {
                Plugin.Log.LogError("[Revive] ApplyHeal: playerHealth null");
                return;
            }
            int maxHP = RepoRefs.HealthMax(ph);
            int curHP = RepoRefs.HealthValue(ph);
            int targetHP = Mathf.Max(1, maxHP * Plugin.ReviveHpPercent.Value / 100);
            int delta = targetHP - curHP;
            if (delta > 0) ph.Heal(delta, effect: false);
            ph.InvincibleSet(2f);
            Plugin.Log.LogDebug(
                $"[Revive] Self-revive COMPLETE: HP={RepoRefs.HealthValue(ph)}/{maxHP}, 2s i-frames");
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[Revive] ApplyHeal EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static bool ShouldTrigger()
    {
        bool isSolo = PhotonNetwork.CurrentRoom == null
            || PhotonNetwork.CurrentRoom.PlayerCount <= 1;
        if (isSolo) return true;
        if (!Plugin.ReviveWorksInMultiplayer.Value) return false;
        return AllOtherPlayersDead();
    }

    private static bool AllOtherPlayersDead()
    {
        var list = SemiFunc.PlayerGetList();
        if (list == null) return true;
        foreach (var p in list)
        {
            if (p == null) continue;
            if (RepoRefs.AvatarIsLocal(p)) continue;
            if (!RepoRefs.AvatarDeadSet(p)) return false;
        }
        return true;
    }

    private static Vector3 ResolveRespawnPosition(PlayerAvatar avatar)
    {
        switch (Plugin.ReviveRespawnLocation.Value)
        {
            case "Truck":
                return TruckRespawnPosition();
            case "DeathLocation":
                return avatar.transform.position;
            case "ExtractionPoint":
            default:
                var rd = RoundDirector.instance;
                if (rd != null && RepoRefs.RoundExtractionActive(rd))
                {
                    var ep = RepoRefs.RoundExtractionCurrent(rd);
                    if (ep != null && ep.safetySpawn != null)
                        return ep.safetySpawn.position;
                }
                Plugin.Log.LogDebug("[Revive] No active extraction point, falling back to truck");
                return TruckRespawnPosition();
        }
    }

    private static Vector3 TruckRespawnPosition()
    {
        if (TruckSafetySpawnPoint.instance != null)
            return TruckSafetySpawnPoint.instance.transform.position;
        if (TruckHealer.instance != null)
            return TruckHealer.instance.transform.position;
        Plugin.Log.LogError("[Revive] No truck respawn anchor found, using Vector3.zero");
        return Vector3.zero;
    }
}

internal static class PendingHeal
{
    private const int MaxRetries = 300;

    private static PlayerAvatar _avatar;
    private static int _retries;

    public static void Schedule(PlayerAvatar a)
    {
        _avatar = a;
        _retries = 0;
    }

    public static void TryOnTick()
    {
        if (_avatar == null) return;
        try
        {
            if (++_retries > MaxRetries)
            {
                Plugin.Log.LogError($"[Revive] Pending heal gave up after {MaxRetries} ticks (avatar.isDisabled never cleared)");
                _avatar = null;
                return;
            }
            if (RepoRefs.AvatarIsDisabled(_avatar)) return;
            Plugin.Log.LogDebug($"[Revive] Pending heal: avatar.isDisabled cleared after {_retries} ticks, applying heal");
            PlayerDeathDonePatch.ApplyHeal(_avatar);
            _avatar = null;
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[Revive] PendingHeal EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            _avatar = null;
        }
    }
}
