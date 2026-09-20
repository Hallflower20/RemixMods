# SplitScreen Co-op — developer handoff

Updated 2026-09-16 (after the sixth three-player playtest: rot-spore crash fixed, layout
holds across rooms). Covers the `dynamic-split-screen` branch at commit `4b71edc` plus the
uncommitted tree. `CLAUDE.md` is the short version for a new session; this file is the
reasoning. Read
"Build", "How the dynamic pipeline renders" and "Invariants" before touching rendering
code; nearly every regression so far came from violating one of those, not from geometry.

---

## 1. Build

The .NET Framework **4.8.1** Developer Pack is installed and both project files target
`v4.8.1`, so MSBuild and Visual Studio build normally:

```bash
"/c/Program Files (x86)/Microsoft Visual Studio/2019/Community/MSBuild/Current/Bin/MSBuild.exe" SplitScreenCoop.csproj /p:Configuration=Release "/p:RainWorldPath=D:\Steam\steamapps\common\Rain World"
```

`RainWorldPath` matters: the csproj's hard-coded HintPaths point at `C:\Program Files
(x86)\Steam`, which does not exist on this machine; `AssemblySearchPaths` resolves every
reference from `$(RainWorldPath)` instead. The post-build step copies the output over
`Mod/plugins/SplitScreen Co-op.dll`.

Two publicized-assembly facts still apply: `BepInEx\utils\PUBLIC-Assembly-CSharp.dll` is
the referenced `Assembly-CSharp` (never also reference the private one in `Managed`), and
private game members (`RoomCamera.paletteTexture`, `RoomRealizer.realizedRooms`,
`RoomRealizer.performanceBudget`) are legal at runtime because
`BepInEx\patchers\Dragons.PublicDragon.dll` publicizes on load.

### Tests

`Tests/` is a plain console program over the solver only — no Unity, no game. It stubs
Unity math in `Tests/UnityMathStub.cs`.

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\Roslyn\csc.exe" /target:exe /out:Tests.exe SplitLayoutSolver.cs Tests\UnityMathStub.cs Tests\Program.cs; .\Tests.exe
```

Expect `PASS: 10328 layout checks` (the pan-budget test samples an 11x11 grid per cell,
which is where most of that count comes from). `SplitLayoutSolver.cs` is deliberately
pure and Unity-free so it stays testable — **keep it that way.** Anything needing a
`RoomCamera`, a `Room` or a `RenderTexture` belongs in `SplitScreenCoop.Dynamic.cs`.

### Verifying a built DLL without launching the game

```powershell
Add-Type -Path "D:\Steam\steamapps\common\Rain World\RainWorld_Data\Managed\Mono.Cecil.dll"
$m=[Mono.Cecil.ModuleDefinition]::ReadModule("...\Mod\plugins\SplitScreen Co-op.dll")
$m.Assembly.FullName; $m.AssemblyReferences | % { $_.Name }
$m.GetMemberReferences() | ? { $_.Name -like "add_*" } | % { $_.DeclaringType.FullName + "::" + $_.Name }
```

Useful for confirming a new `On.X.Y` / `IL.X.Y` hook actually resolved and that no stray
reference crept in.

---

## 2. Repo map

| File | What it owns |
|---|---|
| `SplitScreenCoop.cs` | Hook registration, Futile camera creation, `SetSplitMode`, classic split layouts, camera↔player ownership |
| `SplitScreenCoop.Dynamic.cs` | The dynamic pipeline: layers, stages, HUD routing, compositor, per-frame view panning |
| `SplitLayoutSolver.cs` | **Pure** rectangle layout solver with hysteresis. Unity-free, unit-tested |
| `SplitScreenCoop.Multicamera.cs` | Pause menu, water, culling, shortcut IL hooks, `RoomCamera.Update` IL |
| `SplitScreenCoop.LevelTextures.cs` | Level-image sharing between cameras (skips redundant PNG decodes) |
| `SplitScreenCoop.Realizer2.cs` | Per-player `RoomRealizer` instances, their guards and the shared budget |
| `SplitScreenCoop.CameraDiagnostics.cs` | Logging, stall detection, room resync, `[FrameHitch]` |
| `SplitScreenCoop.ShaderShenanigans.cs` | Per-camera shader global capture and replay |
| `SplitScreenCoop.Coop.cs` | Shared food, karma, shelter/gate/game-over rules |
| `SplitScreenCoop.WatcherCompat.cs` | Watcher ripple/warp/level-combiner routing |
| `SplitScreenCoop.CoopFixes.cs` | Misc vanilla co-op fixes |
| `SplitScreenCoop.CameraListener.cs` | `MonoBehaviour` on each Unity camera: render targets, pre/post render |
| `SplitScreenCoopOptions.cs` | Remix config |

`Todo.txt` tracks feature/bug status. Keep it current — it is the other half of this
document.

---

## 3. How the dynamic pipeline actually renders

### The one fact everything else follows from

**A `RoomCamera` does not follow the player.** Vanilla `RoomCamera.Update` lerps `pos`
toward the prebaked screen origin and clamps it to ±20 px horizontally and a 32 px band
vertically (`RoomCamera.Update`, the `Mathf.Clamp` lines after `seekPos`). The render
texture of each camera is therefore always one prebaked room screen, give or take a lean.
All "following" in Dynamic style is the compositor translating uvs inside that one image.
A cell of normalized width `w` drawn at zoom `z` samples a window `w/z` wide, so it can
pan by exactly `1 - w/z`. That budget is the whole design constraint.

Classic style gets the same budget a different way (it widens the vanilla clamp by a
quarter screen and crops the render); do not mix the two mechanisms.

### Layer and stage isolation

`InitHudRenderLayers` claims **9 unused Unity layers**, scanning from 31 down to 8:
4 HUD layers, 1 global HUD layer, 4 world layers. A real session logs e.g.
`isolated world layers=[26,25,24,23]; HUD layers=[31,30,29,28]; global HUD layer=27`.
If fewer than 9 are free the mod logs an error and falls back to Classic.

Each `RoomCamera` gets:

- its sprite layers (everything except the `HUD` and `HUD2` containers) moved onto
  `worldStages[n]`, rendered only by `fcameras[n]` via `cullingMask = 1 << worldLayers[n]`
- its `HUD` / `HUD2` containers moved onto `hudStages[n]`, rendered by `hudCameras[n]`
  into `hudTextures[n]`

The four `RoomCamera`s are additionally separated in world space by `camOffsets`
(`(0,0)`, `(32000,0)`, `(0,32000)`, `(32000,32000)`) with each Unity camera translated to
match. Layers and offsets are belt-and-braces; both are load-bearing.

### Render order (Unity camera depth)

| Depth | Camera | Target |
|---|---|---|
| 100–103 | `fcameras[0..3]` — world | `cameraListeners[n].renderTexture` |
| 150–153 | `hudCameras[0..3]` | `hudTextures[n]` |
| 200 | `dynamicCompositorCamera` (`cullingMask = 0`) | display RT |
| 220 | `globalHudCamera` | display RT, `clearFlags = Nothing` |

The compositor camera draws nothing itself; `DynamicCompositor.OnPostRender` calls
`CompositeDynamicLayout`, which issues immediate-mode `GL` polygons. Order inside it:

1. `GL.Clear` to black
2. full-screen backdrop, only when a ghost exists or the layout is fully merged
3. per-viewport world polygons
4. `DrawDynamicDividers`
5. `DrawDynamicHud`
6. debug outlines

**Dividers must stay above the world and below the HUD.** They were drawn last once and
painted black over pause-menu buttons.

The global HUD camera runs *after* the compositor, so anything on `globalHudStage` lands
on top at native screen coordinates, unclipped and untranslated. That stage holds the
shared meters, the single pause menu, one camera's `TextPrompt` (the letterbox bars and
hint text), and — via the culling mask — Futile's root stage.

### Layout: rectangles, decided by a small tree

`SplitLayoutSolver` builds a guillotine partition of the screen from **fixed slots**.
World positions play no part in where a cell sits (they only decide merging, see below,
and the pan inside a cell). Items — a single player or a merged group — are ordered by
their lowest camera number:

- **Two items**: side by side, first item left (stacked, first on top, only inside a
  box that is taller than wide on screen, i.e. a pair tiling a half-width column).
  The cut follows the weights: two singles are halves, a pair beside a single owns two
  thirds.
- **Three items**: the item with the most members (a merged pair) takes the top half;
  with three singles camera 0 does. The other two take the bottom, left to right by
  number. The top item is weighed against the *largest* of the others, so three
  players give one half and two quarters.
- **Four items**: a 2×2 grid, cameras 0 1 / 2 3.

This replaced (2026-09-16) a position-driven tree: a two-player divider that rotated
continuously with the players' on-screen direction, side swaps and axis flips with
dead zones and hold times, and a three-player "peel off the most isolated player"
choice. The players' verdict was that the screens "constantly shift around depending
on positions" and that only two things should ever change the screen: a split when
two players get far enough apart and a merge when they get close. Every dead zone,
hold time and score is gone with it (`directionDeadZone`, `layoutHoldSeconds`,
`dividerTurnSeconds`, `SwitchMargin`, `ChooseAxis`, `ChooseSplitOff`,
`SplitTwoRotating`, `CutForArea`, `Clip`); `Tests/Program.cs` `PositionsNeverRestructure`
pins the new contract: any motion, including crossing sides, circling and changing
rooms, leaves every rectangle exactly where it was and never sets `restructured`.

`NodeMemory` (keyed by the bitmask of cameras in the node) now only holds the damped
cut fraction and the `layoutKey` (the item masks in slot order). While the key is
unchanged the cut `SmoothDamp`s with the live weights, so a dying player's cell slides
shut; when the key changes the cut snaps to its target and the slide below is the only
animation. (Damping across a key change was tried first: the third player's cell then
passed through a half on its way from a quarter to a third because the old fraction
belonged to a cut on the other axis.)

**Nothing fades.** Merging is geometric, like a LEGO split screen. Two players on one
prebaked screen share a base camera (`baseCameraNumbers`), every cell draws that
camera's image opaque, and each cell's pan target is
`lerp(identity, centre-my-player, splitAmount)`. At split 0 both cells sample the same
image with the same pan, so the seam does not exist and the divider (alpha = split) is
gone; as the players separate the pans diverge and the divider returns. The per-pair
split amount is `SmoothDamp`ed (`splitSmoothingTime`, 0.25 s) because a player arriving
on the other's screen through a pipe changes the pair's screen key in one tick. Do not
reintroduce an alpha blend between two camera images; that was the "fading" the users
rejected.

**When a cell may adopt another camera's image** is a one-way hysteresis in the
`sharedCandidate` loop of `UpdateDynamicLayout`. Starting to share requires the player's
*own* camera to sit on the base camera's screen (same room and camera position): that is
the instant vanilla cuts, and the two images are the same picture, so nothing visible
changes except the line beginning to fade. Adopting earlier, as soon as the player was
merely visible near the edge of the other screen, replaced the cell's content with a
different screen and then panned it into place, which the playtest described as "swaps
to the other camera, then swipes". Once sharing (`lastBaseByCamera`), a cell keeps
sharing while its player stays visible on the base screen, so a camera switching screens
at the edge does not break a merged view apart prematurely.

