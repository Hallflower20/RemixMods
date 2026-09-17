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

`SplitLayoutSolver` builds a guillotine partition of the screen:

- **Two groups** (`SplitTwoRotating`): one straight line through the screen centre. Its
  normal's angle is damped (`dividerTurnSeconds`, 0.3 s) towards a target: while both
  players share a prebaked screen the target is perpendicular to the players' on-screen
  direction, so the line turns continuously with them; on different screens it is the
  nearest axis (side by side or stacked, chosen by the score below with the usual dead
  zone and hold time). Axis flips and side swaps are therefore rotations, never cuts.
  A line through the centre halves the screen exactly; a dying player's weight slides
  the cut off-centre (`CutForArea`). The diagonal cells this produces have little pan,
  but they only occur while both players are on one screen, where both cells draw the
  same picture, so a player can never be hidden by the line.
  Axis score = how much of the separation lies along that axis + 0.75 × how square the
  resulting cells are (in screen-height units, so the 16:9 screen strongly prefers
  columns unless the players are clearly above one another).
- **Three groups**: peel one off (the most isolated, by gap to its neighbour along an
  axis), weighed against the *largest* remaining group, not their sum. Three players give
  one half and two quarters; a pair sharing an image beside a single player gives the
  pair two thirds. The remainder recurses.
- **Four groups**: always two against two, then each pair splits → a 2×2 grid.
- Cells narrower or shorter than 0.3 of the screen carry a heavy score penalty.

Hysteresis lives in `NodeMemory`, keyed by the bitmask of cameras in the node: a node
keeps its axis, its side order and its split-off choice unless the alternative wins by
`SwitchMargin` (0.25) *and* `layoutHoldSeconds` (0.75 s) have passed; side swaps also
need `directionDeadZone` (160 world px). Cut fractions are `SmoothDamp`ed so a dying
player's cell slides shut and a pair merging beside a third player slides from a half to
two thirds.

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

Structural changes with three or four cells (a different player peeled off, an axis
flip inside the tree) are animated, never cut. The solver compares each live cell's
target rectangle with the previous tick's; a jump over `SnapThreshold` starts a
**slide** of `transitionSeconds` (0.45 s): each cell's `polygon` interpolates from its
previous rectangle to `targetPolygon`; `Layout.sliding` is true meanwhile. Sliding
rectangles overlap and leave gaps, so the compositor first draws every `targetPolygon`
(the resting layout) and then the sliding `polygon`s over it, all opaque. Two-cell
layouts never slide; their single line is damped continuously (see above).

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
4. **Transition feel.** `transitionSeconds` (0.45), `SnapThreshold` (0.04) and the
   hysteresis constants are untuned. A side swap rotates 180°, which is the old
   "dynamic" look; if that reads as too much motion, shorten it or dissolve instead.
5. **Rectangle layout feel.** Hysteresis margins (`SwitchMargin` 0.35, `layoutHoldSeconds`
   2 s, `directionDeadZone`) and the 0.75 squareness weight are guesses tuned once from a
   three-player complaint. Two-player stacking needs roughly 2.6× more vertical than
   horizontal separation. Count `restructured` lines in the next log: more than one every
   few seconds with three players standing still means the hold is still too short.
5b. **The freeze.** Unexplained. The next log will carry a `[Hang]` line naming the last
   marker; if the marker is `RainWorldGame.Update(orig)` look at realizer/shortcut
   interplay (three cameras, two in pipes to the same room, one `realizedRoom=null`), not
   at the layout code.
5c. **"Screen after the death screen".** The third log ends at the death screen with no
   further process switch, so what broke was never logged. The stale level-texture key
   is the fix applied; if it recurs, the `[MenuCamera]` line for the next process and the
   stage histogram are the evidence to read.
6. **Death stall** — the forced room resync never fired in captured logs.
7. **Dual display** untested this cycle.

---

## 7. Reading the log

`BepInEx\LogOutput.log`. Mod tags:

| Tag | Meaning |
|---|---|
| `[FrameHitch]` | A rendered frame over 50 ms, with what happened in it. **Start here for stutter** |
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
