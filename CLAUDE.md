# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A collection of independent **Rain World (Remix/Downpour-era) code mods** by henpemaz and contributors, one Visual Studio solution (`RemixMods.sln`) with one C# project per mod: SplitScreenCoop, LizardSkin, SweetDreams, SpawnMenu, LapMod, MapWarp, Fartificer, UwUMod, Tag, and RemixMods (a scratch/sandbox mod). Projects do not reference each other. There are no tests, no linter, and no CI — verification means building and loading the mod in the game.

## Building

All projects target **.NET Framework 4.8** (SplitScreenCoop on `dynamic-split-screen`: 4.8.1) and are built with MSBuild on Windows. `msbuild` is not on PATH; use the full path from the Visual Studio 18 Community install (`dotnet` exists at `C:\Program Files\dotnet\dotnet.exe` but the old-style csproj files need MSBuild). Rain World with BepInEx is installed at `C:\Program Files (x86)\Steam\steamapps\common\Rain World`.

```bash
"/c/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe" SplitScreenCoop/SplitScreenCoop.csproj -p:Configuration=Release "-p:RainWorldPath=C:\Program Files (x86)\Steam\steamapps\common\Rain World" -nologo -v:m
```

Add `"-p:PostBuildEvent="` to compile without overwriting the committed DLL in `Mod/plugins/` (see below). `-m:1` avoids interleaved output when building the whole `RemixMods.sln`.

Without the game DLLs every project fails with `CS0246: The type or namespace name 'On' ... could not be found` — that means missing references, not broken code.

**Solver tests (SplitScreenCoop, `dynamic-split-screen` branch only):** `SplitLayoutSolver.cs` is pure C# with Unity math stubbed, so its tests compile with plain `csc` in seconds. Run from **PowerShell** (Git Bash rewrites `/nologo` and backslash paths into file names):

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe" /nologo /target:exe /out:"$env:TEMP\Tests.exe" SplitLayoutSolver.cs AdaptiveLayout.cs FrameStats.cs Tests\UnityMathStub.cs Tests\Program.cs Tests\AdaptiveTests.cs Tests\FrameStatsTests.cs; & "$env:TEMP\Tests.exe"
```

Run it from `SplitScreenCoop/`; expect `PASS: <n> layout checks` (21702 as of 2026-09-22). There is no way to select a single test — `Tests/Program.cs` is one console program that runs every check. Add a check for every new solver rule.

Game assemblies are **not** in the repo, and projects locate them in inconsistent ways — check the specific `.csproj` before building:

- `..\lib\*.dll` wildcard (RemixMods, LizardSkin, Tag): drop the game/BepInEx/MonoMod DLLs into `lib/` (git-ignored except its `.gitignore`).
- Absolute HintPaths to `C:\Program Files (x86)\Steam\steamapps\common\Rain World\...` (SplitScreenCoop, SpawnMenu, SweetDreams, LapMod, MapWarp). SplitScreenCoop additionally has a `RainWorldPath` property (default `D:\Steam\...`) fed into `AssemblySearchPaths`, so `-p:RainWorldPath=...` overrides it.
- `..\..\RemixReferences\` sibling folder outside the repo (Fartificer, UwUMod).
- Tag is an SDK-style project that also needs `..\..\Rain Meadow\mod\plugins\Rain Meadow.dll` (a sibling Rain Meadow checkout).
- Key references: `PUBLIC-Assembly-CSharp` (publicized game assembly), `HOOKS-Assembly-CSharp` (MonoMod `On.`/`IL.` hook types), BepInEx, MonoMod, UnityEngine modules. SweetDreams/LizardSkin ship `Newtonsoft.Json.dll`.

Each project has a post-build step that copies the built DLL into its own `Mod/plugins/`. **The built DLLs in `*/Mod/plugins/` are committed** — rebuilding changes tracked binaries, and a release means committing the updated DLL. Note several assembly names contain spaces (`SplitScreen Co-op.dll`, `Sweet Dreams.dll`, `Spawn Menu.dll`).

To test, copy/symlink a mod's `Mod/` folder into the game's `RainWorld_Data/StreamingAssets/mods/<name>` and enable it in the Remix menu. Logs go to BepInEx's `LogOutput.log`.

## Reading the game's code

The game is closed source; a decompiled copy lives **outside the repo** at `C:\Users\vshah\code\RainWorld-decompiled\src\` (`Assembly-CSharp\` one file per type, e.g. `RoomCamera.cs`, `Futile.cs`, `RoomRealizer.cs`; `Assembly-CSharp-firstpass\` is only ~85 `AG*` profiling/utility types — everything game-related, including `Futile`, is in `Assembly-CSharp` on this game version). Grep there to find hookable methods and vanilla behaviour. HookGen exposes private methods too, so `On.RoomCamera.ApplyPalette` and similar are valid. Regenerate after a game update with `ilspycmd` (installed as a dotnet global tool; run from PowerShell):

```powershell
$m = "C:\Program Files (x86)\Steam\steamapps\common\Rain World\RainWorld_Data\Managed"; & "$env:USERPROFILE\.dotnet\tools\ilspycmd.exe" -p -o "C:\Users\vshah\code\RainWorld-decompiled\src\Assembly-CSharp" --nested-directories -r "$m" "$m\Assembly-CSharp.dll"
```

(`dnSpy.Console.exe` in `C:\Users\vshah\Documents` crashes when run from a tool without a real console; use ilspycmd instead.)

## Mod layout convention

```
<Mod>/
  <Mod>.csproj, *.cs, Properties/AssemblyInfo.cs
  Mod/                    # the folder that ships to players
    modinfo.json          # id, version, target_game_version, authors, description (<LINE> = newline)
    thumbnail.png
    plugins/<Assembly>.dll
    (assets: atlases/, illustrations/, music/, soundeffects/, modify/, ...)
