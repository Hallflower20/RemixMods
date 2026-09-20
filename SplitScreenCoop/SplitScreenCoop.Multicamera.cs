using System;
using System.Linq;
using UnityEngine;
using MonoMod.Cil;
using Mono.Cecil.Cil;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        /// <summary>
        /// Dialogue centered and oncreen
        /// </summary>
        public Vector2 DialogBox_DrawPos(On.HUD.DialogBox.orig_DrawPos orig, HUD.DialogBox self, float timeStacker)
        {
            if (dynamicStyle && !dualDisplays)
            {
                // The native HUD texture is translated to the region centroid by
                // DrawDynamicHud; shifting this twice would push dialog off-screen.
                return orig(self, timeStacker);
            }
            if (CurrentSplitMode == SplitMode.SplitVertical)
            {
                return orig(self, timeStacker) - new Vector2(self.hud.rainWorld.screenSize.x / 4f, 0f);
            }
            return orig(self, timeStacker);
        }

        /// <summary>
        /// reduce double audio
        /// </summary>
        public void VirtualMicrophone_DrawUpdate(On.VirtualMicrophone.orig_DrawUpdate orig, VirtualMicrophone self, float timeStacker, float timeSpeed)
        {
            if (dynamicStyle && !dualDisplays)
            {
                var view = DynamicViewportForCamera(self.camera.cameraNumber);
                // A camera whose player is dead, or which has no live cell on screen,
                // must not contribute a point of view to the mix. The dead-player
                // test does not depend on the layout, so it holds during layout
                // transitions and game over too. VirtualMicrophone.Update rebuilds
                // volumeGroups every tick, so zeroing them here is per frame only.
                AbstractCreature followed = self.camera.followAbstractCreature;
                bool followerDead = followed == null || IsCreatureDead(followed);
                bool noLiveCell = dynamicActive && (view == null || view.ghost);
                if (followerDead || noLiveCell)
                {
                    for (int g = 0; g < self.volumeGroups.Length; g++) self.volumeGroups[g] = 0f;
                }
                else if (view?.sharesImageWith >= 0)
                {
                    RoomCamera source = CameraByNumber(self.camera.game, view.sharesImageWith);
                    if (source?.virtualMicrophone is VirtualMicrophone other)
                        self.volumeGroups[0] *= Mathf.InverseLerp(100f, 1000f,
                            (self.listenerPoint - other.listenerPoint).magnitude);
                    self.volumeGroups[1] = 0f;
                    self.volumeGroups[2] = 0f;
                }
                orig(self, timeStacker, timeSpeed);
                return;
            }
            if (self.camera.cameraNumber > 0 && self.camera.room == self.camera.game.cameras[0].room)
            {
                if (self.camera.game.cameras[0].virtualMicrophone is VirtualMicrophone other)
                {
                    self.volumeGroups[0] *= Mathf.InverseLerp(100f, 1000f, (self.listenerPoint - other.listenerPoint).magnitude);
                }
                self.volumeGroups[1] *= 0f;
                self.volumeGroups[2] *= 0f;
            }
            orig(self, timeStacker, timeSpeed);
        }
        
        public bool inpause; // non reentrant
        public bool[] oldCameraZoom = new bool[] { false, false, false, false };
        /// <summary>
        /// Make 2 pause
        /// </summary>
        public void PauseMenu_ctor(On.Menu.PauseMenu.orig_ctor orig, Menu.PauseMenu self, ProcessManager manager, RainWorldGame game)
        {
            orig(self, manager, game);

            if (dynamicStyle && !dualDisplays && dynamicLayout != null)
            {
                // One pause menu for the whole screen. A per-region copy is drawn
                // inside that region's HUD texture, which the compositor translates,
                // so what you see is not where Futile thinks the button is and the
                // pointer cannot reach it. The global stage is drawn over the
                // finished composite at native coordinates, so the mouse lines up.
                MovePauseMenuGlobal(self);
                return;
            }

            // Dual displays never leave NoSplit but render two cameras, and each
            // display needs its own menu; the per-slot offsets are zero there.
            if ((CurrentSplitMode != SplitMode.NoSplit || dualDisplays) && renderedCameraNumbers.Count > 1 && !inpause)
            {
                inpause = true;
                try
                {
                    for (int slot = 0; slot < renderedCameraNumbers.Count; slot++)
                    {
                        int cameraNumber = renderedCameraNumbers[slot];
                        RoomCamera roomCamera = game.cameras.FirstOrDefault(camera => camera.cameraNumber == cameraNumber);
                        if (roomCamera == null) continue;
                        oldCameraZoom[cameraNumber] = cameraZoomed[cameraNumber];
                        SetCameraZoom(roomCamera, false);
                    }

                    int firstCamera = renderedCameraNumbers[0];
                    self.container.SetPosition(camOffsets[firstCamera] + PauseOffsetForSlot(CurrentSplitMode, 0, manager.rainWorld.screenSize));
                    for (int slot = 1; slot < renderedCameraNumbers.Count; slot++)
                    {
                        int cameraNumber = renderedCameraNumbers[slot];
                        var additionalPause = new Menu.PauseMenu(manager, game);
                        additionalPause.container.SetPosition(camOffsets[cameraNumber] + PauseOffsetForSlot(CurrentSplitMode, slot, manager.rainWorld.screenSize));
                        manager.sideProcesses.Add(additionalPause);
                    }
                }
                finally
                {
                    inpause = false;
                }
            }
            else if (!(dynamicStyle && !dualDisplays) && renderedCameraNumbers.Count > 0 && !inpause)
            {
                // One rendered camera in classic or dual mode. The menu is built at
                // the world origin, which only camera 0 looks at; when camera 0's
                // player is dead the survivor's camera renders and the menu was
                // invisible on every screen.
                self.container.SetPosition(camOffsets[renderedCameraNumbers[0]]);
            }
            // After the menus are placed, and not for the extra menus built above.
            if (dualDisplays && !inpause) LogPauseDiagnostics(self, "pause menu opened");
            if (!inpause) ArmPauseOverlayCheck(); // measures, 0.8 s from now, whether each view really got the menu
        }

        public void PauseMenu_GrafUpdate(On.Menu.PauseMenu.orig_GrafUpdate orig,
            Menu.PauseMenu self, float timeStacker)
        {
            // A pause menu built before the first layout solve, or one left behind by
            // an earlier session, can still be parented to a per-view HUD stage.
            if (dynamicStyle && !dualDisplays && globalHudStage != null &&
                self?.container != null && self.container.container != globalHudStage)
                MovePauseMenuGlobal(self);
            orig(self, timeStacker);
        }

        private static Vector2 PauseOffsetForSlot(SplitMode mode, int slot, Vector2 screenSize)
        {
            float x = screenSize.x / 4f;
            float y = screenSize.y / 4f;
            if (mode == SplitMode.SplitVertical) return slot == 0 ? new Vector2(x, 0f) : new Vector2(-x, 0f);
            if (mode == SplitMode.SplitHorizontal) return slot == 0 ? new Vector2(0f, -y) : new Vector2(0f, y);
            if (mode == SplitMode.Split3Screen)
            {
                if (slot == 0) return new Vector2(0f, -y);
                return slot == 1 ? new Vector2(x, y) : new Vector2(-x, y);
            }
            if (mode == SplitMode.Split4Screen)
            {
                if (slot == 0) return new Vector2(x, -y);
                if (slot == 1) return new Vector2(-x, -y);
                if (slot == 2) return new Vector2(x, y);
                return new Vector2(-x, y);
            }
            return Vector2.zero;
        }

        /// <summary>
        /// Need to shut down two pause menus
        /// </summary>
        public void PauseMenu_ShutDownProcess(On.Menu.PauseMenu.orig_ShutDownProcess orig, Menu.PauseMenu self)
        {
            if (dualDisplays && !inpause) LogPauseDiagnostics(self, "pause menu closing");
            orig(self);
            if (CurrentSplitMode != SplitMode.NoSplit)
            {
                foreach (int cameraNumber in renderedCameraNumbers.ToArray())
                {
                    RoomCamera roomCamera = self.game.cameras.FirstOrDefault(camera => camera.cameraNumber == cameraNumber);
                    if (roomCamera != null) SetCameraZoom(roomCamera, oldCameraZoom[cameraNumber]);
                }
            }
            // Only ever stop a *different* pause menu. Dynamic mode builds one shared
            // menu, and matching self here would shut this one down a second time.
            var otherpause = self.manager?.sideProcesses?.FirstOrDefault(t => t is Menu.PauseMenu && t != self);
            if (otherpause != null) self.manager.StopSideProcess(otherpause); // removes from sideprocesses list so this isnt a recursive loop
        }

        /// <summary>
        /// Hud is at an offset on splitscreen mode
        /// </summary>
        public void RoomCamera_FireUpSinglePlayerHUD(On.RoomCamera.orig_FireUpSinglePlayerHUD orig, RoomCamera self, Player player)
        {
            orig(self, player);
            AssignCameraToPlayer(self, player);
            MoveCameraHudToOverlay(self);
            MovePlayerNamesToWorld(self);
        }

        public delegate bool delget_ShouldBeCulled(GraphicsModule gm);
        /// <summary>
        /// cull should account for more cams
        /// </summary>
        public bool get_ShouldBeCulled(delget_ShouldBeCulled orig, GraphicsModule gm)
        {
            if (gm.owner.room.game.cameras.Length > 1)
            {
                bool result = orig(gm);
                for (int i = 1; i < gm.owner.room.game.cameras.Length; i++)
                {
                    if (dynamicActive && !renderedCameraNumbers.Contains(gm.owner.room.game.cameras[i].cameraNumber))
                        continue;
                    result = result &&
                    !gm.owner.room.game.cameras[i].PositionCurrentlyVisible(gm.owner.firstChunk.pos, gm.cullRange + ((!gm.culled) ? 100f : 0f), true) &&
                    !gm.owner.room.game.cameras[i].PositionVisibleInNextScreen(gm.owner.firstChunk.pos, (!gm.culled) ? 100f : 50f, true);
                }
                return result;
            }
            return orig(gm);
        }

        /// <summary>
        /// water wont move all vertices if the camera is too far to the right, move everything at startup
        /// </summary>
        public void Water_InitiateSprites(On.Water.orig_InitiateSprites orig, Water self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
        {
            orig(self, sLeaser, rCam);
            // Every mode with a second camera, dual displays included (they stay in
            // NoSplit while rendering two cameras).
            if (rCam.game?.cameras?.Length > 1)
            {
                var camPos = rCam.pos + rCam.offset;
                float y = -10f;
                if (self.cosmeticLowerBorder > -1f)
                {
                    y = self.cosmeticLowerBorder - camPos.y;
                }
                Vector2 top = new Vector2(1400f, self.fWaterLevel - camPos.y + self.cosmeticSurfaceDisplace);
                Vector2 bottom = new Vector2(1400f, y);
                for (int i = 0; i < self.pointsToRender; i++)
                {
                    int num3 = i * 2;

                    (sLeaser.sprites[0] as WaterTriangleMesh).MoveVertice(num3, top);
                    (sLeaser.sprites[0] as WaterTriangleMesh).MoveVertice(num3 + 1, top);

                    (sLeaser.sprites[1] as WaterTriangleMesh).MoveVertice(num3, top);
                    (sLeaser.sprites[1] as WaterTriangleMesh).MoveVertice(num3 + 1, bottom);
                }
            }
        }

        /// <summary>
        /// implements following player for cam2
        /// </summary>
        public void ShortcutHandler_Update(ILContext il)
        {
            var c = new ILCursor(il);
            int indexLoc = 0;

            // this is loading room if creature followed by camera
            if (c.TryGotoNext(MoveType.Before,
                i => i.MatchCallvirt<AbstractCreature>("FollowedByCamera"),
                i => i.MatchBrfalse(out _),
                i => i.MatchLdarg(0),
                i => i.MatchLdfld<ShortcutHandler>("betweenRoomsWaitingLobby"),
                i => i.MatchLdloc(out indexLoc)
                ))
            {
                c.Index++;
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Ldloc, indexLoc);
                // was A && !B
                // becomes (A || A2) && !B
                // b param here is A
                c.EmitDelegate<Func<bool, ShortcutHandler, int, bool>>((b, sc, k) =>
                {
                    return b || sc.game.cameras.Any(c=> sc.betweenRoomsWaitingLobby[k].creature.abstractCreature.FollowedByCamera(c.cameraNumber));
                });
            }
            else Logger.LogError(new Exception("Couldn't IL-hook ShortcutHandler_Update part 1 FollowedByCamera from SplitScreenMod")); // deffendisve progrmanig

            // this is actually loading the room, should add to tracked rooms, cmon game
            if (c.TryGotoNext(MoveType.Before,
                i => i.MatchCallvirt<World>("ActivateRoom")
                ))
            {
                c.Remove();
                c.EmitDelegate<Action<World, AbstractRoom>>((w, r) =>
                {
                    TrackShortcutDestination(w, r);
                });
            }
            else Logger.LogError(new Exception("Couldn't IL-hook ShortcutHandler_Update part 2 AddNewTrackedRoom from SplitScreenMod")); // deffendisve progrmanig


            // this is moving the camera if the creature is followed by camera
            ILLabel jump = null;
            if (c.TryGotoNext(MoveType.Before,
                i => i.MatchCallvirt<AbstractCreature>("FollowedByCamera"),
                i => i.MatchBrfalse(out jump),
                i => i.MatchLdarg(0),
                i => i.MatchLdfld<ShortcutHandler>("game")
                ))
            {
                c.GotoLabel(jump);

                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Ldloc, indexLoc);
                c.EmitDelegate<Action<ShortcutHandler, int>>((sc, k) =>
                {
                    for (int i = 1; i < sc.game.cameras.Length; i++)
                    {
                        if (sc.betweenRoomsWaitingLobby[k].creature.abstractCreature.FollowedByCamera(i))
                        {
                            sc.game.cameras[i].MoveCamera(sc.betweenRoomsWaitingLobby[k].room.realizedRoom, sc.betweenRoomsWaitingLobby[k].room.nodes[sc.betweenRoomsWaitingLobby[k].entranceNode].viewedByCamera);
                        }
                    }
                });
            }
            else Logger.LogError(new Exception("Couldn't IL-hook ShortcutHandler_Update part 3 MoveCamera from SplitScreenMod")); // deffendisve progrmanig
        }

        /// <summary>
        /// activating a room should add it to the tracked active rooms
        /// </summary>
        public void ShortcutHandler_SuckInCreature(ILContext il)
        {
            var c = new ILCursor(il);
            // this is actually loading and entering the room, should add to tracked rooms, cmon game
            if (c.TryGotoNext(MoveType.Before,
                i => i.MatchCallvirt<World>("ActivateRoom")
                ))
            {
                c.Remove();
                c.EmitDelegate<Action<World, AbstractRoom>>((w, r) =>
                {
                    TrackShortcutDestination(w, r);
                });
            }
            else Logger.LogError(new Exception("Couldn't IL-hook ShortcutHandler_SuckInCreature AddNewTrackedRoom from SplitScreenMod")); // deffendisve progrmanig
        }

        /// <summary>
        /// fixes draw parameter changing on repeated calls to init, would array-oob on the previous leaser
        /// </summary>
        public void PoleMimicGraphics_InitiateSprites(ILContext il)
        {
            var c = new ILCursor(il);

            if (c.TryGotoNext(MoveType.Before,
                i => i.MatchStfld<PoleMimicGraphics>("leafPairs")
                ))
            {
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Func<int, PoleMimicGraphics, int>>((was, pole) =>
                {
                    return pole.leafPairs > 0 ? pole.leafPairs : was;
                });
            }
            else Logger.LogError(new Exception("Couldn't IL-hook PoleMimicGraphics_InitiateSprites from SplitScreenMod")); // deffendisve progrmanig
        }

        /// <summary>
        /// fixes fsprite names so they can use different textures
        /// </summary>
        public void RoomCamera_ctor(ILContext il)
        {
            var c = new ILCursor(il);
            if (c.TryGotoNext(MoveType.Before,
                i => i.MatchLdstr("LevelTexture"),
                i => i.MatchLdcI4(1),
                i => i.MatchNewobj<FSprite>()
                ))
            {
                c.Index++;
                c.Emit(OpCodes.Ldarg_2);
                c.EmitDelegate<Func<string, int, string>>((name, camnum) =>
                {
                    return camnum > 0 ? name + camnum.ToString() : name;
                });
            }
            else Logger.LogError(new Exception("Couldn't IL-hook RoomCamera_ctor from SplitScreenMod")); // deffendisve progrmanig
        }

        /// <summary>
        /// proper scroll and boundaries for our custom split modes. Originally only supported horiz split
        /// </summary>
        public void RoomCamera_Update1(ILContext il)
        {
            var c = new ILCursor(il);
            ILLabel jump = null;
            if (c.TryGotoNext(MoveType.Before,
                i => i.MatchLdarg(0),
                i => i.MatchLdfld<RoomCamera>("splitScreenMode"),
                i => i.MatchBrfalse(out jump),
                i => i.MatchLdarg(0),
                i => i.MatchLdfld<RoomCamera>("followAbstractCreature"),
                i => i.MatchCallvirt<AbstractCreature>("get_realizedCreature")
                ))
            {
                c.GotoLabel(jump);
                c.MoveAfterLabels();
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Action<RoomCamera>>((rc) =>
                {
                    // Dynamic style keeps the vanilla camera: it stays on its prebaked
                    // screen and the compositor pans the rendered image instead.
                    if (dynamicStyle && !dualDisplays) return;
                    if (cameraZoomed[rc.cameraNumber])
                        return;
                    if (CurrentSplitMode == SplitMode.SplitHorizontal)
                    {
                        float pad = rc.sSize.y / 4f;
                        if (rc.followAbstractCreature != null && rc.followAbstractCreature.realizedCreature is Creature cr)
                        {
                            if (!cr.inShortcut) rc.pos.y = SmoothCameraAxis(rc.pos.y, rc.followAbstractCreature.realizedCreature.mainBodyChunk.pos.y - 2 * pad, rc.sSize.y);
                            else
                            {
                                Vector2? vector = rc.room.game.shortcuts.OnScreenPositionOfInShortCutCreature(rc.room, cr);
                                if (vector != null)
                                {
                                    rc.pos.y = SmoothCameraAxis(rc.pos.y, vector.Value.y - 2 * pad, rc.sSize.y);
                                }
                            }
                            rc.pos.y += rc.followCreatureInputForward.y * 2f;
                        }
                    }
                    else if (CurrentSplitMode == SplitMode.SplitVertical)
                    {
                        float pad = rc.sSize.x / 4f;
                        if (rc.followAbstractCreature != null && rc.followAbstractCreature.realizedCreature is Creature cr)
                        {
                            if (!cr.inShortcut) rc.pos.x = SmoothCameraAxis(rc.pos.x, rc.followAbstractCreature.realizedCreature.mainBodyChunk.pos.x - 2 * pad, rc.sSize.x);
                            else
                            {
                                Vector2? vector = rc.room.game.shortcuts.OnScreenPositionOfInShortCutCreature(rc.room, cr);
                                if (vector != null)
                                {
                                    rc.pos.x = SmoothCameraAxis(rc.pos.x, vector.Value.x - 2 * pad, rc.sSize.x);
                                }
                            }
                            rc.pos.x += rc.followCreatureInputForward.x * 2f;
                        }
                    }
                    else if(IsGridSplit(CurrentSplitMode))
                    {
                        float pad = rc.sSize.x / 4f;
                        float pad2 = rc.sSize.y / 4f;
                        if (rc.followAbstractCreature != null && rc.followAbstractCreature.realizedCreature is Creature cr)
                        {
                            if (!cr.inShortcut)
                            {
                                rc.pos.x = SmoothCameraAxis(rc.pos.x, rc.followAbstractCreature.realizedCreature.mainBodyChunk.pos.x - 2 * pad, rc.sSize.x);
                                rc.pos.y = SmoothCameraAxis(rc.pos.y, rc.followAbstractCreature.realizedCreature.mainBodyChunk.pos.y - 2 * pad2, rc.sSize.y);
                            }
                            else
                            {
                                Vector2? vector = rc.room.game.shortcuts.OnScreenPositionOfInShortCutCreature(rc.room, cr);
                                if (vector != null)
                                {
                                    rc.pos.x = SmoothCameraAxis(rc.pos.x, vector.Value.x - 2 * pad, rc.sSize.x);
                                    rc.pos.y = SmoothCameraAxis(rc.pos.y, vector.Value.y - 2 * pad2, rc.sSize.y);
                                }
                            }
                            rc.pos.x += rc.followCreatureInputForward.x * 2f;
                            rc.pos.y += rc.followCreatureInputForward.y * 2f;
                        }
                    }
                });

                try
                {
                    c.GotoNext(MoveType.After, i => i.MatchCallOrCallvirt<RoomCamera>("get_hDisplace")); // IL_00A7

                    c.Emit(OpCodes.Ldarg_0); // RoomCamera
                    c.EmitDelegate<Func<float, RoomCamera, float>>((v, rc) =>
                    {
                        if (dynamicStyle && !dualDisplays) return v;
                        if ((CurrentSplitMode == SplitMode.SplitVertical || IsGridSplit(CurrentSplitMode)) && !cameraZoomed[rc.cameraNumber])
                        {
                            return v - rc.sSize.x / 4f;
                        }
                        return v;
                    });

                    c.GotoNext(MoveType.After, i => i.MatchCallOrCallvirt<RoomCamera>("get_hDisplace")); // IL_00CF

                    c.Emit(OpCodes.Ldarg_0); // RoomCamera
                    c.EmitDelegate<Func<float, RoomCamera, float>>((v, rc) =>
                    {
                        if (dynamicStyle && !dualDisplays) return v;
                        if ((CurrentSplitMode == SplitMode.SplitVertical || IsGridSplit(CurrentSplitMode)) && !cameraZoomed[rc.cameraNumber])
                        {
                            return v + rc.sSize.x / 4f;
                        }
                        return v;
                    });

                    c.GotoNext(MoveType.After, i => i.MatchLdfld<RoomCamera>("splitScreenMode"), i => i.MatchBrtrue(out _)); // IL_0116
                    c.MoveAfterLabels();
                    c.Emit(OpCodes.Ldarg_0); // RoomCamera
                    c.EmitDelegate<Func<float, RoomCamera, float>>((v, rc) =>
                    {
                        if (dynamicStyle && !dualDisplays) return v;
                        if ((CurrentSplitMode == SplitMode.SplitHorizontal || IsGridSplit(CurrentSplitMode)) && !cameraZoomed[rc.cameraNumber])
                        {
                            return v - rc.sSize.y / 4f;
                        }
                        return v;
                    });


                    c.GotoNext(MoveType.After, i => i.MatchLdfld<RoomCamera>("splitScreenMode"), i => i.MatchBrtrue(out _)); // IL_014C
                    c.MoveAfterLabels();
                    c.Emit(OpCodes.Ldarg_0); // RoomCamera
                    c.EmitDelegate<Func<float, RoomCamera, float>>((v, rc) =>
                    {
                        if (dynamicStyle && !dualDisplays) return v;
                        if ((CurrentSplitMode == SplitMode.SplitHorizontal || IsGridSplit(CurrentSplitMode)) && !cameraZoomed[rc.cameraNumber])
                        {
                            return v + rc.sSize.y / 4f;
                        }
                        return v;
                    });
                }
                catch (Exception e)
                {
                    Logger.LogError(new Exception("Couldn't IL-hook RoomCamera_Update1 from SplitScreenMod, inner spot", e)); // deffendisve progrmanig
                    throw;
                }
            }
            else Logger.LogError(new Exception("Couldn't IL-hook RoomCamera_Update1 from SplitScreenMod")); // deffendisve progrmanig

            // I give up, let jolly be broken
            //// Jolly do NOT touch the camera I hecking swear you don't know what you're doing
            try
            {
                c.Index = 0;
                ILLabel donot = null;
                c.GotoNext(MoveType.After, // this.game.aliveplayers.count > 0 && this.game.firstaliveplayer != null
                    i => i.MatchCallOrCallvirt<RainWorldGame>("get_FirstAlivePlayer"),
                    i => i.MatchStloc(out _),
                    i => i.MatchLdloc(out _),
                    i => i.MatchBrfalse(out donot)
                    );

                c.Emit(OpCodes.Br, donot); // just don't
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                throw;
            }
        }

        /// <summary>
        /// proper scroll and boundaries for our custom split modes. Originally only supported horiz split
        /// </summary>
        public void RoomCamera_DrawUpdate1(ILContext il)
        {
            var c = new ILCursor(il);
            ILLabel jump = null;
            ILLabel jump2 = null;
            if (c.TryGotoNext(MoveType.Before,
                i => i.MatchLdarg(0),
                i => i.MatchLdfld<RoomCamera>("voidSeaMode"),
                i => i.MatchBrtrue(out jump),
                i => i.MatchLdstr(out _)
                ))
            {
                var b = c.Index;
                c.GotoLabel(jump);
                if (c.Prev.MatchBr(out jump2))
                {
                    try
                    {
                        c.Index = b + 3; // NOT void Sea

                        c.GotoNext(MoveType.After, i => i.MatchCallOrCallvirt<RoomCamera>("get_hDisplace")); // IL_00A7

                        c.Emit(OpCodes.Ldarg_0); // RoomCamera
                        c.EmitDelegate<Func<float, RoomCamera, float>>((v, rc) =>
                        {
                            if (dynamicStyle && !dualDisplays) return v;
                            if ((CurrentSplitMode == SplitMode.SplitVertical || IsGridSplit(CurrentSplitMode)) && !cameraZoomed[rc.cameraNumber])
                            {
                                return v - rc.sSize.x / 4f;
                            }
                            return v;
                        });

                        c.GotoNext(MoveType.After, i => i.MatchCallOrCallvirt<RoomCamera>("get_hDisplace")); // IL_00CF

                        c.Emit(OpCodes.Ldarg_0); // RoomCamera
                        c.EmitDelegate<Func<float, RoomCamera, float>>((v, rc) =>
                        {
                            if (dynamicStyle && !dualDisplays) return v;
                            if ((CurrentSplitMode == SplitMode.SplitVertical || IsGridSplit(CurrentSplitMode)) && !cameraZoomed[rc.cameraNumber])
                            {
                                return v + rc.sSize.x / 4f;
                            }
                            return v;
                        });

                        c.GotoNext(MoveType.After, i => i.MatchLdfld<RoomCamera>("splitScreenMode"), i => i.MatchBrtrue(out _)); // IL_0116
                        c.MoveAfterLabels();
                        c.Emit(OpCodes.Ldarg_0); // RoomCamera
                        c.EmitDelegate<Func<float, RoomCamera, float>>((v, rc) =>
                        {
                            if (dynamicStyle && !dualDisplays) return v;
                            if ((CurrentSplitMode == SplitMode.SplitHorizontal || IsGridSplit(CurrentSplitMode)) && !cameraZoomed[rc.cameraNumber])
                            {
                                return v - rc.sSize.y / 4f;
                            }
                            return v;
                        });


                        c.GotoNext(MoveType.After, i => i.MatchLdfld<RoomCamera>("splitScreenMode"), i => i.MatchBrtrue(out _)); // IL_014C
                        c.MoveAfterLabels();
                        c.Emit(OpCodes.Ldarg_0); // RoomCamera
                        c.EmitDelegate<Func<float, RoomCamera, float>>((v, rc) =>
                        {
                            if (dynamicStyle && !dualDisplays) return v;
                            if ((CurrentSplitMode == SplitMode.SplitHorizontal || IsGridSplit(CurrentSplitMode)) && !cameraZoomed[rc.cameraNumber])
                            {
                                return v + rc.sSize.y / 4f;
                            }
                            return v;
                        });
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(new Exception("Couldn't IL-hook RoomCamera_DrawUpdate1 from SplitScreenMod, inner inner spot", e)); // deffendisve progrmanig
                        throw;
                    }
                }
                else Logger.LogError(new Exception("Couldn't IL-hook RoomCamera_DrawUpdate1 from SplitScreenMod, inner spot")); // deffendisve progrmanig
            }
            else Logger.LogError(new Exception("Couldn't IL-hook RoomCamera_DrawUpdate1 from SplitScreenMod")); // deffendisve progrmanig
        }
    }
}
