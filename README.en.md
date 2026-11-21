# Terraria Ambience

English · [简体中文](README.md)

An ambient-audio mod for Terraria: forest insects, cave rumble, rain and wind, footsteps that depend on the material underfoot, plus room reverb, occlusion and underwater dampening.

> **This is a maintained fork.** It fixes a batch of audio-lifetime, multiplayer and dedicated-server defects in
> upstream *Terraria Ambience* 0.3.7.1, and adds a Simplified Chinese config UI.
> Upstream thread: <https://forums.terraria.org/index.php?threads/terraria-ambience-mod.104161/>

## What this repository is

| Item | Value |
|---|---|
| Upstream | Terraria Ambience by RighteousRyan, version `0.3.7.1` |
| This repo | `ora233-fantaea/TerrariaAmbience-fantasea`, branches `1.4` (main) and `stream-1.4` (streaming feasibility) |
| Version | **Unchanged**, still `0.3.7.1`. Tell builds apart by their load source: `from Local` / `from Workshop` in the log |
| Runtime | tModLoader 2026.9.x / .NET 8 / FNA (FAudio backend) |
| Hard dependency | `TerrariaAmbienceAPI` 1.3.6.8 (Workshop `2826615136`; the DLL in `lib/` is byte-identical to it) |
| Size | 442 tracked files; 372 audio files (284 ogg + 88 wav), 65.1 MB on disk, about 447 MB decoded |

Every code-level change serves one goal: **make the same sound set behave correctly during long sessions, in multiplayer, and on dedicated servers.** The assets themselves were not re-authored.

## Features

**Ambient beds**

- Forest (four cue sets: morning / day / evening / night), jungle, desert, ocean, snow, Corruption/Crimson, cavern layer, Underworld
- Wind and rain, thunder, blizzards; dampening underwater and at depth

**Interaction sounds**

- Material-specific footsteps: grass, dirt, leaves, stone, smooth stone, marble, metal and metal plating, gems, sand and gravel, snow, thin/thick ice, glass, slime, water, wood / blunt wood / wood deck, wet ground
- Armor and vanity footstep layers; NPC and slime steps, water entry
- Chest open/close, crafting (work bench, furnace, alchemy table and more), hotbar item switching
- Campfire crackle, wood creaks, dungeon bone rattles

**Spatial audio**

- Room reverb: room size, decay and pre-delay derived from a flood-fill count of walls and tiles
- Occlusion: sources behind walls are low-passed and attenuated
- Multiplayer sync: other players' crafting sounds, day/night forest cues

## Installation

1. Install **tModLoader 2026.9.x** (the .NET 8 build).
2. Put `TerrariaAmbience.tmod` and `TerrariaAmbienceAPI.tmod` together into
   `Documents\My Games\Terraria\tModLoader\Mods\`.
3. Enable both in the mod list. **The main mod on its own will fail to load** — `build.txt` declares `modReferences`, a hard dependency.

When the same mod exists both on the Workshop and locally, tML prefers **local**, and the log states the source explicitly:

```
[tML]: Selected TerrariaAmbience 0.3.7.1 for tML 2026.9.3.1 from Local.
```

## Audio device requirement: do not exceed 48000 Hz

This is an engine-level constraint, not a mod constraint. The tModLoader FAQ states:

> Try changing your audio output rate to be less than or equal to 48000 Hz.
> **Rates greater than 48000 Hz may cause crashes while certain mods are enabled.**

The chain is as follows: the game does not choose an output format itself. It takes the current format of the Windows endpoint and opens a WASAPI shared-mode stream with it, and that negotiation is all-or-nothing. On failure, FAudio uses a C assertion rather than a managed exception:

```c
hr = IAudioClient_Initialize(...);
FAudio_assert(!FAILED(hr) && "Failed to initialize audio client!");
```

A failed `FAudio_assert` calls `abort()`: the process simply disappears. `client.log` records nothing; the only trace is a single assertion line in `Natives.log`.

Recommended: **48000 Hz, 16 or 24 bit, audio enhancements off, exclusive mode off.**
44.1 kHz works too. 96 / 192 kHz buys nothing (the assets are mostly 44.1k/48k, with a few 32k files and two at 96k); it only multiplies each voice's mixing buffer by 2–4x and pushes endpoint negotiation into the range where drivers fail most often.

The mod shows a warning on the main menu, but **only when the detected device rate is above 48 kHz**, and it reports the measured value.

## Configuration

Five panels, 42 fields:

| Panel | Contents |
|---|---|
| Sounds & filters | Reverb (enable, wetness, raycast mode, max area, refresh interval, advanced algorithm), occlusion, underwater dampening, plus campfire, crafting, slime, wood creak, animal, chest, splash and running sounds |
| Footsteps | Master volume, NPC footstep volume, wet footsteps, armor/vanity layers |
| Ambience | Overall volume and transition harshness, per-biome volumes (forest, snow, jungle, evil, desert, ocean, caverns, Underworld, breeze, underwater), boss music adjustment, debug overlay |
| UI | Main menu UI, ambience forecast UI |
| Inventory sounds | Enable, volume multiplier, dynamic inventory sounds |

**Chinese UI**: `Localization/zh-Hans_Mods.TerrariaAmbience.hjson` ships with the repo. tML selects the file by game language, so the game must be set to **Simplified Chinese** for it to apply; English keeps using the `en-US` file.

`Reverb wetness` (`reverbWetness`, 0–1, default `0.3`) is a local addition controlling how much of each source is sent into the reverb. Lower it if rooms still sound smeared, raise it if they sound too dry. It applies at runtime, with no rebuild required.

## Building from source

### Prerequisites

- .NET 8 SDK
- tModLoader 2026.9.x installed (for `tMLMod.targets` and the runtime assemblies)
- `lib/TerrariaAmbienceAPI.dll` (already in the repo)

### Key constraint: it must live under ModSources

`TerrariaAmbience.csproj` imports `..\tModLoader.targets` on its first line:

```xml
<Import Project="..\tModLoader.targets" />
```

So the project **must** sit at `<tModLoader>\ModSources\<any name>\`, where tModLoader generates the targets file. Building from anywhere else fails on the missing import.

For a hand-built isolated environment, a shim with the same name works:

```xml
<Project>
  <Import Project="D:\SteamLibrary\steamapps\common\tModLoader\tMLMod.targets" />
