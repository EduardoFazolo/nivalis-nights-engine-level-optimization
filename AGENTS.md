# AGENTS.md

Guide for coding agents working on NNELO (NivalisNights-EngineLevelOptimizations), a BepInEx 6 IL2CPP performance mod
for the Unity game Nivalis Nights. Read this before changing anything.

## Layout

```
NNELO/                      the mod (net6.0, BepInEx 6 IL2CPP plugin, NNELO.dll)
  Core/                     shared services; Nnelo.cs is the facade (Nnelo.Log, .Config, .Game, .Il2Cpp, .Unity,
                            .Hooks, .Events, .Modules)
    Plugin.cs               entry point: GUID "nivalisnights.nnelo", registers modules
    HookManager.cs          native detours (Funchook via BepInEx INativeDetour), Harmony instance, deferred hooks
    Il2CppResolver.cs       classes, method pointers and field offsets by name; raw field reads
    UnitySymbols.cs         UnityPlayer.dll engine function RVAs per engine build (PDB GUID gated)
    GameInfo.cs             module bases, GameAssembly PE timestamp, UnityPlayer PDB GUID
    SelfTest.cs             startup check of offsets/addresses against the known build
    Driver.cs, Lifecycle.cs per-frame pump, events, F2 whole-mod toggle, toasts
  Modules/Performance/      every optimization (Optimizations.cs holds the original set and the Toggle type)
  Modules/Diagnostics/      TestPanel: compiled only with -p:Diagnostics=true (overlay, live toggles, A/B bench)
tools/Installer/            Windows Forms setup (net48) embedding NNELO.dll + the BepInEx archive
scripts/check-hygiene.sh    CI check: no personal paths, e-mails, binaries or local-only files
.github/workflows/ci.yml    CI (hygiene + installer build and install/uninstall test)
```

## Build

The mod references BepInEx's generated interop assemblies from an installed game
(`<GameDir>\BepInEx\interop`, `\core`). CI cannot build it. Build locally:

```
dotnet build NNELO/NNELO.csproj -c Release [-p:GameDir="..."]
dotnet build NNELO/NNELO.csproj -c Release -p:Diagnostics=true -o NNELO/bin/Diagnostics
dotnet build tools/Installer/Installer.csproj -c Release [-p:ModDll=... -p:BepInExZip=...]
```

The game must be closed to replace a DLL in `BepInEx\plugins`.

## Known target

- Unity 2020.3.44f1, IL2CPP, DX11, built-in render pipeline (deferred).
- GameAssembly.dll PE timestamp `0x6ABFCA71` (`GameInfo.KnownGameAssemblyTimestamp`).
- UnityPlayer PDB GUID `52A48DC2A85942EDBDD9083427CD882D1` (`UnitySymbols.KnownBuilds`). Engine RVAs come from Unity's
  public symbol server PDB for that build.
- Hardcoded offsets (Character fields, Animator internals) are only valid for these builds. SelfTest reports
  mismatches; engine hooks disable themselves on an unknown UnityPlayer build.

## Adding or changing an optimization

1. Each optimization is an `Optimizations.Toggle` { Name, Config (bool, default), Apply(on), Key (test-build hotkey,
   F1-F12 or null), Bench (included in the Shift+F2 suite) }. Return it from a module `Init(ConfigFile)` and add it to
   `Optimizations.All`.
2. Patch game classes only after gameplay starts (`Nnelo.Hooks.When(() => Nnelo.Events.InGameplay, ...)` or the lazy
   patch in `Optimizations.PatchCharacterWhenLive`). Never in `Plugin.Load`.
3. Measure with the test build: Shift+key runs a paired on/off benchmark (6 cycles x 2 x 6 s) and logs average frame
   time **and** 1% lows/hitches. Keep a change only if it shows a real gain (about 1-2 fps or clearly better 1% lows)
   with no visible difference. Delete it otherwise; don't leave dead toggles.
4. Update the README feature table and the "tried and rejected" table below.

## Hard rules (each learned from a broken build)

- **Crowd density is never reduced.** NPCs within 15 m must look exactly like the original game (full-rate
  animation, no pop-in, no T-poses).
- **Never delay or skip `Character.SetVisible`.** It also resets the animator step and re-enables the skeleton;
  delaying it caused T-posing, frozen NPCs.
- **Don't detour functions shorter than ~32 bytes** (IL2CPP folds tiny functions; the detour overruns into padding).
  `HookManager.Native` refuses them, and refuses a second hook on the same address.
- **Never let exceptions cross a native detour.** Catch everything inside detours. Don't wrap game methods that can
  throw IL2CPP exceptions (e.g. `Character.UpdateAll` throws when `Camera.main` is null): a C++ exception unwinding
  through managed frames crashes the game.
- **Harmony binds patch parameters by name.** Match the game's parameter names exactly (they come from the interop
  assemblies).
- **`FindObjectsOfType` freezes the main thread** (~900k objects). Never call it periodically.
- **Frame time is noisy** (the same spot swings by 10+ fps). Use the paired A/B bench; single toggles prove nothing.
- Unity's `GarbageCollector.GCMode` setter is stripped in this build.

## How the engine-level hook works (AnimatorBatching)

The game calls `Animator.Update(dt)` per NPC; in the engine that ends in `Animator::UpdateAvatars(list of one)`,
which schedules evaluation jobs and waits for them. The mod defers those single-element calls and issues them as
batches (grouped by delta time within 2%) at the start of `DirectorManager::ExecuteStage` and in LateUpdate. Each
deferred output is re-validated through its animator's playable-output handle before evaluation. The
`dynamic_array` layout and Animator offsets are documented in the class comment; they are engine-build specific.

## Tried and rejected (measured, don't retry without a new idea)

| Idea | Result |
|---|---|
| DirectX 12 (with patched compute shaders) | ~10% slower than DX11 |
| Delaying `SetVisible` | NPCs froze and T-posed |
| Idle-aware animation (standing NPCs throttled from 4 m) | nearby NPCs visibly choppy; no gain at 15 m |
| NPC decision throttle, lighting/rain/scanner throttles, draw distance x0.75, cloth rate, venue decision cap, focus raycast dedupe | not significant |
| Thread placement (main/render threads on dedicated cores) | 1% lows worse (42 -> 38 fps) |
| No opaque distance sort | -0.45 ms |
| NPC shadow cull distance | nothing to cull: only 0-2 NPCs are visible through shadows alone |
| Sky camera forward/no-occlusion | camera already configured that way |
| Billboard rotation skip (Harmony prefix per name tag) | patch overhead > saving |
| MagicaCloth distance disable | the game already does it (15 m + 5 m fade) |
| GC scheduling (collect at menus) | works, but no measured gain |
| Graphics jobs boot.config flags | DX11 already falls back to legacy graphics jobs |

## Repository hygiene

- No personal or machine-specific data in tracked files: no user names, user-profile paths, e-mail addresses.
  Use placeholders like `<game folder>` or `%USERPROFILE%`. `scripts/check-hygiene.sh` enforces this in CI.
- Never commit binaries, game files, BepInEx, generated interop assemblies, saves or extracted game data.
- Reverse-engineering notes and dumps are local-only and not part of this repository.
