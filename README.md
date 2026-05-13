# Let me Solo Them

A solo-rebalance mod for [R.E.P.O.](https://store.steampowered.com/app/3241660/REPO/) (Semiwork, 2025). Named after the Elden Ring "Let Me Solo Her" meme — gives the lone player a fighting chance.

Every gameplay value is exposed as a config entry. Tune to taste.

## Features

- **Spawn Grace** — Forces a configurable grace timer (default 105s) at the start of every solo level before enemies start spawning.
- **Spare Chassis self-revive** — Free extra-lives system. Configurable starting lives, per-level grants, respawn location, and HP-on-revive. Includes a zero-HP backup that catches self-destruct edge cases the vanilla death pipeline misses.
- **Solo Sword + Tranq starter kit** — Spawns an unlimited-durability sword and a tranq gun in front of you at level start. Both are configurable (damage %, stun duration, fire rate, spawn location).
- **Solo Damage Multiplier** — Scales player-incoming damage by lobby size. Defaults: solo 0.5×, duo 0.75×, trio 0.9×, quad 1.0× (vanilla).
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

## Bug reports

Please open an [Issue](https://github.com/Pogwas/LetMeSoloThem/issues) and include:

- R.E.P.O. game version
- Mod version
- Your `BepInEx/LogOutput.log` (or the relevant ~50 lines around the bug)
- Other plugins installed
- Steps to reproduce

## License

MIT — see [LICENSE](LICENSE).
