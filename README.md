# Let me Solo Them

A solo-rebalance mod for [R.E.P.O.](https://store.steampowered.com/app/3241660/REPO/) (Semiwork, 2025). Named after the Elden Ring "Let Me Solo Her" meme — gives the lone player a fighting chance.

Every gameplay value is exposed as a config entry. Tune to taste.

## Features

- **Spawn Grace** — Forces a configurable grace timer (default 105s) at the start of every solo level before enemies start spawning.
- **Spare Chassis self-revive** — Free extra-lives system. Configurable starting lives, per-level grants, respawn location, and HP-on-revive. Includes a zero-HP backup that catches self-destruct edge cases the vanilla death pipeline misses.
- **Solo Sword + Tranq starter kit** — Spawns an unlimited-durability sword and a tranq gun in front of you at level start. Both are configurable (damage %, stun duration, fire rate, spawn location).
- **Solo Damage Multiplier** — Scales player-incoming damage by lobby size. Defaults: solo 0.5×, duo 0.75×, trio 0.9×, quad 1.0× (vanilla).
- **Solo Strength grant** — Grants Strength upgrade levels at the start of a run (and optionally each level after), so a lone player can haul heavier loot. Configurable amounts and MP behavior.
- **Solo Enemy Awareness** — Scales enemy detection (vision range and sightings needed to aggro) down by lobby size, so a solo player isn't spotted as easily. Defaults: solo 0.5×, duo 0.75×, trio 0.9×, quad 1.0× (vanilla).
- **On-screen HUD** — Shows the grace countdown and chassis state.

## Installation

1. Install [BepInEx 5.4](https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/) for R.E.P.O.
2. Drop `LetMeSoloThem.dll` into `BepInEx/plugins/`.
3. Launch the game once to generate `BepInEx/config/com.pogwas.letmesolothem.cfg`, then edit it to taste — or use [REPOConfig](https://thunderstore.io/c/repo/p/nickklmao/REPOConfig/) for an in-game UI.

## Configuration sections

| Section | What it controls |
|---|---|
| `Spawn Grace` | Solo grace-timer length and override-vs-floor mode |
| `HUD` | Toggle and font size of the on-screen HUD |
| `Self-Revive` | Spare Chassis system: lives, HP%, respawn location, MP behavior, zero-HP backup |
| `Solo Sword` | Sword grant, damage %, spawn location |
| `Solo Tranq` | Tranq gun grant, stun duration, fire rate |
| `Solo Damage` | Player-count-keyed damage multipliers (solo/duo/trio/quad) |
| `Solo Strength` | Strength upgrade levels granted at run start and per round |
| `Solo Enemy Awareness` | Player-count-keyed enemy detection dampening |

## Bug reports

Please open an [Issue](https://github.com/Pogwas/LetMeSoloThem/issues) and include:

- R.E.P.O. game version
- Mod version
- Your `BepInEx/LogOutput.log` (or the relevant ~50 lines around the bug)
- Other plugins installed
- Steps to reproduce

## Changelog

### 0.5.0

Two new solo-rebalance features.

- **Solo Strength grant** — Automatically grants Strength upgrade levels at the start of a run (default +3), with an optional per-level drip. Lets a solo player carry heavier valuables without buying the upgrade. Off in multiplayer by default; all amounts are configurable.
- **Solo Enemy Awareness** — Scales enemy detection down by lobby size — vision-cone range is shortened and more consecutive sightings are needed before an enemy aggros. Defaults: solo 0.5×, duo 0.75×, trio 0.9×, quad 1.0× (vanilla). Point-blank close-range detection is left intact.

### 0.3.1

Compatibility fixes for the R.E.P.O. **Cosmetics Update** (game v0.4.x), which changed how the vanilla death pipeline interacts with the HUD, audio mixer, and inventory. In v0.3.0 the Spare Chassis revive would short-circuit the death pipeline before its tail-end cleanup ran, leaving three things broken after self-revive:

- **Audio cutout** — most 3D positional audio (footsteps, ambience, enemies, items, cart) went silent post-revive. The death pipeline transitions the audio mixer snapshot to `Spectate`, and `GameDirector.gameStateMain` is empty — nothing restores the snapshot on the Death→Main transition. Vanilla restores it via `PlayerVoiceChat.ToggleMixer(false)`, but `voiceChat` is null in singleplayer, so the restoration path can't fire. v0.3.1 calls `AudioManager.SetSoundSnapshot(On)` directly.
- **Inventory drop on death** — the Cosmetics Update added a new solo branch to `PlayerAvatar.PlayerDeathDone` that calls `Inventory.ForceUnequip()` unconditionally, dropping every hotbar item at the player's position. v0.3.1 adds a Harmony Prefix on `Inventory.ForceUnequip` that skips the drop when a chassis revive is incoming.
- **HUD vanishes** — `GameDirector.gameStateDeath` `SetActive(false)`s the HUD parent GameObject (health bar, hotbar icons, haul total, crosshair, and the floating $ tags above grabbed valuables). The matching `HUD.Show()` only fires after the full death-freeze countdown, which our revive interrupts. v0.3.1 calls `HUD.Show()` in the custom revive flow.

### 0.3.0

Initial Thunderstore release. Spawn grace, Spare Chassis self-revive, Solo Sword + Tranq starter kit, Solo Damage Multiplier, on-screen HUD.

## License

MIT — see [LICENSE](LICENSE).
