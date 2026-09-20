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
  screen texture, with its `CameraListener` direct and not compositing. `RestoreMenuCameras`
  does this; do not re-isolate masks at shutdown. A listener left in split mode copies
  its stale texture over the menu every frame (the "frozen screen after sleep").
- **Vanilla draws with `PausedDrawUpdate` while paused.** Anything that watches the draw
  loop must count those draws too.
- **The stutter players report at 240 fps is tick judder** (40 Hz tick on one frame in
  six). Read the `[Perf]` line: avg vs p95. Vanilla's FPS cap option is the remedy.
- **Drawables retire their leaser inside `DrawSprites`** (`if (slatedForDeletetion ||
  room != rCam.room) sLeaser.CleanSpritesAndRemove()`). Anything that skips `DrawSprites`
  for a camera must still call it in that case, or the camera leaks sprites for ever.
- **A named `GrabPass` happens once per frame, not once per camera** — and the Watcher
  shaders use seventeen of them. That is why Dynamic mode renders one world camera per
  frame (`SelectFrameCamera`, Auto/option). Before touching that, read HANDOFF §5 "Camera
  audit": it lists every per-camera input and how it is replicated.
- **A shader global written in `Room.Update` must not depend on a camera.** If vanilla
  does (`_tileCorrection`), recompute it per viewing camera. `[ShaderAudit]` in the log
  names every global written outside camera scope and the method that wrote it.
- **Flat lime/red (or any flat data colour) on screen is a raw Watcher mask mesh, not a
  palette or grab problem.** `MaskSource`s are Unity meshes on layer 0 whose shaders
  write data; only a camera that also has the layer's grab quad may see them. Vanilla
  moves them three ways (`DrawUpdate`, the `setGameObject*` setters directly, or not at
  all), so placement is recorded from the setters and from the sprite leaser, never
  from one call site. Any camera that draws layer 0 without world content (the overlay
  camera) must refuse them (`HideMaskSourcesFromOverlay`, `[MaskAudit]`). An artifact
  that crosses the split divider unclipped is drawn by the overlay camera: look there
  first. HANDOFF §5 "Fifteenth log" has the three rounds this cost.
- **Vanilla follows the Watcher with every camera in the ripple layer**
  (`coopRippleDimensionPlayer`). `RoomCamera_Update` clears it before `orig` when each
  player has a camera, or the cameras fight `EnsureStableCameraAssignments` every tick.
- **Remix draws elements in add order** and a combo box's list belongs to its box: make
  boxes with `Combo(...)` and add them last, lowest first.
- **A shader that reads `_GrabTexture` without its own `GrabPass` sees the last grab of
  any camera - but only if it draws before its own camera's first grab.** Futile sets
  every sprite layer's queue itself (`3000 + depth`), so a shader's `Queue` tag means
  nothing: draw order is container order. (An earlier note here blamed the lime/red
  artifact on `DisplaySnowShader`; it was never the snow sprite, see the rule above.)
  Each camera binds its previous frame as `_GrabTexture` in
  `OnPreRender` (`BindOwnGrab`). HANDOFF §5 "Thirteenth log" has the audit of all 306
  shaders; `Tools/grabaudit.py` and `Tools/shaderdisasm2.py` re-run it (pip install UnityPy).
- **Taking turns costs every view its share of the frames.** Under the game's 60 fps
  limit two views got 30 each, which players report as lag although `[Perf]` is flat.
  `ApplyRotationFrameCap` raises `Application.targetFrameRate` by the number of views
  for as long as the rotation runs and puts the old value back. Auto judges only
  frames in which the rotation ran (median, per view, 38 fps) and retries with a
  back-off; it must not flap on merged states or on a load in the window. Check
  `[Perf] fpsPerView= frameLimit= vsync=`.
- **Whoever stops the rotation hands the cameras back.** It leaves all but one camera
  disabled; only the dynamic pipeline re-enables them by itself. A frozen second
  display, or a pause menu missing on one display, is this.
- **A HUD part that places sprites inside `Draw` cannot be moved by shifting those
  sprites around `orig`** (`HypothermiaMeter`): shift the part's own `pos`/`lastPos`.
  Read the vanilla `Draw` before writing such a hook.
- **Three split styles.** Dynamic (merge and part), Static (the same pipeline with
  `NeverMerge`: a fixed region per *living* player, reflowing only on death or revival,
  no shelter collapse) and Classic (the original non-isolated layouts). Read
  `NeverMerge`, not `alwaysSplit`, anywhere merging is decided.
- **Futile's stage list order is draw order** (render queue 3000 + depth, handed out stage
  by stage). Harmless while every stage has its own Unity camera; the moment one camera
  draws several stages (dual displays: world + HUD + root) they must be listed bottom to
  top. The list is world 0-3, HUD 0-3, global HUD, root; startup logs it.
- **Dual displays use the per-camera stages too.** On the root stage every Unity camera
  draws every RoomCamera's sprites: Futile meshes have 1e10 bounds and are never culled.
- **Anything moved to another container must be moved in vanilla's creation order.**
  `RouteNode` appends, so call order is draw order. The Watcher gives every food pip a
  black `backCircle` that is created first and belongs underneath; routed last it hid the
  white fill and every pip looked gray for the whole session. When routing a new HUD part,
  read its constructor for the `AddChild` order first.
- **Vanilla precasts warps for `cameras[0]` only.** Cameras 1–3 need the `MoveCamera`
  fallback in `RoomCamera_WarpMoveCameraActual` or they never arrive.
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
