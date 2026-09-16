# SplitScreen Co-op — developer handoff

Updated 2026-09-15 (second pass, after the three-player playtest). Covers the
`dynamic-split-screen` branch at commit `c3de29f` plus the uncommitted tree. Read
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
It ramps to solid over `DividerInvisibleBelowPixels` (4) to `DividerOpaqueAbovePixels`
(300); the ramp is that wide so the line fades across the *whole* glide as two views
converge, not only its last few pixels. `DividerAlpha` then damps it per camera pair
(`DividerFadeSeconds`, 0.2 s), so a camera cut, which moves one view by a screen in one
frame, fades the line in rather than popping it. Different rooms are solid.
The solver still computes a distance-based `DividerSegment.alpha` (`PairMemory.line`,
`lineSmoothingTime`); the compositor only uses it as a fallback when alignment cannot be
computed. Two rejected variants, so nobody re-tries them: alignment with a narrow 64 px
ramp never crossed its band (halves on one screen are identical, on two screens a whole
screen apart); distance alone hid the line while the views were still visibly apart.

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
`timeStacker` the sprites used. Members of one image group share the group's live
average position as their anchor, so their shifts are identical. The result is
`SmoothDamp`ed over 0.12 s and snaps only when the image underneath changed: the base
camera's room or camera position, or the cell moving more than 0.08 across the screen.
Only base cameras have their Unity camera enabled.

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
5. **Rectangle layout feel.** Hysteresis margins (`SwitchMargin`, `layoutHoldSeconds`,
   `directionDeadZone`) and the 0.75 squareness weight are untuned guesses. Two-player
   stacking needs roughly 2.6× more vertical than horizontal separation.
6. **Death stall** — the forced room resync never fired in captured logs.
7. **Dual display** untested this cycle.

---

## 7. Reading the log

`BepInEx\LogOutput.log`. Mod tags:

| Tag | Meaning |
|---|---|
| `[FrameHitch]` | A rendered frame over 50 ms, with what happened in it. **Start here for stutter** |
| `[Coop]` | Game-over decision, which camera's prompt entered game-over mode, `GoToDeathScreen` |
| `[MenuCamera]` | Watchdog corrections and a camera snapshot after every process switch. **Start here for a black menu** |
| `[CameraLayout]` | Layer allocation at startup; then every structural layout change as `groups=[cam:sharesImageWith,...]\|sources=[...]\|rendering=[...]\|direct=bool` |
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

1. Playtest with three players; collect `[FrameHitch]` lines, a pause, an Exit, a death.
2. Tune the layout hysteresis from how it feels; every constant is at the top of
   `SplitLayoutSolver`.
3. If pause bars still look odd, capture a screenshot with the debug overlay on.
4. Address the `cameras[0]`-only ghost/rot palette calls if Watcher support matters.