Structural changes with three or four cells (a pair joining or parting, a player
arriving or a dead player's cell being removed) are animated, never cut: the tick the
layout key of any node changes starts a **slide** of `transitionSeconds` (0.45 s): each
cell's `polygon` interpolates from its previous rectangle to `targetPolygon`;
`Layout.sliding` is true meanwhile and `Layout.restructured` on the first tick (logged
as `[CameraLayout] … restructured`). Sliding rectangles overlap and leave gaps, so the
compositor first draws every `targetPolygon` (the resting layout) and then the sliding
`polygon`s over it, all opaque. Two-cell layouts never slide: two players joining or
parting keeps both rectangles where they are, only the pans and the line change.

**Divider opacity is how far apart the two views are, damped.** `DividerTargetAlpha` in
`Dynamic.cs` computes where each cell's image sits in world pixels (base camera position,
interpolated and clamped like `RoomCamera.DrawUpdate`, plus that cell's pan × screen
size). When the two origins coincide the picture is continuous across the line, i.e. one
camera already frames both players and there is nothing to indicate, so the line is 0.
It ramps to solid over `DividerInvisibleBelowPixels` (3) to `DividerOpaqueAbovePixels`
(32). It used to be 4→300 px so the fade spanned the whole glide, but that left the line
nearly transparent while the seam was still offset by 30–80 px (the follow slack between
two cameras on one screen, or a merging pair's pan difference), and the exposed seam read
as the picture *shearing*. The line now covers any visible offset and drops over the last
32 px; `DividerAlpha` damps it per camera pair (`DividerFadeSeconds`, 0.25 s), so the
drop is still a fade and a camera cut, which moves one view by a screen in one frame,
fades the line in rather than popping it. Different rooms are solid.

Two related seams: a cell that switches base camera on the same screen carries its shift
over by the two cameras' position difference (`ownViewBaseNumbers/Positions` in
`RefreshDynamicViewShifts`) so the displayed world does not jolt by the slack; and a base
switch to a camera that has not rendered yet redraws last frame's image
(`lastDrawnListeners`) instead of leaving the cell black for a frame.
The solver still computes a distance-based `DividerSegment.alpha` (`PairMemory.line`,
`lineSmoothingTime`); the compositor only uses it as a fallback when alignment cannot be
computed. Two rejected variants, so nobody re-tries them: distance alone hid the line
while the views were still visibly apart; and an earlier narrow 64 px ramp *before the
pans were damped* never crossed its band, because alignment then happened in one frame.
With the shift damped over 0.12 s and the split over 0.5 s, the narrow band is crossed
gradually, which is why 3→32 px works now.

**Why a merge across a screen boundary cannot be fully continuous, and what is done
instead.** A half-width cell is 683 px wide; adjacent prebaked screens in a wide room
overlap by less than that. A cell keeping its player centred therefore has to change
which screen it samples somewhere, and at that point its content must jump by (cell
width − overlap). Vanilla makes that jump too (the whole view cuts); here only one half
does. The merge pan is then a *glide*: `splitSmoothingTime` is 0.5 s, so when the cut
puts the pair inside the merge band the view moves to its merged position over half a
second with the line fading alongside, instead of the 0.25 s move that read as a snap.
A merge that does not cross a boundary is driven by distance and needs no glide.

Players that are joined (same prebaked screen, within `mergeDistance`) form one group,
and their individual cells just tile the group rectangle — they draw one image.

### The uv-shift model

Every polygon is in normalized display space. `DrawVertex` computes
`uv = position / zoom + uvShift`, so a texture point `u` appears at display point
`(u - uvShift) * zoom`. `SplitLayoutSolver.ClampedUvShift` gives the shift that puts the
player at an anchor and clamps it so the cell's rectangle never samples outside the source.
For a rectangle that clamp is exact, and the tests prove that any source position can be
shown inside its own cell (`PanBudget` in `Tests/Program.cs`).

One shift exists per camera, `ownViewShifts[n]`, recomputed **every rendered frame** in
`RefreshDynamicViewShifts` (hooked from `RainWorldGame.GrafUpdate`). Player positions
are measured against the cell's *base* camera and interpolated with the same
`timeStacker` the sprites used. The pan has two stages, and the solver
(`ViewportState.groupSplit` / `innerSplit`) and the compositor compute the same formula:

1. A **joined group** pans as one image: anchor = lerp(group's live average position,
   group box centre, `groupSplit`) plus each member's offset from that average, scaled by
   zoom. `groupSplit` is the group's largest split against players *outside* it. Every
   member's shift is identical, so at split 0 cells on one screen line up exactly.
2. Each cell then blends from that group anchor to its **own centroid** by `innerSplit`,
   its largest split against its own group-mates.

A pair joins (becomes one item of the layout tree) below split 0.02 and parts only above
0.95, i.e. when stage 2 is nearly complete, so joining and parting restructure the tree
but never pop the pan. The anchor is then clamped (`PanBounds`, `ViewportState.anchorMin/
Max`) into the cell's window shrunk by `VisibleMargin` × split, and the source-window
clamp (`windowMin/Max`) blends from the group's box to the cell's own box with
`innerSplit`. That is the "always visible in your own cell" guarantee: at split 0 the
margin is zero so seamless cells stay exactly aligned; once the image has parted, the
player is inside their cell by at least the margin. The result is `SmoothDamp`ed over
0.12 s and snaps only when the image underneath changed: the base camera's room or camera
position, or the cell moving more than 0.08 across the screen. Only base cameras have
their Unity camera enabled.

**Timing.** `UpdateDynamicLayout` runs once per *game tick* (40 Hz), not per rendered
frame, so the solver gets `1 / game.framesPerSecond` as dt, never `Time.deltaTime`. Per
frame code (`RefreshDynamicViewShifts`, `DividerAlpha`) does use `Time.deltaTime`.

HUD polygons use `DynamicHudShift` → `HudUvShift`, which centres the HUD's native screen
centre on the cell centroid and clamps to avoid edge-clamp smear.

**Consequence worth internalising:** anything positioned against the composited image
must invert the same shift. A HUD element at native position `p` is *seen* at
`p/screenSize - shift`. Placing UI by cell centroid alone gives you something that looks
right but sits somewhere else in Futile space — which is exactly why the pause menu was
once unclickable.

### Zoom

`zoomTarget = clamp(groupArea^ViewZoomExponent, MinZoom, 1)`, then raised to at least the
group rectangle's larger side so the window fits inside the source. The Remix default is
now `ViewZoomExponent = 0` (native scale everywhere, pan to follow). Zooming out shrinks
the pan budget; at `zoom == cell extent` the cell shows the whole screen and cannot follow
at all. The key was renamed from `ZoomExponent` because saved configs held the old 0.5.

---

## 4. Invariants that are easy to break

1. **`SplitLayoutSolver.cs` stays Unity-free.** It is the only unit-tested part.
2. **Cells are axis-aligned rectangles.** See §3; a non-rectangular cell has no pan and
   needs out-of-texture hacks to show its player.
3. **Dividers under the HUD**, per above.
4. **Anything positioned against the composite must use `DynamicHudShift`.**
5. **Nothing may be left on `Futile.stage` and assumed visible.** Isolating world layers
   left the root stage rendered by no camera. `globalHudCamera` includes
   `Futile.stage.layer` in its culling mask specifically so that root-stage content
   (`Menu.MouseCursor.BumToFront` parents the cursor there; ProcessManager's fade sprite
   lives there) still draws. If you change that mask, re-test the pause cursor and Exit.
6. **Shader globals must be attributed to a camera.** See §5.
7. **Never let one player's `RoomRealizer` abstractize a room another player or another
   realizer needs.** See §5.
8. **Only `ApplyDynamicCameraRendering` isolates world culling masks.** `SetSplitMode`
   used to do it too, which re-isolated camera 0 during shutdown right after
   `ResetDynamicLayout` restored `initialWorldCullingMasks`, and the main menu rendered
   black after Exit.
9. **Do not steer `RoomCamera.pos` in Dynamic style.** The vanilla clamp wins every tick
   anyway; the old follow code only made `lastPos`/`pos` inconsistent.
10. **Texel snapping**: `DrawDynamicPolygon` rounds `uvShift` to whole source texels when
    `zoom > 0.999`. Without it a fractional pan blends two texels per pixel and the view
    visibly softens.
11. **`levelTexture` is a property.** The IL hook in `LevelTextures.cs` matches
    `get_levelTexture`; matching a field silently fails and every camera decodes again.

---

## 5. Fixes and *why* — do not re-derive these

### This pass (uncommitted)

**Centering, juts, jumps** — all one bug. The Voronoi/power-cell solver produced cells
whose bounding boxes touched opposite screen edges, so `zoom` pinned to ~1 and the legal
pan collapsed to a point; the followed slugcat could sit on the far side of its own
divider. `WorldUvShift` compensated with a 0.35 out-of-texture "visibility slack" that
(a) smeared the clamped edge row across the cell — the juts — and (b) switched between
0.35 and 0.08 whenever the player crossed the polygon edge — the jumps. The log from the
playtest is full of `player visibility correction` lines. The solver now produces
rectangles (§3), the slack is deleted, and the pan is smoothed per rendered frame.

**Black screen after Exit** — invariant 8. `RainWorldGame_ShutDownProcess` calls
`ResetDynamicLayout()` (restores masks) then `SetSplitMode(NoSplit)`, which re-applied
`cullingMask = 1 << worldLayers[i]` because `dynamicStyle` was still true. Camera 0 then
rendered only an empty isolated layer while the main menu sat on `Futile.stage`.

**Stutter, part 1: level PNG decodes.** `RoomCamera.MoveCamera` → `MoveCamera2` reads the
level PNG and `ApplyPositionChange` decodes it with `Texture2D.LoadImage` on the main
thread, per camera. Three cameras following through one pipe decoded the same file three
times in one frame. `LevelTextures.cs` keys each camera's loaded image by
`RoomNameManipulator(fileName) + CameraTextureSuffixManipulator(...)` and copies raw
pixels (`GetRawTextureData` → `LoadRawTextureData` → `Apply`) from a sibling that already
holds that image. Both CPU and GPU copies are updated because
`RoomCamera.PixelColorAtCoordinate` and friends read the texture on the CPU. Format,
size and mip count must match or it falls back to decoding.

**Stutter, part 2: realizers fighting.** `RoomRealizer.RemoveNotVisitedRooms` (run on every
room change) and the perf-shaving paths only consult the realizer's own trackers. With one
realizer per player, B routinely abstractized the neighbour rooms A had just realized, and
A realized them again next tick — a full `Room` load each time. `RoomRealizer_KillRoom`
now also refuses when another realizer's `CanAbstractizeRoom` says no for that room.
Separately, `CurrentPerformanceEstimation` summed only the realizer's own rooms, so N
realizers each spent a full vanilla budget: it now returns the estimate over
`world.activeRooms`, and `MakeRealizer2` sets every realizer's budget to
`1500 + 750 × (players − 1)`. Watch `[RoomRealizer] refused to abstractize` — a few are
right, a stream means rooms are being pinned.

**Stutter, part 3: no data.** The playtest log had no timing. `RainWorldGame.GrafUpdate`
now logs `[FrameHitch] frame=… ms=… gcCollections=… events=[…]` for any frame over 50 ms,
with `realize`/`abstractize` room, `MoveCamera`, and `level texture decode/copied` events
collected via `NoteFrameEvent`. Rate-limited to one line per 30 frames with a suppressed
count. **Read these first from the next log.**

**Per-tick garbage.** The layout log line was built with three `string.Join`s every tick;
it is now behind a numeric signature. Two `Array.Exists` closures per camera per tick are
loops. `EnsureStableCameraAssignments` ran twice per tick; once now.

**Grey food pips.** Futile draws children in insertion order and `RouteNode` appends.
`RouteGlobalMeters` routed the meters first and the `TextPrompt` last, so the prompt's
semi-transparent letterbox bar (`sprites[1]`, whose height follows the food meter) sat
*over* the pips. Vanilla adds the TextPrompt to `fContainers[1]` before any meter. The
prompt is routed first now. Keep it that way; the same order bug would grey the karma
symbol and rain circles.

**Palettes differing near echoes / rot ("red goop looks black").** Two vanilla
single-camera assumptions. `Room.UpdateSentientRotEffect` calls
`cameras[0].UpdateRotMode(room, amount)` for *every* viewed room, so camera 0 was handed
other rooms' rot amounts every tick; and `UpdateRotMode` itself always writes the rot
effect colours into `cameras[0]`'s palette textures (`ApplyEffectColorsToAllPaletteTextures`),
so a second camera showing the rot never got them. `RoomCamera_UpdateRotMode` now
re-implements the method per camera: only cameras whose `room` is the given room take
the update, and each writes its own palettes. `UpdateGhostMode` is mirrored the same
way. The heartbeat `[CameraState]` line carries `palA/palB/blend/ghost/dark/fog/dayNight`
per camera.

**Buildings / city backdrop wrong on one camera.** `RoofTopView`, `AboveCloudsView` and
their Watcher relatives write per-room globals from their constructors during
`Room.Loaded`, and `_SceneOrigoPosition` is written *only* there. No camera shows a room
while it loads, so those writes had no recipient and the last room to load won for
everyone. `Room.Loaded` is now scoped like `Room.Update`, every room-scoped write also
goes into a per-room `RoomShaderState` (a `ConditionalWeakTable`), and
`RoomCamera_ChangeRoom` copies that record into the arriving camera's listener.