</Project>
```

### Compile

```bash
dotnet build TerrariaAmbience.csproj -c Release -p:BuildMod=false
```

`-p:BuildMod=false` produces only the DLL, no `.tmod`. The project publicizes FNA internals through `Krafs.Publicizer` (`handle`, `dspSettings`), which is what makes the native voice handle reachable — a prerequisite for the reverb and low-pass implementation.

### Package

```bash
dotnet tModLoader.dll -build "<abs>/ModSources/TerrariaAmbience" \
  -eac "<abs>/ModSources/TerrariaAmbience/bin/Release/net8.0/TerrariaAmbience.dll" \
  -unsafe true -savedirectory "<abs>/savedir"
```

Output lands in `savedir\tModLoader\Mods\`. The `-build` working directory must be the tModLoader install directory (or an isolated copy containing `tModLoader.dll` / `ReLogic.dll`), otherwise assembly resolution fails.

### Smoke test

```bash
dotnet tModLoader.dll -server -nosteam -savedirectory "<abs>/smoke" -modpath "<abs>/smoke/Mods"
```

Expect `Adding Content: Terraria Ambience → Finalizing Content → Adding Recipes` with no `Error`/`Exception` from the mod itself.

## Current status

`docs/STATUS.md` is the entry point and holds the full list with line-level evidence. Summary:

**Fixed (two batches)**

| Area | Representative items |
|---|---|
| Crashes / multiplayer | Multiplayer packet wrote `Int32→Vector2→String` but read `Int32→String→Vector2`; three missing `Main.dedServ` guards; `SoundInstance` null dereference; two packets that were never actually sent |
| Leaks and per-tick cost | Positional audio overwrote its instance on every `Play()`, leaking native voices; campfire detection scanned 100x100 tiles every tick; spawn search, raycasts and water-depth sampling lacked precondition gates; FAudio descriptors and the reverb voice were never released |
| Logic and feel | Thunder probability formula, crafting throttle, double chest sounds and an out-of-range index, pixel/tile mix-up, off-by-one in track randomization, low-pass stuck at 1.0, sticky "tiles above", missing crystal ball category, boss music volume not restored |
| Reverb | Room surface count was used as a 0–1 fraction: 2500 surfaces produced 7.5 s decay / 156 ms pre-delay / `RoomSize` 1250, while FAudio caps at 100. Now normalized through `roomScale` |
| Menu music | Non-50 tracks went silent after leaving a world. `Reuse()` does not flush queued buffers, so `Stop(Immediate)` must come first |

**Known, not fixed**

| Item | Nature |
|---|---|
| `[DefaultValue(true)]` on the `float entityFootstepsVolume` makes NPC footsteps always 0 | One-line fix, but it changes the default listening experience; pending decision |
| Asphalt / Sunplate / Titanstone belong to two stone footstep sets, so every step plays twice | De-duplicate the sets |
| 4 of the 8 stone footstep variants contain a second impact 20–40 ms in | Asset issue; only re-authoring helps |
| `TerrariaAmbienceAPI` A1–A12 (only the DLL is in the repo, no project) | The main mod never uses the streaming API; latent |
| 447 MB decoded, of which 43 biome beds account for 369 MB | Plan written, not implemented; see `docs/streaming-plan-2026-11-19.md` |

## Documentation

| Path | Contents |
|---|---|
| `docs/STATUS.md` | **Entry point**: status, both batches, mechanism notes, verification matrix, next steps |
| `docs/progress.md` | Original defect ledger (B1–B31, with line numbers and fixes) |
| `docs/qa-report-2026-11-18.md` | Silent QA findings (35 items, severity-ordered, two corrections included) |
| `docs/terraria-ambienceapi-analysis.md` | Full teardown of `TerrariaAmbienceAPI` (version lineage, three primitives, coupling, A1–A12) |
| `docs/fix-report-2026-11-19.md` | Item-by-item notes on the first batch; §8 is the full diagnosis of indoor footstep layering |
| `docs/streaming-plan-2026-11-19.md` | Staged streaming plan and FNA/NVorbis fact-checking |
| `docs/streaming-verification-2026-11-21.md` | Streaming feasibility: in-process decoding, seam quantification, API corrections |

## Credits

- **RighteousRyan** — original author; the mod and all audio design
- **William** — mod icon
- **Audio assets**: InspectorJ, morgantj, kinoton, globofonia, wjoojoo, JoelAudio, MegaShroom, straget
- Upstream community: <https://discord.gg/pT2BzSG>

Changes, diagnostics and documentation in this fork were produced by the fork maintainer; all original design credit belongs upstream.

## License

There is **no** `LICENSE` file, and upstream declares no license. Redistribution rights for the code and audio assets rest with the original author: confirm with RighteousRyan before publishing or redistributing. This fork does not change that.