```

When bumping a version, keep `modinfo.json` `version` and the `[BepInPlugin(guid, name, version)]` attribute in sync (e.g. SplitScreenCoop is `0.2.2` in both). The `modinfo.json` `id` (e.g. `henpemaz_splitscreencoop`) is also the key passed to `MachineConnector.SetRegisteredOI`, and `target_game_version` should track the game version the mod was fixed for.

## Code patterns

- Entry point is a `BaseUnityPlugin` with `[BepInPlugin("com.henpemaz.<mod>", ...)]`. Files start with `[module: UnverifiableCode]` + `SecurityPermission(SkipVerification = true)` so the publicized game assembly's private members can be accessed.
- `OnEnable()` subscribes to `On.RainWorld.OnModsInit`; nearly all other hooks are applied inside `OnModsInit`, guarded by an `init` flag because `OnModsInit` can run more than once. Only hooks that must exist before mod init (e.g. SplitScreenCoop's `Futile.Init`, `FScreen`, `PersistentData.ctor`) go in `OnEnable`.
- Game modification is done entirely through MonoMod HookGen: `On.Type.Method += Handler` (call `orig(self, ...)` unless intentionally replacing) and `IL.Type.Method += ILHandler` using `ILCursor` for mid-method patches. Overloaded methods have mangled hook names like `On.RainWorldGame.SpawnPlayers_bool_bool_bool_bool_WorldCoordinate`.
- Remix config menus subclass `OptionInterface` (`SplitScreenCoopOptions`, `LizardSkinOI`, `LapModRemix`).
- Larger mods are split into `partial class` files by feature: `SplitScreenCoop.<Feature>.cs`, `LizardSkin.Hooks.cs`, `TagMod.Logging.cs`.

## Mod-specific architecture

**SplitScreenCoop** (most actively maintained): active development is on the **`dynamic-split-screen`** branch (all inside `SplitScreenCoop/`), which adds the Adaptive and Static split styles (`AdaptiveLayout.cs`, `SplitLayoutSolver.cs`, `SplitScreenCoop.Dynamic.cs`) on top of the classic fixed layouts. On that branch read `SplitScreenCoop/CLAUDE.md` (short) and `SplitScreenCoop/HANDOFF.md` (design reasoning, rejected approaches, log tags) before touching rendering or layout code; their build/dnSpy paths refer to a different machine (VS 2019, `D:\Steam`, user `20xha`) — use the paths above instead. New `.cs` files must be added to the `<Compile Include>` list in the csproj (not a glob). The mod runs up to 4 Unity cameras, each a `RoomCamera` rendered to its own region of the screen. Extra cameras are parked at large world offsets (`camOffsets`, multiples of 32000) so their sprites don't overlap; `curCamera` tracks which camera Futile is currently drawing for, and `CameraListener` components bind Unity cameras to `RoomCamera`s. Split layouts (`SplitMode`: vertical/horizontal/3-way triangle/4-way) and per-camera screen rects are static fields on `SplitScreenCoop`. Feature files: `Multicamera` (camera creation/switching), `Realizer2` (additional `RoomRealizer`s so remote players' rooms stay realized), `Coop`/`CoopFixes` (self-sufficient co-op: shared food, shelters, deaths, gates, HUD/pause/map fixes), `ShaderShenanigans` + `WatcherCompat` (per-camera level textures, ripple buffers and shader globals for the Watcher DLC), `DisplayExtras` (multi-monitor output), `CameraDiagnostics` (stalled render-target recovery). Optional integration with CoopLeash via `lib/CoopLeash.dll` and `ModManager.ActiveMods` id check. `SplitScreenCoop/Todo.txt` lists implemented features and outstanding in-game validation steps.

**LizardSkin**: lizard-style cosmetics for slugcats. `Cosmetics/Generic*.cs` are cosmetic implementations that draw against an `ICosmeticsAdaptor`; `PlayerGraphicsCosmeticsAdaptor` (in-game) and `MenuCosmeticsAdaptor` (Remix menu preview) adapt different hosts. Configurations are serialized with Newtonsoft.Json (`AbstractJsonConverter`, `LizKinConfiguration`) to `lizardskin_custom.json` in the config dir.

**SweetDreams**: dream illustrations/music keyed by slugcat × lizard type, data in `Mod/sweetdreams/main.json`. Source art lives in `Art/*.psd` and is exported to `Mod/illustrations/sweetdreams/` with `Art/export.py`, a Krita Python script (run inside Krita, not standalone).

**Tag**: a game mode for **Rain Meadow** (online multiplayer mod) — registers `TagGameMode` via `RainMeadow.OnlineGameMode.RegisterType` and syncs state through Meadow lobby data (`TagLobbyData`, `HunterData`).