**Dead players' point of view audible.** A camera outside the layout kept its
`VirtualMicrophone` at full volume. `VirtualMicrophone_DrawUpdate` zeroes every volume
group for a camera whose followed player is dead (independent of layout state, so it
holds through transitions and game over) and for a camera with no live cell;
`VirtualMicrophone.Update` rebuilds the groups each tick so this is per frame.

**Game over with a player still alive.** Two faults. `IsCreatureDead` returned true for
a living player whose realized body was slated for deletion, which is what happens to
a player abstracted mid-pipe when the next room is not yet realized; that dropped their
camera from the layout and satisfied the "everyone dead or held" test when the other
player was grabbed or died. Death is now the creature state only (`state.dead`,
`permaDead`). Separately, vanilla raises game over from several `Player` paths and the IL
guard in `RainWorldGame.GameOver` is the only thing deferring them to the co-op rule;
`TextPrompt_EnterGameOverMode` now refuses to enter game-over mode unless
`AllPlayersDown` holds, whichever path called it, and logs `[Coop] blocked game-over
prompt` with each player's state. `UpdateCoopGameover` uses the same predicate.

**Shared meters only revealed for their owner.** `HUD.Update` computes
`showKarmaFoodRain` from its own owner's map button. `HUD_Update` sets it on the meter
source's HUD when any living player holds the map or has `showKarmaFoodRainTime`.

**Black screen after Exit / death (second report).** Root cause still not proven, so
there is now a watchdog: `EnforceMenuCameraState` runs every frame from the compositor
holder's `LateUpdate` and after `ProcessManager.PostSwitchMainProcess`. While the main
loop is not a `RainWorldGame` it forces camera 0 enabled, targeting
`Futile.screen.renderTexture`, with `Futile.stage.layer` in its mask, and disables every
other camera. Each correction is logged as `[MenuCamera]`, along with a snapshot per
process switch (`fadeToBlack`, `blackDelay`, stage child count, whether the RawImage
still shows the screen texture). Read those lines first if it recurs.

**Game-over prompt.** The game raises game over on `cameras[0].hud.textPrompt`. If camera
0's player had died first, `globalMeterSource` (and so the prompt slot) belonged to
another camera and camera 0's prompt nodes were parked out of any container. The prompt
slot now goes to any prompt in `gameOverMode` or `pausedMode` first. `[Coop]` lines log
the game-over decision, which camera's prompt entered game-over mode, and
`GoToDeathScreen`.

**Three-player "indecisive" sliding.** Three causes, all in the join/structure logic.
(a) A pair joined at 850 px and parted 30 px further out, and every join or part
restructured the three-player tree into a slide; two players hovering around the merge
distance slid the whole layout back and forth. Join/part is now 0.02/0.95 of the split
amount (see the uv-shift model), the merge distance is 600 px and the blend width 250 px
(`SplitScreenCoopOptions` migrates the old 850/300 the same way it migrated 280/200).
(b) The split-off and axis choices could flip every 0.75 s on a 0.25 score margin;
`layoutHoldSeconds` is 2 s and `SwitchMargin` 0.35. (c) A player in a pipe changed
screen key twice per transit (pending room, then the new screen). `UpdateDynamicLayout`
holds the key a player had on entering a shortcut (`heldScreenKeys`) and, when *every*
player is in a shortcut, skips the solve and keeps the last layout. Every restructure now
logs `[CameraLayout] frame=… restructured; cells=[…]`, which is the count to watch.

**Everything six times too slow ("constantly shuffled", "indecisive").** The third
three-player log had `restructured` lines exactly 6 frames apart: the game renders at
240 fps and ticks at 40 Hz, and the solver was fed `Time.deltaTime` (1/240 s) once per
tick. Every damping time, hold time, ghost fade and slide therefore ran six times slower
than configured; slides took ~3 s and the "moved more than 0.04" restructure test fired
on every tick of a slowly settling cut, restarting the slide each time. Fixed by using the
tick length as dt and by detecting restructures *structurally* (`structureChanged`: a node
changed axis, side or split-off item, was created, or changed item count) rather than by
box motion. Nodes unused for 2 s are dropped (`NodeMemorySeconds`), because a spawn-in
ghost fade had left the {1,2} sub-box a sliver and the pair revived at that size when the
players left the shelter 80 s later.

**Held screen keys and the all-in-pipes freeze were reverted.** Holding a pipe
traveller's old key meant no camera rendered that screen once the cameras moved on, so
their cells fell back to their own disabled cameras with stale textures and the sources
flickered `[0,0,0]→[0,1,1]→[0,1,0]`. Pending-room keys already keep players in one pipe
together as one group. The freeze skipped the solve while a transient (the sliver above)
was on screen and froze it there for the whole transit. Neither is needed once timing is
right.

**Level texture keys leaked across sessions.** `loadedLevelTextureKeys` is static and was
never cleared. On the next session (continue after death) a camera loading the respawn
shelter's screen could copy blank pixels from a sibling whose key matched from the
previous game. Cleared in the game constructor and shutdown (`ForgetLevelTextures`). This
is the best candidate found for "the screen after the death screen broke again".

**Shutdown leaves cameras menu-ready.** `RainWorldGame_ShutDownProcess` now ends with
`RestoreMenuCameras`, the same correction the menu watchdog applies, instead of leaving
camera 0 disabled and pointed at its split texture for the watchdog to fix a frame later.
When a menu's root stage has more than 40 children the watchdog logs a histogram of their
types (`stage children by type: …`) so the ~500-child leak can be named.

**Black screen with audio, no menus (fifth log).** Not a render-target problem: every
camera kept rendering and compositing (`postRenderAge=1`, `compositeAge=1`) and no
`[CameraRenderTarget] rebuilt` line means no resolution change. What the heartbeats do
show is `roomDrawAge` climbing from frame 16904 (140, 176, 756) while `roomUpdateAge`
stayed 0. `RainWorldGame.GrafUpdate` only skips the `DrawUpdate` loop when paused or
`!processActive`, and both of those also skip `RoomCamera.Update`, so the loop *was*
entered and camera 0's `DrawUpdate` threw every frame from then on: game logic and audio
continue (`RawUpdate` runs `Update` before `GrafUpdate`), nothing is ever drawn again, and
a pause menu opened afterwards is never drawn either. The throw itself is not in the log
because the playtest machine's `BepInEx.cfg` has `WriteUnityLog = false`. Three changes:
`HookUnityLog` mirrors Unity exceptions/errors into this log as `[UnityLog]` (rate limited
per message); `DetectDrawStall` logs `[CameraHealth] draw loop stalled` with the last
Unity error and flips `drawPathSafeMode`, which turns the mod's code inside the draw loop
(shader capture, `OffsetHud`, `RouteGlobalMeters`, level texture copying) into pass-through
so a mod-caused stall self-heals and a persisting one is proven to be vanilla or another
mod; and the mod's own after-`orig` code in `RainWorldGame_Update`, `RainWorldGame_GrafUpdate`,
`RoomCamera_DrawUpdate` and `RoomCamera_Update` is wrapped in try/catch (`[HookError]`) so it
can never take the frame down. Context at 16904: three players in CC, camera 1 had just
`MoveCamera(room)`d into CC_B06 where camera 0 already was, cell 1's base switched from
camera 2 to camera 0, and camera 2 had just been re-enabled. Candidates inside camera 0's
`DrawUpdate`, in order: `hud.Draw` (HUD hooks, Jolly off-room IL hook, map IL hook, nodes
the mod routes out of their containers), `spriteLeasers[i].Update` (CustomDecal hooks),
then the level graphic. Ask for `Player.log`
(`%USERPROFILE%\AppData\LocalLow\Videocult\Rain World\Player.log`) from the playtest
machine, or set `WriteUnityLog = true`; with this build the BepInEx log carries it anyway.

**Black screen: found and fixed (sixth log).** With Unity errors mirrored, the throw was
`SentientRotSpores.DrawSprites` (the Watcher rot particle cloud, present in CC_B06):
`ArgumentOutOfRangeException` from `GetChildAt(0)`. The object keeps ONE mesh
GameObject and its `InitiateSprites` disposes that node before creating a new one, so
when a second camera enters the room the first camera's sprite leaser is left with an
empty container and its next `DrawSprites` throws, every frame, from inside camera 0's
`DrawUpdate`. `SplitScreenCoop.RotSpores.cs` replaces `InitiateSprites` with one renderer
GameObject per camera sharing one mesh (built the way vanilla builds it) and the object's
particle `ComputeBuffer` (read by reflection; it is private), guards `DrawSprites` so an
emptied container rebuilds that camera's renderer instead of throwing, and disposes
everything in `Destroy`. This is the same single-camera assumption as the ripple and
level-combiner code in `WatcherCompat.cs`; any other `IDrawable` that stores per-object
(not per-leaser) Unity objects will fail the same way once two cameras view its room.

**Rearrange only within a room.** Player request: sides, axes, split-offs and (four
players) the pairing are re-decided only while every player in that layout node shares a
room (`AllInOneRoom`, the `positional` flag through `Partition`). In different rooms the
node keeps its shape; when they pipe together it re-decides with the usual hold and
margin. `ChooseSplitOff` also now finds its stored item by mask when that player is no
longer at the edge along the axis, so a player crossing the map (or the region map) no
longer forces a slide during the hold time. Test: `SeparateRoomsHold`.

**Hard freeze with nothing in the log.** A playtest ended in a freeze after a player
arrived in CC_C08 by pipe; the log just stops. A background thread (`StartHangWatchdog`
in `SplitScreenCoop.CameraDiagnostics.cs`) now watches `WatchdogFrame`, which `GrafUpdate`
advances, and after 4 s and again after 30 s without progress logs
`[Hang] … last marker=…`. `HangMarker` is a string constant set at the entry of each of
the mod's expensive paths (`RainWorldGame.Update(orig)`, `RainWorldGame.GrafUpdate(orig)`,
`UpdateDynamicLayout.Solve`, `RefreshDynamicViewShifts`, `CompositeDynamicLayout`,
`LoadLevelTexture`, `RoomRealizer_KillRoom`, `additional RoomRealizer.Update`, `CoopUpdate`,
`MonitorCameraHealth`). A marker ending in `(orig)` means the hang is inside vanilla code
called from that hook. `WrapAngle` also guards against an infinite angle, the only loop in
the solver whose bound depends on data. The frame counter is advanced from the
compositor's `LateUpdate` as well, so menus (which have no game `GrafUpdate`) do not
produce false `[Hang]` lines; the one in the third log, 4 s into the death screen with
marker `idle`, was that false positive.

### Earlier (commits `ba9a981`, `c3de29f`)

**Player soft-locked inside a shortcut pipe.** `RoomRealizer.RemoveNotVisitedRooms` calls
`KillRoom` → `AbstractRoom.Abstractize()` without consulting `CanAbstractizeRoom`, and
`ShortcutHandler.Update` only advances a vessel while `vessel.room.realizedRoom != null`.
Fixed by hooking `On.RoomRealizer.KillRoom` and refusing any room occupied by a living
player, shown or being loaded by a camera, or holding a player's vessel
(`RoomIsInUseByAnyPlayer`). Note `CanAbstractizeRoom`'s own `NonPermaDeadPlayers` guard
is behind `ModManager.CoopAvailable`, which is **false** when JollyCoop is disabled.

**Palettes differing between cameras.** `Room.Update` writes per-room values into global
shader properties outside any camera-scoped call. `On.Room.Update` tracks `curRoom` and
every `Shader.SetGlobal*` hook goes through `CollectShaderRecipients()`, which attributes
a write to the camera in scope or to every camera showing `curRoom`. Dead end:
`currentPalette.texture` **is** `paletteTexture`.

**Duplicate pause menus, dead mouse.** One `PauseMenu`, parented to `globalHudStage` at
native coordinates (`MovePauseMenuGlobal`). The cursor needed invariant 5.
`PauseMenu_ShutDownProcess` excludes `self` when looking for a duplicate to stop.

**Everything else**: dead players are skipped by `EnsureStableCameraAssignments` (a dragged
corpse forced synchronous `MoveCamera`s); `RainWorldGame_ctor` primes a layout so spawn-in
does not flash; unready cameras key on `loadingRoom` so they stay merged;
`AllPlayersInOneShelter` collapses to one view; globally routed meters take their fade
circles with them.

