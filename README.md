# NivalisNights-EngineLevelOptimizations (NNELO)

A performance mod for **Nivalis Nights** that makes busy areas run smoother without changing how the game looks.

Nivalis Nights is limited by one CPU thread (Unity's main thread), not by the graphics card. In crowded places like
the Meridian Market that thread spends most of the frame animating hundreds of NPCs, preparing them for rendering
and waiting for worker threads. NNELO removes waste from that work. Some fixes go inside the Unity engine itself,
hence the name.

**What it never does:** reduce the crowd, lower graphics settings, or make nearby NPCs look different. Every NPC
within 15 m animates exactly as in the original game.

## Results

Busy Meridian Market, Very High quality, 1080p, Ryzen 7 3700X + GTX 1070. Each change was measured with paired
on/off runs in the same spot. Your numbers will differ with your hardware.

| Change | Measured effect |
|---|---|
| Whole original set of fixes | 26.1 → 32.4 fps (+24%) |
| NPCs as separate transform roots | 40.5 → 46.5 fps (+15%) |
| NPC animation batching (engine-level) | 1% lows 23.9 → 30.9 fps (fewer drops) |
| Idle look-at off | +0.6 ms per frame |
| SALSA face pause for off-screen NPCs | +0.56 ms per frame |
| Sky camera at half rate | +0.46 ms per frame |
| Scene-search cache | 50–100 ms hitches when opening storage or menus: gone |

## Install

### Easiest: the installer

1. Download `NNELO-Setup.exe` from the [Releases](../../releases) page.
2. Run it. It finds the game through Steam, installs the BepInEx mod loader and the mod, and leaves your saves alone.
3. Start the game from Steam. **The first start takes a few minutes** with a black or frozen window while BepInEx
   prepares itself (it needs internet once). Later starts are normal.

To remove it, run the same installer and click **Uninstall**. If the installer put BepInEx there and no other mods
use it, BepInEx is removed too, and the game is back to normal.

### Manual

1. Install [BepInEx 6.0.0-be.788 (Unity IL2CPP, x64)](https://builds.bepinex.dev/projects/bepinex_be) into the game
   folder and start the game once.
2. Copy `NNELO.dll` into `<game folder>\BepInEx\plugins\`.

## Using it

- **F2** switches all optimizations off and on, so you can compare in-game.
- Settings are in `<game folder>\BepInEx\config\nivalisnights.nnelo.cfg` (created after the first start). Every
  optimization can be turned off there, and a few have tuning values such as distances.

## What it changes

| Setting | What it does |
|---|---|
| `AnimationThrottle` | NPCs beyond 15 m update their animation less often as distance grows (up to every 4th frame at 40 m). |
| `AnimatorBatching` | NPC animation updates are evaluated together in parallel instead of one at a time with a wait after each. Engine-level hook, only active on the known game build. |
| `NpcRootHierarchies` | Moves NPCs out of the single shared parent object so Unity can process their movement on several threads. |
| `IdleLookAtOff` | Turns an NPC's head-tracking component off while it isn't looking at anything, and back on when it is. |
| `SalsaOffscreenPause` | Pauses face and lip-sync processing for NPCs that are off-screen and not talking. |
| `SkyCameraHalfRate` | The small camera that renders the sky color for fog renders every other frame. |
| `PhysicsSyncOncePerFrame` | Syncs physics once per frame instead of before every NPC foot raycast. |
| `PhysicsRateLimit` | Runs physics at 45 Hz instead of 60 Hz. |
| `JobWorkerTuning` | Uses one worker thread per physical CPU core (minus one) instead of one per logical core. |
| `AiTickLimit` | The city NPC simulation runs at most 30 times per second. |
| `FootprintCameraThrottle` | The snow footprint texture is re-rendered every 4th frame. |
| `FindObjectOfTypeCache` | Caches scene searches that caused 50–100 ms hitches in menus and storage. |
| `LensFlareCache` | Computes lens flare visibility once per frame instead of several times. |

The mod also checks for monitor changes 4 times a second instead of every frame.

## Compatibility

- Made for the current Steam build of Nivalis Nights (Unity 2020.3.44f1). On startup the mod checks the game and
  engine builds. If the game updates, engine-level features switch themselves off instead of risking a crash, and
  the log says so.
- Other BepInEx mods can run alongside it.

## Tip: check your RAM speed

This game is CPU-bound, so memory speed matters a lot. If your RAM runs at its default speed (often 2133 MHz),
enabling its XMP/EXPO profile in the BIOS can be the biggest single gain. On the test machine it took the Meridian
Market from about 40 to about 58 fps. Test stability afterwards, especially with mixed memory kits.

## Known issues

- The game can show a crash when quitting. That happens without the mod too, and saves are written before it.

## Building from source

You need the game installed, with BepInEx installed and started once: BepInEx generates the assemblies the mod
compiles against (`<game folder>\BepInEx\interop`). These can't be included in this repository.

```
dotnet build NNELO/NNELO.csproj -c Release                       # NNELO/bin/Release/net6.0/NNELO.dll
dotnet build NNELO/NNELO.csproj -c Release -p:Diagnostics=true   # test build with an in-game benchmark panel
dotnet build NNELO/NNELO.csproj -p:GameDir="D:\Games\Nivalis Nights"   # if the game isn't in the default Steam folder
```

The test build adds an FPS overlay, live hotkeys per optimization and an automatic A/B benchmark (Shift+F2 runs all
of them). Results go to `BepInEx\LogOutput.log`.

The installer (`tools/Installer`) embeds the built `NNELO.dll` and the BepInEx archive (expected at
`.cache/bepinex.zip`, or pass `-p:BepInExZip=...`).

## License

MIT, see [LICENSE](LICENSE). BepInEx and the other libraries keep their own licenses.

## Credits

Built on [BepInEx](https://github.com/BepInEx/BepInEx), [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop),
[HarmonyX](https://github.com/BepInEx/HarmonyX) and [MonoMod](https://github.com/MonoMod/MonoMod). Not affiliated
with the developers of Nivalis Nights.
