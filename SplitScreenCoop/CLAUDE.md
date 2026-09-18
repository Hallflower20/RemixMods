# CLAUDE.md — SplitScreen Co-op (Rain World mod)

BepInEx/MonoMod mod for Rain World that gives each player their own camera and composites
them into one dynamic, LEGO-style split screen. Branch of record: `dynamic-split-screen`.
Read `HANDOFF.md` before touching rendering or layout code; it explains why things are the
way they are and lists approaches that were tried and rejected. `Todo.txt` is the playtest
checklist. Keep both updated when you change behaviour.

## Build and test

Build (MSBuild, .NET Framework 4.8.1; pass the real Steam path, the csproj HintPaths point
at a drive that does not exist here):

```bash
"C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe" SplitScreenCoop.csproj /nologo /v:m /p:Configuration=Release /p:RainWorldPath="D:\Steam\steamapps\common\Rain World"
```

The post-build step copies the DLL to `Mod/plugins/SplitScreen Co-op.dll`; that folder is
what the game loads. New source files must be added to the `<Compile Include>` list in
`SplitScreenCoop.csproj` (it is not a glob).

Solver tests are Unity-free and run in seconds. Run them after any change to
`SplitLayoutSolver.cs`:

```bash
csc.exe /nologo /target:exe /out:Tests.exe SplitLayoutSolver.cs Tests\UnityMathStub.cs Tests\Program.cs && Tests.exe
```

(`csc.exe` is under the same MSBuild folder, `...\Bin\Roslyn\csc.exe`.) Expect
`PASS: <n> layout checks`. Add a test for every new solver rule.

## Repo map (what to open for what)

| File | Owns |
|---|---|
| `SplitLayoutSolver.cs` | Pure layout maths: fixed-slot rectangles by camera number, joins, slides, pans. No Unity. Positions never move a cell; only merges/parts/deaths restructure. |
| `SplitScreenCoop.Dynamic.cs` | Per-tick layout inputs, base camera choice, uv-shift pans, GL compositor, divider alpha, HUD routing |
| `SplitScreenCoop.cs` | Hook registration, split modes, game Update/GrafUpdate/ShutDown hooks, classic HUD offsets |
| `SplitScreenCoop.CameraDiagnostics.cs` | All logging tags, hang watchdog, Unity log capture, draw-stall detection, menu camera restore |
| `SplitScreenCoop.ShaderShenanigans.cs` | Per-camera shader globals, palettes, rot/ghost modes, room change |
| `SplitScreenCoop.RotSpores.cs`, `WatcherCompat.cs` | Vanilla objects that assumed one camera, made per-camera |
| `SplitScreenCoop.LevelTextures.cs` | Level PNG decode sharing between cameras |
| `SplitScreenCoop.Realizer2.cs` | One room realizer per player, shared budget |
| `SplitScreenCoop.Coop.cs`, `CoopFixes.cs` | Game-over rules, sleeping, food maths |

## Facts that keep biting

- **The game ticks at 40 Hz; rendering can be 240 fps.** Code in `RainWorldGame.Update`
  hooks must use the tick (`1f / game.framesPerSecond`), never `Time.deltaTime`. Per-frame
  code (`GrafUpdate`, `OnPostRender`) uses `Time.deltaTime`.
- **Every camera renders one prebaked 1400x800 screen.** All "following" is uv panning of
  that image; a cell cannot show more than its screen. See the uv-shift model in HANDOFF.
- **No alpha blending between camera images.** The user rejected crossfades. Merges are
  geometric: same image, pans converge, the divider line fades. Nothing may snap.
- **Cells never move with the players.** Slots are fixed by camera number; only a merge,
  a part, an arrival or a death restructures (as a slide). The users rejected the earlier
  position-driven layout (rotating divider, side swaps) as "constantly shifting around".
- **Vanilla assumes one camera.** Any `IDrawable` that keeps Unity objects per room object
  instead of per sprite leaser breaks when a second camera enters its room (rot spores,
  ripples, level combiner already fixed). A black screen with audio means something threw
  inside `RoomCamera.DrawUpdate`; look for `[UnityLog]` in the log.
- **Private game members** are reachable at compile time: the project references
  `PUBLIC-Assembly-CSharp.dll` from `BepInEx/utils`, not the stock assembly. The reflection
  in `RotSpores.cs` is defensive, not required.
- **After the game shuts down, exactly one camera may draw:** camera 0 into Futile's
  screen texture. `RestoreMenuCameras` does this; do not re-isolate masks at shutdown.
- **Static per-session state must be reset** in `RainWorldGame_ctor` and
  `RainWorldGame_ShutDownProcess` (level texture keys, safe mode, layout).
- Log tags and what they mean are tabulated in HANDOFF section 7. Playtest logs arrive as
  `C:\Users\20xha\Downloads\LogOutput.log`; the playtest machine's BepInEx does not write
  Unity's log, so the mod mirrors exceptions itself.

## Investigating game code

Decompile a type non-interactively with dnSpy (see the memory note for details):

```bash
"C:\Users\20xha\Documents\GitHub\dnSpy\dnSpyBinary\dnSpy.Console.exe" -o out --type RoomCamera "D:\Steam\steamapps\common\Rain World\RainWorld_Data\Managed\Assembly-CSharp.dll"
```

`Futile`, `FContainer`, `FGameObjectNode` live in `Assembly-CSharp-firstpass.dll`. HookGen
exposes private methods, so `On.RoomCamera.ApplyPalette` and similar are hookable.

## Working style the user expects

- This is a game mod; do the work asked, fully, and report test and build results plainly.
- Diagnose from the log before changing code; quote frame numbers and tags.
- When a request conflicts with an earlier design (fade vs. no fade, hold times), the
  latest instruction wins; record the change and the reason in HANDOFF.
- Do not commit or push unless asked.