---

### Fixed slots and mask meshes (2026-09-16)

- Layout: see §3 "fixed slots". Position-driven rotation/side swaps removed at the
  players' request.
- **Foliage lost its colour on cameras 1–3.** Watcher `DynamicLevelElement`, `Grass`,
  ripples, warp tears and urban shadows are `MaskSource` Unity meshes (`MaskMaker`
  singleton), not Futile sprites. The pipeline per camera is: camera renders the meshes
  (opaque queue) → a full-screen grab quad (the `MaskLayer` base node) captures them
  into `_DynamicLevelElements` (or the ripple/shadow grab) → that camera's
  `LevelTexCombiner` command buffer (`DynamicLevelElementCombiner`, AfterForwardOpaque,
  attached to `fcameras[owner]` by `LevelTexCombiner_CreateBuffer`) composites the grab
  into its `_LevelTex`. But there is one GameObject per element, positioned by whichever
  `RoomCamera.DrawUpdate` ran last (`MaskSource.DrawUpdate` subtracts that camera's
  `camPos`), so only one camera ever had the meshes in view; the others combined a stale
  or black grab and the foliage rendered with the wrong data. Fix: `MaskSource_DrawUpdate`
  records each camera's wanted transform in `maskPlacements`, and
  `CameraListener.OnPreCull` → `PlaceMaskSourcesFor(n)` applies camera n's transform and
  `worldLayers[n]` (and disables the renderer for meshes camera n did not draw this
  frame) right before camera n culls. `OnPreCull`, not `OnPreRender`: Unity culls
  between the two, so transforms changed in `OnPreRender` are not honoured.
  Unverified in game: whether Unity's named `GrabPass` grabs once per *camera* (needed)
  or once per *frame*; ripple masks already relied on the former.
- **Sleep lockout / "went to an old camera" (seventh playtest log).** Chain: player 1
  took GATE_LF_SU alone; the SU world unloaded (`SentientRotSpores.RoomUnloaded → Destroy`
  destroyed the per-camera renderer GameObjects) while cameras 1 and 2 still pointed at
  SU rooms with all their sprite leasers. Their `FGameObjectNode`s now wrapped destroyed
  objects: `Futile.LateUpdate` threw NRE twice per frame for 10k frames (`[UnityLog]
  GetComponent[T]`). At the shelter, `RainWorldGame_ShutDownProcess` → `RestoreClassicWorld`
  re-parented those layers → `FGameObjectNode.HandleRemovedFromStage` touched the dead
  object → NRE out of the hook → `ProcessManager.PreSwitchMainProcess` aborted *before*
  `currentMainLoop = null`. Next frame it retried: the pre-orig part passed, vanilla
  `ClearAllSprites` hit the same node → NRE out of `orig`. From then on the half-shut-down
  game stayed current and `RoomCamera.Update → HUD.Update → JollyMeter.Update` threw every
  frame (`currentMainLoop as RainWorldGame` was fine; the HUD was cleared), so
  `ProcessManager.Update` never reached the switch again. Both throws were invisible
  because the `[UnityLog]` mirror bucketed every NRE by message. Fixes: nodes are removed
  from their containers in `SentientRotSpores_Destroy` before destruction and rebuilt if
  found dead in `DrawSprites`; `ClearStaleWorldCameras` (WorldLoaded) cleans the leasers
  of any camera whose room is in a world other than `game.world`; the shutdown hook wraps
  pre/orig/post in their own try/catch (a broken shutdown beats a permanent lockout);
  the mirror keys on message + top two frames. Log lines to expect next time:
  `[CameraMove] … cleared N sprite leasers left in unloaded world …` at a gate, and no
  `[UnityLog]` at all.
- **Stutter with three players.** The log's 46 `[FrameHitch]` lines are 50–100 ms with
  `gcCollections=0` and no room events, clustered while all three were merged in one room
  (HI_B04W): steady per-frame cost, not loads. Every RoomCamera ran DrawSprites over every
  drawable per frame even with its Unity camera off. `SpriteLeaser_Update` now skips the
  object draw for cameras not in `renderedCameraNumbers` (HUD, shader capture and leaser
  deletion are unaffected). Not yet measured; the realizer budget (1500 + 750 per extra
  player) is the other suspect if hitches persist with players in different rooms.

### Eighth/ninth playtest logs (2026-09-17)

- **Leaser leak from the DrawSprites skip.** `SpriteLeaser_Update` skipped `DrawSprites` on
  cameras that were not rendering, but vanilla drawables retire their leaser *inside*
  `DrawSprites` (`if (slatedForDeletetion || room != rCam.room) sLeaser.CleanSpritesAndRemove()`,
  `GraphicsModule.cs:176`, `CosmeticSprite.cs:27`). One room: 369 leasers on camera 0, 557
  and 745 on the two off cameras. Now `DrawSpritesRetiresLeaser` lets `DrawSprites` run when
  it would clean up (and always for drawables that are neither `UpdatableAndDeletable` nor
  `GraphicsModule`). The `[Perf]` line carries per-camera leaser counts: they should match
  across cameras in one room.
- **Foliage/rot still black on cameras 1–3 after per-camera mask placement.** A named
  `GrabPass` is performed once per frame for the first camera that draws the quad; later
  cameras reuse it, so their `DynamicLevelElementCombiner` pass composited camera 0's grab.
  `LevelTexCombiner_CreateBuffer` now copies the camera target into a per-camera RT
  (`dynamicElementGrabs[n]`) inside the "DynamicLevelElement" buffer and binds it as
  `_DynamicLevelElements` before the combiner blit. Caveat: at AfterForwardOpaque *all*
  mask layers' sources are in the target, not only the ones drawn before the quad; if
  rooms with warp tears or urban shadows show foliage artefacts, that is where to look.
  Ripple grabs (`RippleCameraData`) have the same once-per-frame limit and are untouched.
- **Warp: cameras that were never precast never arrive.** Vanilla calls
  `WarpMoveCameraPrecast` on `cameras[0]` only (`WarpPoint.cs`, `OverWorld.cs`);
  `WarpMoveCameraActual` on the others sets `loadingRoom` and nothing applies it. With a room
  they recover through vanilla's "camera needs to move" check; a session resumed from a warp
  starts them at `room=null`, they stayed there all session (ninth log: `room=null|loading=
  WRFA_SK04` for cameras 1 and 2 from 105105 to shutdown), could not share a screen key, and
  the layout stayed split with an empty cell. `RoomCamera_WarpMoveCameraActual` now falls
  back to `MoveCamera` when `warpApplyPosChangeWhenTextureIsLoaded` is false, and
  `ReconcileCameraRoom` no longer treats `loadingRoom == desiredRoom` as progress unless a
  texture apply is actually pending.
- **`ClearStaleWorldCameras` fired on the warping cameras.** At a warp every camera still
  has the old room and `loadingRoom` set when `WorldLoaded` returns, so the cleanup wiped
  all three (369/557/745 leasers), including the warp's ripple/hold-frame mask sources on
  the camera being watched (broken colours) and the other two cells' objects (empty map).
  It now skips any camera with `loadingRoom != null`.
- **Performance data.** `[Perf]` line every heartbeat: avg/p95/max frame ms over the last
  600 frames, rendered cameras, realized room count and names, realizer budget, leasers per
  camera, mask sources. The realizer budget per extra player is a Remix slider
  (`ExtraRealizerBudget`, default 750, applied at region load).

### Tenth log (2026-09-17, second session)

- **Frozen screen after sleep, no exception.** `SetSplitMode(NoSplit)` at shutdown makes
  only the *rendered* camera's listener direct. That session ended with camera 2 as the sole
  survivor, so camera 0's listener stayed `direct=false`; `RestoreMenuCameras` pointed the
  Unity camera at Futile's screen texture, but the listener's `OnPostRender` still copied
  its stale split texture over that screen every frame: the sleep screen ran under a frozen
  last frame of the game. Fixes: `RestoreMenuCameras` forces listener 0 direct and
  non-compositing (logged as a correction), and `OnPostRender` only forwards when the camera
  actually targets the split texture. The earlier freeze (log 7) was a different cause
  (the JollyMeter loop); both are gone.
- **False `[CameraHealth] draw loop stalled` at 123019 → pass-through for the rest of the
  session.** A four-second pause: vanilla draws with `PausedDrawUpdate` while paused, the
  mod noted only `DrawUpdate`, and the check ran on the first tick after unpausing (Update
  precedes GrafUpdate in `RawUpdate`). `RoomCamera_PausedDrawUpdate` now notes draws, and
  pass-through ends by itself after 600 healthy frames.
- **Pause menu cursor hidden.** `Menu.Menu` adds `cursorContainer` to `Futile.stage`; the
  global HUD stage (pause menu, meters) was appended after the root stage and so drew over
  it. `globalHudStage` is now inserted at index 0, below the root; the root keeps the cursor
  and the fade above everything as in vanilla.
- **Root stage grew by ~500 `FLabel`s per session** (2983 after six): vanilla's
  `AbstractSpaceVisualizer` community labels are hidden, never removed. Removed at shutdown.
- **Level texture cache.** `LoadLevelTexture` keeps the last 12 decoded level images (raw
  pixels, ~4.5 MB each) and uploads them with `LoadRawTextureData` on revisits (`level
  texture cached` in `[FrameHitch]` events) instead of the 50–100 ms PNG decode.
- **The stutter the players feel is tick judder.** `[Perf]` showed avg 4.2 ms (240 fps)
  with p95 12–33 ms: the 40 Hz game tick lands on one frame in six and costs 8–30 ms with
  three players and 10–11 realized rooms (budget 2995). At an uncapped 240 fps that reads as
  micro-stutter. Vanilla's own FPS cap (`options.fpsCap`, Options menu) at 60 or 120 hides
  it; lowering "Extra rooms per player" reduces the tick.

### Camera audit (2026-09-17, eleventh log: slush water twice as bright on pip cameras)

Everything a camera's picture depends on, who owns it in vanilla, and how the mod makes it
per camera. Verified against the decompiled game and the shader metadata in
`resources.assets` (property, keyword and grab-texture names are plain strings there;
`scratchpad/shaderstrings2.py` maps them to shaders).

| Input | Vanilla owner | Per camera? | How |
|---|---|---|---|
| Unity camera settings (HDR off, MSAA off, clear colour, ortho size, culling) | `Futile.InitCamera` | yes | same call for cameras 2–4 |
| Render texture format/size | `FScreen` | yes | copy of the screen texture per listener |
| `RoomCamera` state: palette textures, fade, effect_* darkness/fog/hue, ghost/rot, `pos` | `RoomCamera` | yes | one `RoomCamera` per player; heartbeat `palA/palB/blend/dark/fog` proves equality in one room |
| Shader globals set inside `RoomCamera` methods (`_PalTex`, `_LevelTex`, `_spriteRect`, `_camInRoomRect`, `_screenSize`, `_light`, `_darkness`…, `_SnowTex`, `_SnowSources`, `_WarpPointHoldFrame`) | `RoomCamera` | yes | `curCamera` scope in every `RoomCamera` hook incl. the ctor; replayed in `CameraListener.OnPreRender` |
| Shader globals set from `Room.Update`/`Room.Loaded` (`_AboveCloudsAtmosphereColor`, `_MultiplyColor`, `_SceneOrigoPosition`, `_RimFix`, `_waterTime`, `_snowStrength`, `_WindTex`…) | room objects | per room | `curRoom` scope → every camera showing the room + `RoomShaderState` for late arrivals |
| **Room-scope globals computed from one camera** (`_tileCorrection` from `BlizzardGraphics`/`DustWave`'s owner `rCam`) | room object holding a camera | **was no** | `BlizzardGraphics_Update`/`DustWave_Update` recompute per viewing camera. `[ShaderAudit]` now logs any global first written outside camera scope with the writing method — review new entries |
| Shader keywords | 11 room/camera keywords + `LOWEND` | yes | `CaptureRoomCameraShaderKeywords` replays all 11 (inventory: `grep EnableKeyword` = exactly those) |
| `SnowSource` visibility/data (`cameras[0].currentCameraPosition`) | `SnowSource` | yes | `SnowSource_Update`/`PackSnowData` per camera (older fix) |
| **Named `GrabPass` textures** (`_SlopedTerrainMask` → 29 shaders incl. `WaterSlush`, `Background`; `_DynamicLevelElements`, `_RippleMask`, `_GameplayRippleMask`, `_UrbanShadowsGrab`, `_UrbanShadowsBlurGrab`, `_PearlReaderGrab`, `_WarpTearGrab`, `_PreLevelColorGrab` (8 shaders), `_PreGrassGrab`, `_PreUrbanCandleSSSGrab`, `_MonolithGrab`, `_ARZapperGrab`, `_GildedWindGrab`, `_WarpPointGrabPass`) | Unity: once per **frame**, first camera | **was no** | one world camera per frame (`SelectFrameCamera`, Auto/option) so each frame has one grabbing camera; `_DynamicLevelElements` additionally emulated per camera while several cameras render per frame |
| `_GrabTexture` (54 shaders take it with their own unnamed `GrabPass`; 29 more only *read* it: fog, blizzard, snowfall, lights, decals, sloped terrain, the snow mounds) | Unity: whatever grab ran last, on any camera | **was no** | each camera binds its own previous frame (+1% floor) as `_GrabTexture` before it renders (`CameraListener.BindOwnGrab`); real grabs overwrite it as they draw, so this only serves sprites drawn before a camera's first grab. See "Thirteenth log" for the audit and "Fourteenth log" for a correction |
| Mask meshes (`MaskSource` GameObjects: sloped terrain, mud pits, foliage, grass, candles, pearl readers, ripples, warp tears, shadows) | `MaskMaker` singleton | yes | per-camera transform + layer in `OnPreCull` (`PlaceMaskSourcesFor`); recorded from the three `setGameObject*` setters, so direct writers are covered too (until 2026-09-19 only `DrawUpdate` was, see "Fifteenth log") |
| MSC snow (`SnowSource.visibility`, `Snow.visibleSnow`: one field each) | one camera | yes | per-camera visibility during each `UpdateSnowLight`, `visibleSnow` remembered per camera |
| Command buffers (`LevelTexCombiner`, `RippleCameraData`) | `Camera.main` | yes | attached to the owner camera (`WithUnityMainCamera`) |
| Effects gated on `cameras[0]` (`LightSource.colorFromEnvironment`, `LightBeam`, `EnergySwirl`, `GoldFlakes`, `FairyParticle`, `GenericZeroGSpeck`, `AboveCloudsView` OE clouds, `RippleFlow`, `SpawnRippleTear`) | vanilla assumes one camera | **was no** | their `Update` runs with the viewing camera swapped into `cameras[0]` (`WithViewingCameraAsPrimary`) |
| Rot spores (`SentientRotSpores` one mesh per object) | one camera | yes | renderer per camera (older fix) |
| Per-camera `BlizzardGraphics` instance registration (`room.blizzardGraphics` is one) | first camera to load the room | per room | left as vanilla: one room object, its wind map is room data; only `_tileCorrection` was camera-specific |
| Still `cameras[0]`-only and left alone | `GlobalRain` (flood/rain intensity from camera 0's room), `RainCycle`, music, `Player`→`blizzardGraphics.windMapUpdate`, dev tools | game logic, not per-view rendering | — |

**Twelfth log (artifact screenshot: water drawn as the raw level encoding, WARF).** *(The
"water" was the snow layer; the real cause is in the thirteenth-log section below. The
render-target restore described here stays, it is correct on its own.)* Build
21:03 (grab emulation on every camera, no round-robin). The water body/surface showed the
unpaletted level texture: a grab-based water shader read a surface holding the raw level,
i.e. the combiner's intermediate texture, which the mod's (and vanilla's) command buffer
leaves bound when it ends. Mitigations: every mod-built combiner buffer now ends with
`SetRenderTarget(CameraTarget)`, and under Auto the emulated grab is off. Not proven — the
same log shows whole-process freezes of 26 s, 34 s and 69 s (`[FrameHitch]` with
`gcCollections=0`, p95 still 4.2 ms, and **no `[Hang]` line**, so the watchdog thread was
frozen too): system paging, a GPU driver reset (which also wipes render textures) or the OS
suspending the game. The watchdog now logs `[Hang] the whole process stood still` for that
case, and `[Perf] renderTextures=` counts render textures. Whatever it was, it was not the
mod's frame cost.

Rules that fall out of it: (1) a global written in `Room.Update` must not depend on a
camera — if vanilla does that, recompute per viewing camera as `BlizzardGraphics_Update`
does; (2) any shader that grabs the screen is wrong on every camera after the first in a
frame — keep one world camera per frame, or emulate the grab in that camera's own command
buffer; (3) check `[ShaderAudit]` and `[Perf] alternate=` in each new log.

### Thirteenth log (2026-09-17, third session): snow drawn as raw data on one view; empty food pips

> **Superseded 2026-09-19.** The artifact is a Watcher terrain mask mesh drawn by the
> overlay camera, not the snow sprite: see "Fifteenth log". The shader facts below are
> correct; the conclusion drawn from them is not. `BindOwnGrab` stays for the reason given
> in the fourteenth-log correction.

Screenshots: the snow ground of a WARF room painted lime with a red band along the
surface, on one view while the other view's snow was right, always on the *darker* room.
Diagnosed from the compiled shaders themselves: `Tools/shaderdisasm2.py` and
`Tools/shaderbind.py` decompress `resources.assets` with UnityPy (`pip install UnityPy`) and
disassemble the DXBC with `d3dcompiler_47.dll`; `Tools/grabaudit.py` tabulates every grab
texture against every shader that reads it.

- `LevelSnowShader` (the `UpdateSnowLight` blit into `SnowTexture`) writes snow *data*:
  R = depth code of the terrain found below the pixel (+ height fraction + shadow),
  G = 1 where snow exists, B = 0. R saturates to 1.0 where the scan found only air —
  the level encoding's sky sentinel (depth 255).
- `DisplaySnowShader` (one pass, **no GrabPass**; its `Queue = AlphaTest` tag is overridden
  by Futile, see the correction in the fourteenth-log section) reads `_SnowTex`,
  `_PalTex` and `_GrabTexture`. It discards where `_GrabTexture.a < 0.8`, and outputs
  nothing where the grab has any colour and the depth is over 5 — that is what hides the
  sentinel pixels in vanilla. Otherwise depth 255 feeds
  `lerp(palette, fog, depth * _fogAmount / 30)`, an 8.5x extrapolation that saturates
  to lime and red.
- **WRONG, kept for the record:** "being AlphaTest it draws before every transparent
  sprite, so it reads another camera's grab". Futile sets every layer's queue itself;
  the snow draws after the level and reads its own camera's last unnamed grab. A black
  grab does paint the sentinel pixels lime/red, but why the grab was black is unknown.
  The eleventh log's "water" screenshot was this same snow layer.

Fix: `CameraListener.OnPostRender` copies the camera's target into `lastFrame` and blends
1.2% white over it (`FShader.Basic`, so no pixel is exactly black); `OnPreRender` binds
it as `_GrabTexture` (`BindOwnGrab`; white until the copy exists). Every real GrabPass
overwrites it as it draws, so shaders with their own grab are untouched.
`RoomCamera_UpdateSnowLight` also sets `curCamera` and restores `RenderTexture.active`
(`Graphics.Blit` leaves its destination active).

Grab audit, all 306 `Futile/*` shaders: an unnamed `GrabPass{}` is declared by 54 shaders;
`_GrabTexture` is read by 39, and 29 of those never grab: Aurora, BackgroundJaggedCircle,
Blizzard, ColoredSprite2, CustomDepth, Decal, DeepWater, **DisplaySnowShader**,
FastBlizzard, FastLocalBlizzard, FastSnowFall, FlareBomb, Fog, JaggedCircle, KarmicShield,
LightBeam, LightSource, LocalBlizzard, MudDecal, MudPit, Rubble, SlopedTerrainStain,
SlopedTerrainSurface, SnowFall, TemplarCircle, UnderWaterLight, WallLight, WaterSplash,
WeaverGlow. DisplaySnowShader is the only one *tagged* outside the Transparent queue (the
tag has no effect under Futile). The rest draw
after `LevelColor` (whose first two passes grab `_GrabTexture` and `_PreLevelColorGrab`)
in the same camera and were already fine; the background ones (Aurora,
BackgroundJaggedCircle, CustomDepth) now also get the previous-frame binding instead of
another camera's grab. Named grabs: 20 declared, read by up to 130 shaders each
(`_GameplayRippleMask` 130, `_RippleMask` 111, `_SlopedTerrainMask` 22,
`_PreLevelColorGrab` 12, `_UrbanShadowsBlurGrab` 4, `_BrainMold`/`_PreBrainMold` 3,
`_DynamicLevelElements` 2). Vanilla resets most of them to black or white per camera
every frame (`MaskMaker.Update`, `RoomCamera.DrawUpdate`, `RippleCameraData`), the
string-overload texture hook records those per camera, and each is grabbed once per
frame — which is what one camera per frame is for.

Food pips, **misdiagnosed here and corrected 2026-09-18**. This section first blamed the
gray pips on the shared meter reading a dead player's own stomach under Jolly, and added
a `Player.CurrentFood` hook that reported the pooled stomach. The user then said the pips
are *always* gray, which that cannot explain. Real cause: `RouteFoodMeter` moved each
pip's sprites to the shared HUD stage as gradient, ring, fill, **then `backCircle`**.
`RouteNode` appends, so that order is the draw order. In the Watcher campaign
(`hud.camoMeter != null`) `FoodMeter.MeterCircle.AddCircles` creates a `backCircle` for
every pip *first*: colour index 5 of `Custom.FadableVectorCircleColors`, i.e. black, at
`fade * 0.7` and `rad - 1`. It belongs under the ring and the white fill; routed last it
covered the fill, so every pip was a dark disc with a light rim whether eaten or not.
`RouteFoodMeter` now mirrors the constructor's order exactly (all gradients, darkFade,
lineSprite, per pip backCircle-ring-fill, quarter pips), and the text prompt is routed
bars, fade, label as vanilla adds them (that one was invisible: black on black). The karma
and rain meters already matched. The pooled-food hook was removed: it was built on the
wrong diagnosis and changes what vanilla Jolly shows. The underlying fact stands and may
matter later: Jolly's `AddFood`/`SubtractFood` mirror every meal into `Players[0]`, so a
meter owned by another player shows only that player's own meals until a subtraction
syncs it.

Dual display: the log attached with the report had no dual-display session
(`dualDisplay=False` on every `[CameraMode]` line), so "keyboard and pad both move
player 1, player 2 cannot be controlled" is unreproduced. Nothing in the dual-display
code touches input. What does is the self-sufficient sign-in (`RequestPlayerSignIn(1,
null)` when Jolly is off), and vanilla's defaults give player 1 the "any" preference
(keyboard **and** every joystick) and players 2-4 "specific gamepad n". `RainWorldGame_ctor`
now logs `[Input]` (each player's active flag, preference, pad number, devices) and
re-applies a player's configured preference when their setup owns no device
(`EnsurePlayerControllers`). Rendering: dual displays now take part in one camera per
frame (each camera draws straight into its own display texture, which persists between
frames), so the named grabs are right on the second display too; `alternateFrames` is
forced off in classic split, where both cameras are composited every frame, and the
`DrawSprites` skip no longer excludes dual displays.

Dual display structure audit (same session), what was wrong and what changed:

| Problem | Effect | Fix |
|---|---|---|
| `mirrorMain` was a write-only setter; `FScreen.ReinitRenderTexture` (fullscreen toggle, resolution change) rebuilt display 2's texture and pointed its image at the fresh copy | after any resolution change in a menu, display 2 showed a blank copy instead of the menu | `DisplayExtras.mirrorMain` records the mapping and `ReinitRenderTexture` restores it |
| Shutdown and `ReadSettings` mirrored `cameraListeners[1]` specifically | when player 1 had died, camera 2 owned display 2; its listener was never mirrored and display 2 froze on the last game frame through the sleep/death screen and menus. Turning the option off left display 2 frozen for the rest of the run | `MirrorSecondaryDisplays()` works on the displays: every secondary display shows the main screen and every camera bound to one stops; used at shutdown, when the option is off, and when only one camera is alive |
| Pause menu was only ever placed for split modes; dual displays stay in `NoSplit` | the menu sat at the world origin, which only camera 0 looks at: with camera 0's player dead it was invisible on both displays, and display 2 never showed a menu | `PauseMenu_ctor` builds one menu per rendered camera for dual displays too (slot offsets are zero) and moves the single menu in front of whichever camera renders |
| The water-vertex initialisation (`Water_InitiateSprites`) was gated on a split mode | the second display's water could keep unmoved vertices | applies whenever the game has more than one camera |
| Display 2 stretched the main-aspect image to its own aspect, and Futile's uv crop for aspect irregularities was copied once at creation | distortion on a second monitor of another aspect; drift after resolution changes | `AspectRatioFitter` letterboxes the copy to the main display's aspect; `SyncImageRect` copies Futile's `uvRect` on every `UpdateCameraPosition` |

Left as is: `Screen.fullScreen = true` is forced at every game start while the option is
on (Unity's multi-display needs it); the mouse cursor sprite lives on Futile's root stage
at the origin, so on display 2 the pause menu is keyboard/controller only (same as
classic split); cameras 3 and 4 are never shown with two displays.

### Fourteenth log (2026-09-18): 60 fps machine, two players, Dynamic split

`LogOutput(2).log`, build 2026-09-17 23:12 (it has the `[Input]` line). 1366x768, Jolly,
`dualDisplay=False`, one session of ~9,800 frames in WARF. No `[UnityLog]`, `[HookError]`,
`[Hang]` or `[CameraHealth]`. What the log establishes:

- **Auto never alternated while the screen was split.** Seventeen `camera rendering:` lines.
  Every one taken with two cameras rendering reads `fps=60, cameras=2, per cell=30` and picks
  "every camera every frame"; every one taken merged reads `cameras=1, per cell=60` and picks
  "one camera per frame", which is meaningless with one camera. Both `[Perf]` samples taken
  while split (frames 5732, 6332) say `rendered=[0,1] alternate=False`. The 50/40 thresholds
  were tuned on the 240 fps playtest PC; at 60 fps two views can never reach them. So on
  this machine every named grab (sloped-terrain mask, ripple masks, dynamic level elements,
  pre-level colour) belonged to camera 0 whenever camera 1 was on screen. That is the
  reported "water shaders not applying on the off camera, sometimes": sometimes = split.
  Which water input breaks is inferred from the shader audit (`WaterSlush` reads
  `_SlopedTerrainMask`, water surface/fall/splash read the ripple masks), not observed.
- **The per-camera `_DynamicLevelElements` emulation was usually not installed.**
  `LevelTexCombiner_CreateBuffer` bakes it in from `alternateFrames` at build time. Buffers
  are built when a room is entered, normally with the players together (flag true, no
  emulation), and then ran with the view split (flag false).
- **Two cameras every frame cost nothing measurable here.** `[Perf]` is a flat 16.7 ms
  (avg = p95) with `rendered=[0,1]`. The only slow stretch (frames ~1170-2730: p95 50 ms,
  hitches of 50-150 ms, up to seven suppressed between logged ones) ran with **one** camera
  rendering, in WARF_B14/B13 (`maskSources=311`, ~750 leasers per camera), and its worst
  part (2411-2565, `events=[]`) coincides with player 1 waiting in a pipe from frame 2323 to
  2814 while WARF_B13 was prepared. Vanilla prepares rooms on the main thread in slices;
  with a second player still playing, those slices are visible hitches. The cause of the
  B14 hitches is not established by this log - hence the new timing fields below.
- `[Input]`: p0 keyboard, p1 XInput pad: correct. `multiplayerContext=False` is an artifact:
  the line ran inside the constructor, before the game was the current process.

Changes (build 2026-09-18 19:16):

- `DecideFrameRendering(game)` counts the views that can appear (alive cameras), not the
  cameras rendering this instant, and leaves the flag alone while there is one view. Auto
  now alternates at 25 fps per view (leaves at 22): two views on a 60 fps machine alternate
  (30 each), three (20 each) render every frame. Trade-off: sprites inside a split view
  update at fps / views; pans, zoom and the layout still run every frame in the compositor.
  "Every frame" opts out. Raising the game's fps cap / turning vsync off gives 60 per view.
- When the flag does change, `RebuildDynamicElementPasses` removes and re-adds the
  "DynamicLevelElement" combiner pass on every camera so the buffer matches the mode.
- `[FrameHitch]` gained `ticks= updateMs= modTickMs= grafMs=` for the frame it reports
  (`Time.unscaledDeltaTime` measures the frame before the one ending, so the previous
  frame's phases are printed). `[Perf]` gained `tickMs= ticksPerFrame= modTickMs= grafMs=`
  averages. `On.RoomPreparer.Update` notes `prepare ROOM NNms` for slices of 8 ms or more.
  Frame time minus these is rendering, GPU and the vsync wait.
- `[Input]` is logged again from the first tick.

Not done, and why: emulating the MaskMaker grabs per camera for "Every frame" mode. The
mask layers draw in the opaque queue at 2001+3i (sources, second-layer sources, grab quad),
and the grab quads differ: `SlopedTerrainMaskGrab` follows its grab with a pass named
"Clear" (Blend One Zero), `DynamicLevelElementGrab` with an alpha-blended pass,
`GameplayRippleGrab` with a UsePass. Reproducing each layer's grab with `DrawRenderer` into
a per-camera texture is possible but needs each quad's second pass understood first; and
`_PreLevelColorGrab` (taken by `LevelColor` mid transparent queue) cannot be emulated from
a camera event at all. One camera per frame remains the only general answer.

**Correction to the thirteenth-log section.** It said `DisplaySnowShader` draws before every
grab because its shader is tagged `Queue = AlphaTest`. That is wrong: Futile overrides the
queue of every render layer (`FFacetRenderLayer.Update`: `renderQueue =
Futile.baseRenderQueueDepth (3000) + depth`), so the snow sprite draws in container order,
in "Foreground", after the level. What it samples as `_GrabTexture` is therefore the last
unnamed grab of its *own* camera (at least `LevelColor`'s, the background before the level),
not another camera's, and `BindOwnGrab` is overwritten before the snow draws. The shader
facts stand (they come from disassembly): the blit writes a depth-255 sentinel, and the
display shader only hides it where the grab has colour, so a black grab paints lime/red.
Why the grab was black on one view is **not** established, and whether the artifact still
occurs on builds after 2026-09-17 23:01 has not been reported. `BindOwnGrab` is kept: it is
correct for sprites drawn before a camera's first real grab (Aurora,
BackgroundJaggedCircle, CustomDepth), which otherwise read another camera's grab.

Dual display (no log of a dual session exists yet):

- By construction the old dual path drew every world on every display: it kept all cameras'
  sprites on Futile's root stage, Futile batches a stage across containers into meshes with
  1e10 bounds (`FFacetRenderLayer.cs:210`), so nothing is frustum culled and each of the two
  Unity cameras drew the sprites, and ran the GrabPasses, of all two to four RoomCameras.
  Dual displays now use the per-camera world and HUD stages; each camera's mask is its
  world layer, its HUD layer and the root stage (pause menus). `PlaceMaskSourcesFor`
  isolates mask meshes for dual too; shutdown hands the containers back. The size of the
  gain is not measured.
- **Regression in the 19:16 build, fixed 19:24: the dual pause menu showed nothing and the
  pointer sat behind the game.** Futile's stage list order is its draw order
  (`Futile.LateUpdate` resets `nextRenderLayerDepth` and walks `_stages`; every render
  layer gets `renderQueue = 3000 + depth`). The mod had built the list as global HUD,
  root, HUD 0-3, world 0-3. That never mattered while each stage had its own Unity camera,
  but the dual mask puts a camera's world, its HUD and the root stage under ONE camera,
  so the world drew last, over the HUD, the pause menus and the pointer. The list is now
  world 0-3, HUD 0-3, global HUD, root (`AddStageAtIndex`; global HUD still sits just
  under the root, which Dynamic's pointer-over-pause-menu relies on). Checked by
  simulating Futile's `AddStageAtIndex` on both orders, not in game. Startup now logs
  `[CameraLayout] stage draw order`, and `[Pause]` includes it.
- The 25 fps-per-view Auto rule applies to dual displays too: on a 60 fps machine the two
  displays now take turns, so each refreshes at half rate with correct grab effects.
  "Camera rendering: Every frame" gives full rate with display 2's named grabs wrong.
- Pause-menu pointer missing in dual mode only (23:12 build, before isolation): cause unknown.
  Vanilla only draws its sprite pointer when `options.fullScreen` is set
  (`Menu.ShowCursor`); the mod forces `Screen.fullScreen` for dual displays without
  touching that option, so with a windowed option the OS pointer is what shows. Nothing in the mod touches
  the cursor container there; it stays on the root stage at the origin, which camera 0
  views. `[Pause]` now logs mouse mode, Unity/Futile/`Display.RelativeMouseAt` pointer
  positions, screen sizes, the cursor container's parent, menu position and each enabled
  camera's mask, position and display, when a pause menu opens and closes in dual mode.

### Fifteenth log (2026-09-19): the lime/red "snow" was a terrain mask mesh; ripple camera tug-of-war; frog

**The lime/red artifact, solved, and it was never the snow sprite.** Four explanations in the
sections above (thirteenth and fourteenth log) analyse MSC's `Snow` sprite and
`DisplaySnowShader`. All of them are about the wrong object; read them as a record of
what was ruled out. What the screenshots actually show, and what was missed for three
rounds: the lime hill ignores the split. Measured on the two 2000 px wide screenshots
(divider at x = 999): artifact columns 986-1999 in one, i.e. it starts 13 px *left* of
the divider, and 0-1317 in the other, straight across it without a seam. Nothing a
world camera draws can do that, every cell is clipped and panned. It is
drawn over the finished composite, and only one camera does that: the overlay ("global
HUD") camera, which since the pause-pointer fix draws Futile's root layer 0 as well.

- The hill is a Watcher `TerrainCurve` (snow dunes, sand). Its `TerrainCurveMaskSource`
  owns a `MaskSource`: a raw Unity mesh on layer 0 with the shader `SlopedTerrainMask`,
  which writes data, not colour. Disassembled: `R = 0.667 - depth / 3`, `G` = a 0/1
  flag, `B` = a goo flag, `A = 1`. Flat lime body (front face, depth 0, flag 1), dark red
  gradient along the top (front to back edge, flag 0), lime speckles where the flag's
  sine noise fires. In vanilla the mesh is drawn in the opaque queue, the layer's grab
  quad copies the screen into `_SlopedTerrainMask` and its "Clear" pass wipes it, and
  the level is drawn over it.
- The mod's per-camera mask placement hooked `MaskSource.DrawUpdate` only. Half of
  vanilla never calls it: `TerrainCurveMaskSource`, `Grass`, `UrbanCandle`,
  `UrbanCandleHolder`, `PearlContent`, `UrbanShadow` and `RippleFullScreen` assign
  `setGameObjectPos/Rotation/Scale` themselves; `MudPit` bakes the camera into its
  vertices and never moves the object; `UrbanLife` scales its two quads once. Those
  meshes stayed on layer 0 at `-camPos` of whichever camera drew last, with no camera
  offset. Two consequences: no isolated world camera ever drew them (Dynamic and Static
  had no terrain mask, grass, candles, pearl reader content or urban shadows at all;
  `_SlopedTerrainMask` is read by 22 shaders, mostly backgrounds and lights), and the
  overlay camera drew them raw, with no grab quad to wipe them, positioned for a camera
  that usually was not the one looking at that part of the screen.
- Fix (`WatcherCompat.cs`, `MaskPlacement`): the three setters themselves are hooked
  (manual `Hook`s on the property setters; 33-47 bytes of IL, so Mono does not inline
  them), add the camera's offset and record the placement for `curCamera`;
  `MaskSource_DrawUpdate` only names the camera now. `SpriteLeaser_Update` records the
  mask sources of a leaser that nobody moved (`RecordLeaserMaskSources`: MudPit,
  UrbanLife). A source is registered in `MaskSource.CreateGameObject` and stays hidden
  until a camera has asked for it. `PlaceMaskSourcesFor` sets the layer both ways
  (world layer when isolating, 0 otherwise). And the overlay camera refuses mask meshes
  outright: `HideMaskSourcesFromOverlay` runs in its `OnPreCull`, hides anything of
  `MaskMaker` on its layers and logs `[MaskAudit]` once per material. That line should
  never appear; if it does, something places a mask mesh by a fourth route.
- Every-frame rendering: a named grab is taken for the first camera of a frame only, but
  `MaskMaker.InitTextures` (camera scope, `ApplyPositionChange`) records black for all
  six mask grabs per camera and `OnPreRender` replays it, so later cameras read black,
  not camera 0's dunes. Alternate frames gives every view its own mask.
- Unverified worry, older than this fix: under "Every frame" the emulated
  `_DynamicLevelElements` copy is taken at `AfterForwardOpaque`, after *every* mask layer
  has drawn. Layers draw in creation order; a `SlopedTerrainMaskGrab` layer created
  after the `DynamicLevelElementGrab` layer runs its full-screen "Clear" pass in
  between, which would empty the copy in rooms that have both dunes and foliage. If
  foliage loses its colour in such a room under "Every frame" only, that is why.
- Known leftover: `MudPit` has one mesh per pit and every camera's `DrawSprites` rewrites
  it relative to its own `camPos`. Two cameras on *different screens* of one mud pit room
  see the mask shifted on the camera that did not draw last. Not reported; needs a mesh
  per leaser.
- Verified without the game: build; reflection over the real `Assembly-CSharp.dll` finds
  the three public setters, `CreateGameObject`, `Frog.ReleaseGrasp`, `Snow.DrawSprites`
  and the private setter of `coopRippleDimensionPlayer`. **Not verified in game.**

Found while reading the MSC snow code for the wrong reason, real all the same (Saint's
regions, not the Watcher's): `SnowSource_Update` started `finalVisibility` from the old
value, so a source outside every camera's screen stayed at 2 ("never checked"), which is
itself a trigger: every camera in the room re-blitted its full-screen snow map on every
tick. It starts from 0 now. `UpdateSnowLight` packs the sources marked visible, and that
mark meant "some camera sees it": each blit now gets its own camera's answer (the shared
values are put back afterwards). `Snow.visibleSnow` is one field written by the last
blit and read by every camera's leaser; it is remembered per camera
(`RememberVisibleSnow`, `Snow_DrawSprites`). `LevelSnowShader` cannot write a flagged
pixel with the sky sentinel (min-below starts at max-above and only falls; equal means
no flag), so nothing in that pipeline produces lime. The snow probe written for the
wrong theory was removed again; `ReadSmall` survives in `CameraDiagnostics.cs`.

**Camera tug-of-war in the ripple layer** (`[CameraHealth] forcing room resync ...
reason=player assignment` 120 times, camera 0 flipping between WRFA_A21 and WRFA_C11
every three frames). Jolly's block of `RoomCamera.Update` ends with
`if (cutscenePlayer == null && coopRippleDimensionPlayer != null) followAbstractCreature
= coopRippleDimensionPlayer;` and sets that property to the last Watcher-class player
while `game.ActiveRippleLayer != 0`. One camera: follow the Watcher through the ripple
layer. A camera per player: every camera was dragged to that player each tick and
`EnsureStableCameraAssignments` dragged it back. `RoomCamera_Update` clears the property
before `orig` when every player has a camera; vanilla sets it again further down the same
call, so `JollyMeter` and `Player`, which read `cameras[0]`'s copy, see what they did.

**Frog** (`NullReferenceException` in `Watcher.Frog.ReleaseGrasp` 1201 times, one per
tick, to the end of the session). Vanilla bug: it dereferences `grasps[grasp].grabbed`
for an empty slot when `FrogState.creatureAttachedTo.HasValue`, and
`Creature.LoseAllGrasps` calls it for every slot. An activating warp point does that each
tick (`WarpPoint.SuckInCreatures`), the exception aborts `Room.Update`, the room stops.
`Frog_ReleaseGrasp` returns for an empty slot, as `Creature.ReleaseGrasp` does.

**Dual display pause menu** ("the pointer is behind the game and none of the menu shows").
That is exactly what the 19:16 build of 2026-09-18 drew: world stages listed after the
root stage, so one camera drew the world over HUD, menu and pointer. The 19:24 build
reordered the stages; the fifteenth log (20:14 build) confirms the order at startup but
contains no dual session and no `[Pause]` line, so whether the report is of the old build
or of a second cause is **not known**. Re-read for this round: `Menu.Menu` adds
`container` and `infoLabel` to `Futile.stage` in its constructor and `cursorContainer` in
`Init()`, `PauseMenu` adds its black sprite (fading to 25%, not 50%) to
`pages[0].Container`, nothing in vanilla re-adds a stage, and `FFacetRenderLayer.Update`
assigns `3000 + depth` whenever the depth changes. Nothing else was found. Because the
symptom is purely visual, the log now measures it: when a pause menu opens (Classic and
dual; Dynamic draws its menu over the composite) the mean luminance of each rendered
camera's last frame is stored, and 0.8 s later compared with the frame that has the
menu's overlay in it. `[Pause] overlay check cam=N display=D: luminance before= after=
ratio= -> ...` says whether that view got darker (menu drawn over the world) or not
(missing or behind it), and lists each stage's render queue range.

**Remix menu.** `OpTab._AddItem` appends each element's container, so add order is draw
order, and a combo box's open list lives in the box's own container: the style list
opened underneath the two checkboxes added after it (same for both boxes on the Dynamic
tab). Boxes are created through `Combo(...)`, which moves the box to the front when its
list opens, and are added last, lowest first. Input was never affected: an open list
makes its box Remix's held element.

Housekeeping: the patch helper used in these sessions rewrote files as LF with a BOM.
Line endings (CRLF, as `core.autocrlf` checks out) and BOM state (as in `HEAD`) were put
back; `git diff` shows content only.

### Sixteenth log (2026-09-19, evening): dual displays - first pause, "persistent lag", warmth meter

One dual display session, two players, Static style selected, 19 000 frames, build 18:43.
No `[MaskAudit]`, no `[UnityLog]`, no `[HookError]`.

**"The pause menu showed only the cursor the first time, and worked every time after."**
The new `[Pause] overlay check` answered it: first pause `cam=0 ... IS drawn over this
view`, `cam=1: the menu closed before it could be measured`, and right after it
`[CameraHealth] cam=1 stopped rendering/compositing for 268 frames; enabled=False`.
Camera 1 did not render once during that pause; display 2 showed a still frame of the
game with the pointer over it. Cause: `SelectFrameCamera` returned early when the
rotation was off and left the camera whose turn it was not switched off. The dynamic
pipeline switches its cameras on again every tick; dual displays only in `SetSplitMode`.
Auto left the rotation at frame 7321 ("fps=28": a mean that contained the 1.8 s region
load) and again at 9597 after a pause; both times display 2 froze until the health
watchdog, which does not run while paused, recovered it (268 and 92 frames). This very
likely was the earlier "pause menu completely broken" report as well. Fixed:
`rotationHoldsCameras`; when the rotation stops, every rendered camera is enabled again.

**"Persistent frame drops and lag, not the hardware."** Correct, and it was this mod's
doing. `[Perf]` is flat: `avgMs=16.7 p95Ms=16.7`, `tickMs=2.5`, `grafMs=1.0`. The machine
sits at the game's 60 fps limit with time to spare. But `alternate=True` with two views
means each view is drawn every other frame: 30 frames a second per display, against a
40 Hz game tick. Auto's threshold (25 per view, chosen in the fourteenth-log round to get
the grab effects right) made that the normal state on every 60 fps machine, split screen
included. Fix, `ApplyRotationFrameCap`: while N views take turns the frame limit
(`Application.targetFrameRate`, which vanilla sets from `options.fpsCap` once at startup)
is raised N times, so each view keeps the player's rate, and the value found is put back
when the rotation stops (merge, one survivor, shutdown). The work per second is the same
as "every camera every frame" at the normal limit: one camera per frame, twice as many
frames; `DrawSprites` is already skipped for cameras that do not render.
Auto was rewritten around what it has to judge: the median frame time of frames in which
the rotation ran, per view, against 38 fps. Below that (slow machine, or vsync pacing the
game at 60: the limit does nothing under vsync) every camera renders every frame and the
rotation is tried again after 30 s, doubling to 5 min; under vsync the outcome is computed
from the refresh rate instead of tried. It cannot be judged from outside the rotation:
under the normal limit the frame rate says nothing about what the machine could do.
`[Perf]` now prints `fpsPerView`, `frameLimit`, `vsync`, `p99Ms`.

Still unexplained: during the third pause, from 1.6 s in, every frame took 67 ms
(`updateMs=0 grafMs=0`, 228 frames), in a heavy room pair (201 render layers on world 1);
two earlier pauses in the shelter were 16.7 ms. None of the measured phases held the time.
`[FrameHitch]` and `[Perf]` now also carry `rainWorldMs` (all of `RainWorld.Update`:
ticks, drawing, menus, side processes), `futileMs` (`Futile.LateUpdate`, the mesh
rebuild), `renderMs` (each world camera from `OnPreCull` to `OnPostRender`) and `paused=`.
Whatever is left of a slow frame after those is the GPU, the present or another plugin.

Other waste found in the same pass:
- Game over: nobody alive means `desiredRenderedCameras` was empty while `SetSplitMode`
  falls back to camera 0, so the comparison failed on every tick and `SetSplitMode` ran
  84 times in 95 frames (display rebinding, two log lines each). Same fallback on both
  sides now.
- `[CameraState] state change` fired every 15 frames for the whole session, 749 of the
  log's 1153 lines, because the key contained `enabled=` and the rotation flips it every
  frame. It prints `enabled=turns` while the rotation runs.
- `GetAliveCameraNumbers` was a seven-stage LINQ chain with a closure per player, run
  every tick and more. Plain loop.
- The rain, warmth and Gourmand meter hooks allocated two lists per meter per camera per
  frame even when there was nothing to move. Reused lists, and no work for a zero offset.
- `LogRenderTextures` (`Resources.FindObjectsOfTypeAll`) ran every heartbeat; once a minute
  now. Heartbeats go by the clock (10 s), not by 600 frames.
- Noted, not changed: unnamed render textures grow through a session (12 to 124 here,
  +14 MB). `DynamicLevelElement.AddLevelCombiner` passes `RenderTexture.GetTemporary(1, 1)`
  and a new `Material` every time the pass is added and nothing releases them; vanilla
  does the same, the mod does it once per camera and again on every Auto flip.

**Warmth meter "bugging around the bottom left".** An old hook, not the layout.
`HypothermiaMeter.Draw` places its ten circles itself (`circles[i].pos = pos + ...`),
unlike `RainMeter`, which does that in `Update`. `HypothermiaMeter_Draw` shifted the
circles, let `Draw` overwrite `pos`, then "restored" each circle's `pos` to its value from
before `Draw`. `HUDCircle.Update` copies `pos` to `lastPos` on the next tick, so `lastPos`
never left the constructor's `(-100, -100)`, and a circle is drawn at
`Lerp(lastPos, pos, timeStacker)`: all ten swept from beyond the bottom left corner to
their place forty times a second, in Classic and on dual displays, even with a zero
offset. The hook now moves the meter (`self.pos`/`lastPos`), which is what `Draw` reads.
Under Dynamic and Static the hook passed through and the meter was not visible at all:
it stays in its view's HUD (warmth is per player; food, karma and rain go to the global
stage), and `DynamicHudShift` leaves the HUD's bottom left corner outside every cell
smaller than the screen. `HypothermiaMeterOffset` puts it in the corner of its own cell
(`(cellMin + shift) * sSize`, since a HUD point h lands at h - shift). The same is
probably true of the Watcher's `CamoMeter`, `AmmoMeter` and `BreathMeter`; not touched.
`GetHUDPartCurrentCamera` now finds the camera through the HUD when no camera is in scope:
while paused `RainWorldGame.GrafUpdate` draws the HUDs itself and the Classic meters lost
their split offset for the length of the pause.

Verified without the game: build, solver tests, reflection over the real assembly for the
new hook targets. **None of it in game.**

### Static split style (2026-09-18)

A third value of the "Split-screen style" option, next to Dynamic and Classic. It is the
Dynamic pipeline (isolated stages, compositor, HUD routing, one camera per frame, every
per-camera shader fix) with cells that never merge:

- Regions depend on how many players are **alive** and on nothing else: one full screen, two
  halves (lower camera number left), a half over two quarters (lowest number on top), four
  quarters in camera order. Standing together, sharing a screen or a shelter changes nothing.
- A death hands the screen to the survivors (the usual 0.7 s fade and slide); a revival
  hands it back. That was the user's explicit correction to "keeps it the whole time".
- Implementation: `staticStyle` (from the option) and `NeverMerge => alwaysSplit ||
  (staticStyle && !dualDisplays)`. `NeverMerge` replaces every read of `alwaysSplit` in the
  dynamic pipeline (`dynamicSettings.permanentSplit`, the compositor's `fullyMerged`, the
  direct full-screen test, Jolly arrow hiding) and in the classic fallback, so Static on a
  machine where the dynamic pipeline is unavailable degrades to Classic always-split. The
  one behavioural difference from Dynamic + "Permanent split" is that Static skips the
  `AllPlayersInOneShelter` collapse. The solver needed no change: this is its
  `permanentSplit` fed with the alive cameras.
- Dual displays are untouched: they force `alwaysSplit` off and `NeverMerge` ignores
  `staticStyle` while they are on.
- Two cells on one prebaked screen still draw the same camera's image (the base-camera
  choice in `UpdateDynamicLayout` is independent of merging), so together players cost one
  world render and their grab effects are right without taking turns.
- Tests: `StaticStyle` (players wandering together on one screen for 240 ticks, counts 1-4:
  regions never move, never share, dividers stay solid, exact slot rectangles; then
  3 -> 2 -> 1 -> 3 players for death and revival). Mutation-checked: with `permanentSplit`
  off it fails with "regions started merging". Startup logs `[CameraLayout] split style=`.
- Merge distance and blend width do not apply to Static; zoom, divider, smoothing, filter
  and camera rendering do. Not tested in game.

## 6. Open / unverified

Ordered by confidence that something is still wrong or unknown.

1. **Stutter.** The second playtest log has 19 `[FrameHitch]` lines over ~190k frames.
   The big ones are room loads (633 ms at spawn, 425 ms `realize CC_C03`, 329 ms with no
   event) and region/gate transitions; texture sharing fired 80 times and every camera
   switch with a copy stayed near 60 ms. Frames of 50–130 ms with `events=[]` and no GC
   are draw cost or world update, not the mod's bookkeeping. Nothing repeated per tick.
2. **Palette differences.** The echo/rot mirroring is the only camera-specific palette
   state found that vanilla applies to camera 0 alone. If the next heartbeat shows two
   cameras in one room with identical `palA/palB/blend/ghost/fog` and they still look
   different, the cause is outside `RoomCamera` state: suspect the Watcher For All plugin
   (loaded in every log; not inspected) writing shader globals from its own hooks, which
   would be attributed to whichever camera was in scope or to none.
3. **Game over.** The last log has no `[Coop]` lines because they are new. The flow is
   `UpdateCoopGameover` → `GameOver(null)` (IL hook lets it through when
   `coopActualGameover`) → `InitGameOverMode` on camera 0 → prompt → key → death screen.
   Also note `IsCreatureDead` treats a *living* player whose realized creature is slated
   for deletion as dead; the last log shows camera 1 flip dead→alive→dead near the end,
   so a living-but-abstracted player can drop out of the layout.
4. **Transition feel.** `transitionSeconds` (0.45) is untuned for the fixed-slot
   slides (a pair forming beside a third player, a death). There is no rotation or
   side swap any more.
5. **Fixed slots in play.** Since 2026-09-16 the layout only restructures on a merge,
   a part, an arrival or a removal, so every `[CameraLayout] … restructured` line in
   the next log should coincide with one of those; any other one is a bug in the
   grouping inputs (screen keys), not in the solver. Whether players miss the old
   world-direction ordering (camera 0 is always left/top now) is for the playtest.
5b. **The freeze.** Unexplained. The next log will carry a `[Hang]` line naming the last
   marker; if the marker is `RainWorldGame.Update(orig)` look at realizer/shortcut
   interplay (three cameras, two in pipes to the same room, one `realizedRoom=null`), not
   at the layout code.
5c. **"Screen after the death screen".** The third log ends at the death screen with no
   further process switch, so what broke was never logged. The stale level-texture key
   is the fix applied; if it recurs, the `[MenuCamera]` line for the next process and the
   stage histogram are the evidence to read.
6. **Death stall** — the forced room resync never fired in captured logs.
7. **Dual display input** — reported: keyboard and pad both move player 1, player 2
   cannot be controlled; no log of that session yet. In the next log read the `[Input]`
   line at game start: `devices=[none]` for player 2 means the sign-in path; player 1
   listing the pad means the "any" preference — set player 1 to Keyboard in Input
   Settings. One camera per frame on two displays is new and untested; "Camera
   rendering: Every frame" turns it off. The structural fixes (mirror state, pause menu
   per display, letterboxing) are also untested in game.
8. **Terrain mask fix unverified in game.** Expect no lime/red hills anywhere, no
   `[MaskAudit]` line, and dunes, grass, candles and urban shadows looking as in single
   player on every view under Dynamic and Static (they had no masks at all there). If a
   grab-reading effect (fog, blizzard, aurora) looks wrong in the first frames after a
   room change, the previous-frame `_GrabTexture` binding is the input to suspect.
8b. **Dual display pause menu**: explained by the sixteenth log (display 2 was not
   rendering) and fixed, unverified. In the next dual-session log every pause should
   have an `overlay check` for both cameras saying "IS drawn over this view", and there
   should be no `[CameraHealth] ... stopped rendering` line.
8c. **Frame rate per view**: `[Perf] fpsPerView=` should equal the game's frame limit
   while `alternate=True` and two views render (`frameLimit=` twice the player's).
   If it reads half of that, look at `vsync=` first. If `[CameraLayout] camera
   rendering:` lines alternate between the two modes, the machine cannot hold the
   doubled rate and the back-off is doing its job; "Every frame" is the setting then.
8d. **67 ms frames while paused** (sixteenth log): unexplained. Read `rainWorldMs`,
   `futileMs`, `renderMs` on the `[FrameHitch]` lines with `paused=True`.
9. **Food pip fix unverified in game**: in the Watcher campaign with two or more players,
   eaten pips should be white discs on a dark backing and uneaten ones a ring on a dark
   backing. If they are still all dark, look at what else draws over them on the shared
   HUD stage, in routing order.

---

## 7. Reading the log

`BepInEx\LogOutput.log`. Mod tags:

| Tag | Meaning |
|---|---|
| `[FrameHitch]` | A rendered frame over 50 ms, with what happened in it and where the main thread spent it: `updateMs` (game ticks), `grafMs` (`RainWorldGame.GrafUpdate`), `rainWorldMs` (all of `RainWorld.Update`, so menus and side processes too), `futileMs` (`Futile.LateUpdate`), `renderMs` (world cameras, cull to `OnPostRender`), `paused=`. Time not covered by those is the GPU, the present or another plugin. **Start here for stutter** |
| `[Coop]` | Game-over decision, which camera's prompt entered game-over mode, `GoToDeathScreen` |
| `[Hang]` | Main thread stalled 4 s / 30 s; `last marker` names the mod path (or vanilla `orig`) it was in. **Start here for a freeze** |
| `[UnityLog]` | A Unity exception/error mirrored into this log with its stack trace (the playtest config does not write Unity's log). **Start here for a black screen with audio** |
| `[HookError]` | The mod's own after-draw/after-update code threw and was contained |
| `[CameraHealth] draw loop stalled` | `RoomCamera.DrawUpdate` stopped completing while `Update` runs; the mod's draw-path code is now in pass-through |
| `[MenuCamera]` | Watchdog corrections and a camera snapshot after every process switch. **Start here for a black menu** |
| `[CameraLayout]` | Layer allocation at startup; then every structural layout change as `groups=[cam:sharesImageWith,...]\|sources=[...]\|rendering=[...]\|direct=bool`; `restructured; cells=[…]` each time the layout tree changed shape (a slide) |
| `[CameraState]` | Per-camera snapshot on change + every 600 frames |
| `[CameraMode]` | Split-mode transitions and world-direction fallbacks |
| `[CameraMove]` | `RoomCamera.MoveCamera` calls |
| `[CameraHealth]` | Stall detection and recovery. **Any of these is a red flag** |
| `[CameraPreload]` | Shortcut destination tracking |
| `[RoomRealizer]` | Refused abstractization |
| `[LevelTexture]` | Level-image copy fell back to decoding (warning) |
| `[Perf]` | Every 10 s: avg/p95/p99/max frame ms, `fps`, `fpsPerView` (what each view gets while they take turns), `frameLimit` and `vsync`, rendered cameras, `alternate=` (one camera per frame), realized rooms, realizer budget, the phase averages of `[FrameHitch]`, sprite leasers per camera, mask sources; once a minute the render texture census. A flat 16.7 ms with `fpsPerView=30` IS the lag. **Start here for "it lags"** |
| `[ShaderAudit]` | First write of a shader global outside camera scope, with the vanilla method that wrote it. **Start here for "effect X looks different on the other cameras"** |
| `[Pause]` | Dual display only: pointer positions (Unity, Futile, `Display.RelativeMouseAt`), mouse mode, cursor container parent, menu position and each enabled camera's mask/position/display when a pause menu opens and closes. **Start here for the missing pause pointer** |
| `[Pause] overlay check` | Classic and dual: 0.8 s after a pause menu opens, whether each rendered view really got darker (the menu's 25% black overlay is drawn over it) and every stage's render queue range. **Start here for "the pause menu does not show"** |
| `[MaskAudit]` | A raw Watcher mask mesh was about to be drawn by the overlay camera and was hidden. Should never appear; it is the lime/red hill of 2026-09-17..19 trying to come back |
| `[Input]` | Each player's control setup at game start: active flag, preference, pad number/guid, preset, devices, plus whether the game counts as multiplayer. **Start here for "player 2 cannot move"** |

Reading `[CameraState]`: `realizedRoom=null` means the followed creature has no room —
in a shortcut, abstracted, or dead. `postRenderAge`/`compositeAge` growing large on a
*disabled* camera is normal. Growing on an *enabled* one is a stall.

The old `player visibility correction` warning is gone; if it reappears someone
re-introduced non-rectangular cells.

---

## 8. Investigating game code

`dnSpy.Console.exe` decompiles one type to stdout — no GUI needed:

```powershell
& "C:\Users\20xha\Documents\GitHub\dnSpy\dnSpyBinary\dnSpy.Console.exe" -o <outDir> --type RoomCamera "D:\Steam\steamapps\common\Rain World\RainWorld_Data\Managed\Assembly-CSharp.dll" > RoomCamera.cs
```

`-o` is required even though output goes to stdout. Nested types take their full name
(`--type JollyCoop.JollyHUD.JollyPlayerSpecificHud`). `Futile` and `FContainer` are in
`Assembly-CSharp-firstpass.dll`.

List the hooks that actually exist for a type (HookGen exposes private methods too,
which is how `On.RoomCamera.ApplyPalette` and `IL.RoomCamera.ApplyPositionChange` are
reachable):

```powershell
$asm=[System.Reflection.Assembly]::LoadFrom("...\BepInEx\plugins\HOOKS-Assembly-CSharp.dll")
$asm.GetType("On.RoomCamera").GetEvents() | % { $_.Name }
```

`Add-Type` throws `ReflectionTypeLoadException` on that DLL; `LoadFrom` + `GetEvents`
works.

**Dump the IL you are about to match** before writing an `ILCursor` pattern:

```powershell
Add-Type -Path "...\Managed\Mono.Cecil.dll"
$m=[Mono.Cecil.ModuleDefinition]::ReadModule("...\Managed\Assembly-CSharp.dll")
$meth=$m.GetType("RoomCamera").Methods | ? { $_.Name -eq "ApplyPositionChange" }
$meth.Body.Instructions | % { "$($_.OpCode.Name) $($_.Operand)" }
```

**Find every caller of a game method** with the same module: iterate `Types → Methods →
Body.Instructions` and test `Operand -is [Mono.Cecil.MethodReference]`. Swap the
predicate for `FieldReference` to find writers of a shader-property field.

---

## 9. Suggested next steps

1. Playtest with three players; collect `[FrameHitch]`, `[Hang]` and `restructured`
   lines, a pause, an Exit, a death, and all three players taking one pipe together.
2. Tune the layout hysteresis from how it feels; every constant is at the top of
   `SplitLayoutSolver`.
3. If pause bars still look odd, capture a screenshot with the debug overlay on.
4. Address the `cameras[0]`-only ghost/rot palette calls if Watcher support matters.
