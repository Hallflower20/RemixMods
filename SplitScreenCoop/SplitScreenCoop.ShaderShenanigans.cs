
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        internal static bool restoringShaderState;
        internal static int lastCompositorClearFrame = -1;
        /// <summary>
        /// The room whose Update is running, when no RoomCamera call is in scope.
        /// </summary>
        internal static Room curRoom;
        private static readonly CameraListener[] shaderRecordScratch = new CameraListener[4];

        /// <summary>
        /// Which listeners a Shader.SetGlobal call belongs to. Inside a RoomCamera call
        /// that is exactly one camera. Outside it, a room object updating from
        /// Room.Update - AboveCloudsView and RoofTopView in Chimney Canopy, DustWave,
        /// LightningMaker, the grime and rim-fix floats - still writes per-room values
        /// into a global. Those were recorded against no camera at all, so every view
        /// rendered with whichever room updated last and the second view showed the
        /// wrong palette. Attribute them to every camera actually showing that room.
        /// Fills a reusable buffer; these hooks run hundreds of times per frame.
        /// </summary>
        private static int CollectShaderRecipients()
        {
            if (curCamera >= 0)
            {
                if (curCamera >= cameraListeners.Length || cameraListeners[curCamera] == null) return 0;
                shaderRecordScratch[0] = cameraListeners[curCamera];
                return 1;
            }
            RainWorldGame game = curRoom?.game;
            if (game?.cameras == null) return 0;
            int found = 0;
            foreach (RoomCamera camera in game.cameras)
            {
                if (camera == null || camera.room != curRoom) continue;
                int number = camera.cameraNumber;
                if (number < 0 || number >= cameraListeners.Length || cameraListeners[number] == null) continue;
                if (found >= shaderRecordScratch.Length) break;
                shaderRecordScratch[found++] = cameraListeners[number];
            }
            return found;
        }

        private static int CollectShaderRecipients(int nameID)
        {
            int found = CollectShaderRecipients();
            AuditShaderWrite(nameID, null, found);
            return found;
        }

        private static int CollectShaderRecipients(string name)
        {
            int found = CollectShaderRecipients();
            AuditShaderWrite(0, name, found);
            return found;
        }

        // ---- Shader write audit --------------------------------------------------
        // Every per-camera shader difference so far came from a global written
        // somewhere the mod did not attribute to a camera: from Room.Update using one
        // camera's position (BlizzardGraphics' _tileCorrection), from a constructor,
        // from a room object updating from cameras[0]. The first write of each global
        // outside camera scope is logged with the vanilla method that made it, so the
        // next log lists every candidate instead of the screen showing it.
        private static readonly Dictionary<int, byte> auditedShaderWrites = new Dictionary<int, byte>();
        private static Dictionary<int, string> shaderPropertyNames;

        private static string ShaderPropertyName(int id)
        {
            if (shaderPropertyNames == null)
            {
                shaderPropertyNames = new Dictionary<int, string>();
                foreach (var field in typeof(RainWorld).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    if (field.FieldType == typeof(int) && field.Name.StartsWith("ShadProp"))
                        shaderPropertyNames[(int)field.GetValue(null)] = field.Name;
            }
            string name;
            return shaderPropertyNames.TryGetValue(id, out name) ? name : "id " + id;
        }

        private static void AuditShaderWrite(int id, string name, int found)
        {
            byte scope = curCamera >= 0 ? (byte)1 : curRoom != null ? (byte)2 : (byte)4;
            if (scope == 1) return;
            if (name != null) id = Shader.PropertyToID(name);
            byte seen;
            auditedShaderWrites.TryGetValue(id, out seen);
            if ((seen & scope) != 0) return;
            auditedShaderWrites[id] = (byte)(seen | scope);
            string site = "?";
            try
            {
                var trace = new System.Diagnostics.StackTrace(2, false);
                for (int i = 0; i < trace.FrameCount; i++)
                {
                    var method = trace.GetFrame(i).GetMethod();
                    string type = method?.DeclaringType?.FullName ?? "";
                    if (type.StartsWith("SplitScreenCoop") || type.StartsWith("UnityEngine.Shader") || type.StartsWith("MonoMod")) continue;
                    site = (type.Length > 0 ? type + "." : "") + method.Name;
                    break;
                }
            }
            catch { }
            sLogger?.LogInfo($"[ShaderAudit] frame={Time.frameCount} global {name ?? ShaderPropertyName(id)} first written outside camera scope ({(scope == 2 ? "room " + (curRoom?.abstractRoom?.name ?? "?") : "no room")}) by {site}; cameras receiving it={found}");
        }

        public void Room_Update(On.Room.orig_Update orig, Room self)
        {
            Room previous = curRoom;
            try
            {
                curRoom = self;
                orig(self);
            }
            finally
            {
                curRoom = previous;
            }
        }

        /// <summary>
        /// Room.Loaded builds the room's effect objects. RoofTopView and its relatives
        /// write per-room globals from their constructors, some of them only there
        /// (_SceneOrigoPosition, which places the background city). No camera shows
        /// a room while it loads, so those writes had no recipient and the last room
        /// to load won for every camera. Scope the load like an update: the writes go
        /// into the room's own record and reach each camera as it arrives (see
        /// RoomCamera_ChangeRoom).
        /// </summary>
        public void Room_Loaded(On.Room.orig_Loaded orig, Room self)
        {
            Room previous = curRoom;
            try
            {
                curRoom = self;
                orig(self);
            }
            finally
            {
                curRoom = previous;
            }
        }

        /// <summary>
        /// Shader globals written on behalf of a room, kept so a camera that starts
        /// showing the room later inherits them. Room.Update rewrites most of them
        /// every tick, but the ones written once at load would otherwise be lost.
        /// </summary>
        internal sealed class RoomShaderState
        {
            public readonly Dictionary<int, Color> colors = new Dictionary<int, Color>();
            public readonly Dictionary<int, Vector4> vectors = new Dictionary<int, Vector4>();
            public readonly Dictionary<int, float> floats = new Dictionary<int, float>();
            public readonly Dictionary<int, Texture> textures = new Dictionary<int, Texture>();
            public readonly Dictionary<int, Vector4[]> vectorArrays = new Dictionary<int, Vector4[]>();
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Room, RoomShaderState> roomShaderStates =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Room, RoomShaderState>();

        private static RoomShaderState CurrentRoomRecord()
        {
            if (curCamera >= 0 || curRoom == null) return null;
            return roomShaderStates.GetValue(curRoom, room => new RoomShaderState());
        }

        public void RoomCamera_ChangeRoom(On.RoomCamera.orig_ChangeRoom orig, RoomCamera self, Room newRoom, int cameraPosition)
        {
            orig(self, newRoom, cameraPosition);
            RoomShaderState record;
            if (newRoom == null || self.cameraNumber < 0 || self.cameraNumber >= cameraListeners.Length ||
                cameraListeners[self.cameraNumber] == null || !roomShaderStates.TryGetValue(newRoom, out record)) return;
            CameraListener listener = cameraListeners[self.cameraNumber];
            foreach (var kv in record.colors) listener.ShaderColors[kv.Key] = kv.Value;
            foreach (var kv in record.vectors) listener.ShaderVectors[kv.Key] = kv.Value;
            foreach (var kv in record.floats) listener.ShaderFloats[kv.Key] = kv.Value;
            foreach (var kv in record.textures) listener.ShaderTextures[kv.Key] = kv.Value;
            foreach (var kv in record.vectorArrays) listener.ShaderVectorArrays[kv.Key] = kv.Value;
        }
        //Envelop camera-related stuff that does shader.set calls so we know the calling camera index and can re-apply those in a sane way later
        //not 100% robust (currently we don't store "global" assignments that one camera might choose to overwrite or not)

        public void RoomCamera_MoveCamera_Room_int(On.RoomCamera.orig_MoveCamera_Room_int orig, RoomCamera self, Room newRoom, int camPos)
        {
            var prev = curCamera;
            try
            {
                curCamera = self.cameraNumber;
                orig(self, newRoom, camPos);
                NoteRoomCameraMoved(self, "MoveCamera(room)");
                CaptureRoomCameraShaderKeywords(self);
            }
            finally
            {
                curCamera = prev;
            }
        }

        public void RoomCamera_MoveCamera_int(On.RoomCamera.orig_MoveCamera_int orig, RoomCamera self, int camPos)
        {
            var prev = curCamera;
            try
            {
                curCamera = self.cameraNumber;
                orig(self, camPos);
                NoteRoomCameraMoved(self, "MoveCamera(position)");
                CaptureRoomCameraShaderKeywords(self);
            }
            finally
            {
                curCamera = prev;
            }
        }

        public void RoomCamera_DrawUpdate(On.RoomCamera.orig_DrawUpdate orig, RoomCamera self, float timeStacker, float timeSpeed)
        {
            var prev = curCamera;
            try
            {
                curCamera = self.cameraNumber;
                orig(self, timeStacker, timeSpeed);
                NoteRoomCameraDrawn(self);
                // This runs inside the game's per-camera draw loop. A throw here
                // would skip every later camera's draw for the frame, so it is
                // contained, and it is skipped entirely while a draw stall is being
                // diagnosed (see DetectDrawStall).
                if (drawPathSafeMode) return;
                try
                {
                    CaptureRoomCameraShaderKeywords(self);
                    OffsetHud(self);
                    RouteGlobalMeters(self);
                }
                catch (Exception error) { LogHookError("RoomCamera_DrawUpdate.post", error); }
            }
            finally
            {
                curCamera = prev;
            }
        }

        /// <summary>
        /// Every RoomCamera runs DrawSprites for every drawable in its room each
        /// rendered frame, whether or not its Unity camera is on. Three players in
        /// one room were three full passes over every object per frame, two of them
        /// drawing into textures nobody displays (rot spores even dispatch their
        /// particle compute per pass). Skip the object draw for cameras that are not
        /// rendering. The camera's own DrawUpdate, its HUD, shader capture and leaser
        /// bookkeeping (deleteMeNextFrame is set elsewhere) still run, and a camera
        /// that starts rendering gets its DrawSprites before the render in the same
        /// frame: rendered cameras are decided in the tick, before GrafUpdate.
        /// </summary>
        public void SpriteLeaser_Update(On.RoomCamera.SpriteLeaser.orig_Update orig, RoomCamera.SpriteLeaser self,
            float timeStacker, RoomCamera rCam, Vector2 camPos)
        {
            if (!drawPathSafeMode && rCam != null && rCam.game?.cameras?.Length > 1 &&
                renderedCameraNumbers.Count > 0 && !CameraRendersThisFrame(rCam.cameraNumber) &&
                !DrawSpritesRetiresLeaser(self.drawableObject, rCam))
                return;
            orig(self, timeStacker, rCam, camPos);
            if (self.maskSources != null && self.maskSources.Length > 0 && !self.deleteMeNextFrame)
                RecordLeaserMaskSources(self, rCam);
        }

        /// <summary>
        /// Vanilla drawables retire their own leaser inside DrawSprites: nearly every
        /// implementation ends with `if (slatedForDeletetion || room != rCam.room)
        /// sLeaser.CleanSpritesAndRemove()`. Skipping DrawSprites on a camera that is
        /// not rendering therefore leaked one leaser, sprites included, for every
        /// object that died or left the room while that camera was off (eighth
        /// playtest: 369 leasers on camera 0 against 557 and 745 on the two cameras
        /// that had been off, all in one room). Let DrawSprites run whenever it would
        /// clean up, and always for drawables whose owner cannot be read here.
        /// </summary>
        private static bool DrawSpritesRetiresLeaser(IDrawable drawable, RoomCamera rCam)
        {
            if (drawable is UpdatableAndDeletable deletable)
                return deletable.slatedForDeletetion || deletable.room != rCam.room;
            if (drawable is GraphicsModule graphics)
                return graphics.owner == null || graphics.owner.slatedForDeletetion || graphics.owner.room != rCam.room;
            return true;
        }

        public void RoomCamera_Update(On.RoomCamera.orig_Update orig, RoomCamera self)
        {
            var prev = curCamera;
            try
            {
                curCamera = self.cameraNumber;
                // Jolly's part of RoomCamera.Update contains, in this order,
                //   if (cutscenePlayer == null && coopRippleDimensionPlayer != null)
                //       followAbstractCreature = coopRippleDimensionPlayer;
                //   foreach player: if (game.ActiveRippleLayer != 0 && player is the Watcher)
                //       coopRippleDimensionPlayer = player;   else it is cleared
                // With one camera that means "follow the Watcher through the ripple
                // layer". With a camera per player it dragged EVERY camera to that one
                // player's room each tick, and EnsureStableCameraAssignments dragged it
                // back on the next: two room moves and two level images per tick (log of
                // 2026-09-18: 120 forced resyncs, camera 0 flipping between WRFA_A21 and
                // WRFA_C11 every three frames). Clear it before vanilla reads it. Vanilla
                // sets it again further down the same call, so JollyMeter and Player,
                // which read cameras[0]'s copy, see what they always saw.
                if (self.coopRippleDimensionPlayer != null && self.game?.cameras != null && self.game.Players != null &&
                    self.game.cameras.Length > 1 && self.game.cameras.Length >= self.game.Players.Count)
                    self.coopRippleDimensionPlayer = null;
                orig(self);
                NoteRoomCameraUpdated(self);
                try { if (!drawPathSafeMode) CaptureRoomCameraShaderKeywords(self); }
                catch (Exception error) { LogHookError("RoomCamera_Update.post", error); }
            }
            finally
            {
                curCamera = prev;
            }
        }

        public void RoomCamera_ApplyPalette(On.RoomCamera.orig_ApplyPalette orig, RoomCamera self)
        {
            var prev = curCamera;
            try
            {
                curCamera = self.cameraNumber;
                orig(self);
                CaptureRoomCameraShaderKeywords(self);
            }
            finally
            {
                curCamera = prev;
            }
        }

        public void RoomCamera_ApproximateLightmap(On.RoomCamera.orig_ApproximateLightmap orig, RoomCamera self)
        {
            var prev = curCamera;
            try
            {
                curCamera = self.cameraNumber;
                orig(self);
            }
            finally
            {
                curCamera = prev;
            }
        }

        public void RoomCamera_WarpMoveCameraActual(On.RoomCamera.orig_WarpMoveCameraActual orig,
            RoomCamera self, Room newRoom, int camPos)
        {
            var prev = curCamera;
            try
            {
                curCamera = self.cameraNumber;
                orig(self, newRoom, camPos);
                NoteRoomCameraMoved(self, "WarpMoveCameraActual");
                CaptureRoomCameraShaderKeywords(self);
                // Vanilla precasts the warp destination texture (WarpMoveCameraPrecast)
                // for the camera that triggered the warp. A camera that was not precast
                // leaves here with loadingRoom set and nothing that will ever apply it.
                // With a room it recovers through vanilla's "camera needs to move"
                // check in Update; without one (a session resumed from a warp) it sat
                // at room=null for a whole playtest, could never share a screen key
                // with anyone, and the layout stayed split around an empty cell. Move
                // it the ordinary way instead.
                if (!self.warpApplyPosChangeWhenTextureIsLoaded && newRoom != null)
                {
                    int position = camPos >= 0 ? camPos : self.loadingWarpCameraPos;
                    Logger.LogInfo($"[CameraMove] frame={Time.frameCount} cam={self.cameraNumber} warp move without a precast texture; moving normally to {newRoom.abstractRoom?.name} position={position}");
                    self.MoveCamera(newRoom, position);
                }
            }
            finally
            {
                curCamera = prev;
            }
        }

        public void RoomCamera_BlankWarpPointHoldFrame(On.RoomCamera.orig_BlankWarpPointHoldFrame orig,
            RoomCamera self)
        {
            var prev = curCamera;
            try
            {
                curCamera = self.cameraNumber;
                orig(self);
            }
            finally
            {
                curCamera = prev;
            }
        }

        private static bool propagatingCameraMode;

        /// <summary>
        /// Room.cs raises ghost (echo) and rot palette modes on cameras[0] only, e.g.
        /// after DestroyWeaverPresenceInRoom, and Watcher's rot updates likewise. Any
        /// other camera showing the same room would keep a stale mode and render the
        /// room with a different palette. Mirror each update onto every camera that
        /// currently shows that room, with its own camera position.
        /// </summary>
        public void RoomCamera_UpdateGhostMode(On.RoomCamera.orig_UpdateGhostMode orig, RoomCamera self, Room newRoom, int newCamPos)
        {
            var prev = curCamera;
            try
            {
                curCamera = self.cameraNumber;
                orig(self, newRoom, newCamPos);
            }
            finally
            {
                curCamera = prev;
            }
            if (propagatingCameraMode || newRoom == null || self.game?.cameras == null) return;
            try
            {
                propagatingCameraMode = true;
                foreach (RoomCamera other in self.game.cameras)
                    if (other != null && other != self && other.room == newRoom)
                        other.UpdateGhostMode(newRoom, other.currentCameraPosition);
            }
            finally
            {
                propagatingCameraMode = false;
            }
        }

        /// <summary>
        /// Vanilla UpdateRotMode has two single-camera assumptions baked in. Room.Update
        /// calls it on cameras[0] for every room that any camera views, so camera 0
        /// was handed other rooms' rot amounts; and it always writes the rot effect
        /// colours into cameras[0]'s palette textures, so a second camera showing the
        /// rot never got them and the red goop rendered black. Re-implemented: only a
        /// camera actually showing the room takes the update, and it writes its own
        /// palettes.
        /// </summary>
        public void RoomCamera_UpdateRotMode(On.RoomCamera.orig_UpdateRotMode orig, RoomCamera self, Room room, float amount)
        {
            if (room == null || self.game?.cameras == null) { orig(self, room, amount); return; }
            if (self.room == room) ApplyRotMode(self, room, amount);
            if (propagatingCameraMode) return;
            try
            {
                propagatingCameraMode = true;
                foreach (RoomCamera other in self.game.cameras)
                    if (other != null && other != self && other.room == room)
                        ApplyRotMode(other, room, amount);
            }
            finally
            {
                propagatingCameraMode = false;
            }
        }

        private static void ApplyRotMode(RoomCamera camera, Room room, float amount)
        {
            var prev = curCamera;
            try
            {
                curCamera = camera.cameraNumber;
                bool changed = false;
                if (camera.rotMode != amount)
                {
                    camera.rotMode = amount;
                    changed = true;
                }
                if (room.roomSettings.EffectColorB != 21 && amount > 0f)
                {
                    room.roomSettings.EffectColorB = 21;
                    camera.usingSentientRotEffectColor = true;
                    changed = true;
                }
                else if (amount <= 0f && camera.usingSentientRotEffectColor)
                {
                    camera.usingSentientRotEffectColor = false;
                    changed = true;
                }
                if (changed)
                    camera.ApplyEffectColorsToAllPaletteTextures(room.roomSettings.EffectColorA, room.roomSettings.EffectColorB);
            }
            finally
            {
                curCamera = prev;
            }
        }

        /// <summary>
        /// UpdateSnowLight blits the snow data (LevelSnowShader) during the update,
        /// outside any camera render, from the globals bound at that moment: replay
        /// this camera's own set first (level texture, palette, snow sources, rect).
        /// Graphics.Blit also leaves its destination as the active render target;
        /// restore it so nothing drawn to "the active target" afterwards lands in
        /// this camera's snow texture.
        /// </summary>
        public void RoomCamera_UpdateSnowLight(On.RoomCamera.orig_UpdateSnowLight orig, RoomCamera self)
        {
            RenderTexture previousTarget = RenderTexture.active;
            var prev = curCamera;
            try
            {
                curCamera = self.cameraNumber;
                if (self.cameraNumber >= 0 && self.cameraNumber < cameraListeners.Length &&
                    cameraListeners[self.cameraNumber] is CameraListener l)
                    l.OnPreRender();
                // SnowSource.visibility is one field per source, and SnowSource_Update
                // sets it when ANY camera in the room can see the source. This method
                // packs every source marked visible, at most 20, relative to THIS
                // camera's screen: a camera spent slots on sources only another screen
                // could see, and past roughly three screens away the 0..1 position
                // encoding (EncodeFloatRG) wraps round and the source lands on this
                // screen as a phantom. Give the blit this camera's own answer.
                List<MoreSlugcats.SnowSource> sources = self.room?.snowSources;
                int[] shared = null;
                if (sources != null && self.room.cameraPositions != null && self.currentCameraPosition >= 0 &&
                    self.currentCameraPosition < self.room.cameraPositions.Length)
                {
                    shared = new int[sources.Count];
                    for (int i = 0; i < shared.Length; i++)
                    {
                        shared[i] = sources[i].visibility;
                        if (sources[i].room == self.room)
                            sources[i].visibility = sources[i].CheckVisibility(self.currentCameraPosition);
                    }
                }
                try { orig(self); }
                finally
                {
                    if (shared != null)
                        for (int i = 0; i < shared.Length && i < sources.Count; i++) sources[i].visibility = shared[i];
                }
                RememberVisibleSnow(self);
            }
            finally
            {
                curCamera = prev;
                RenderTexture.active = previousTarget;
            }
        }

        // UpdateSnowLight leaves the number of sources it packed in room.snowObject.visibleSnow,
        // and Snow.DrawSprites hides the snow sprite while that is zero: one field, written by
        // whichever camera blitted last, read by every camera's leaser. A camera on a screen
        // without snow showed the sprite over a snow map blitted with no sources, which
        // vanilla never displays, whenever another camera of the room had snow in view.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MoreSlugcats.Snow, int[]> visibleSnowByCamera =
            new System.Runtime.CompilerServices.ConditionalWeakTable<MoreSlugcats.Snow, int[]>();

        private static void RememberVisibleSnow(RoomCamera camera)
        {
            MoreSlugcats.Snow snow = camera?.room?.snowObject;
            if (snow == null || camera.cameraNumber < 0 || camera.cameraNumber >= 4) return;
            visibleSnowByCamera.GetValue(snow, _ => new[] { -1, -1, -1, -1 })[camera.cameraNumber] = snow.visibleSnow;
        }

        private void Snow_DrawSprites(On.MoreSlugcats.Snow.orig_DrawSprites orig, MoreSlugcats.Snow self,
            RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            int[] counts;
            if (rCam != null && rCam.cameraNumber >= 0 && rCam.cameraNumber < 4 &&
                visibleSnowByCamera.TryGetValue(self, out counts) && counts[rCam.cameraNumber] >= 0)
                self.visibleSnow = counts[rCam.cameraNumber];
            orig(self, sLeaser, rCam, timeStacker, camPos);
        }

        // ---- Globals computed from one camera in room scope ------------------------
        // BlizzardGraphics and DustWave are room objects that keep the RoomCamera
        // that created them and write _tileCorrection (screen -> room tile space for
        // the wind/dust maps) from that camera's screen position, in Update, i.e.
        // room scope: every camera showing the room received the owner's value and
        // the blizzard, snowfall, slush water and dust effects sampled the wind map
        // at the wrong place on every other camera. Recompute it per camera.
        private static void SetTileCorrectionPerCamera(Room room, Func<RoomCamera, Vector4> formula)
        {
            RainWorldGame game = room?.game;
            if (game?.cameras == null) return;
            foreach (RoomCamera camera in game.cameras)
            {
                if (camera?.room != room) continue;
                Vector4 value = formula(camera);
                WithWatcherCamera(camera, () => Shader.SetGlobalVector(RainWorld.ShadPropTileCorrection, value));
            }
        }

        public void BlizzardGraphics_Update(On.MoreSlugcats.BlizzardGraphics.orig_Update orig, MoreSlugcats.BlizzardGraphics self, bool eu)
        {
            orig(self, eu);
            if (self.room == null || self.slatedForDeletetion) return;
            SetTileCorrectionPerCamera(self.room, camera => new Vector4(
                camera.sSize.x / ((float)camera.room.TileWidth * 20f) * (1366f / camera.sSize.x) * 1.02f,
                camera.sSize.y / ((float)camera.room.TileHeight * 20f) * 1.04f,
                camera.room.cameraPositions[camera.currentCameraPosition].x / ((float)camera.room.TileWidth * 20f),
                camera.room.cameraPositions[camera.currentCameraPosition].y / ((float)camera.room.TileHeight * 20f)));
        }

        public void DustWave_Update(On.MoreSlugcats.DustWave.orig_Update orig, MoreSlugcats.DustWave self, bool eu)
        {
            orig(self, eu);
            if (self.room == null || self.slatedForDeletetion) return;
            SetTileCorrectionPerCamera(self.room, camera => new Vector4(
                camera.sSize.x / (((float)camera.room.TileWidth + 40f) * 20f) * (1366f / camera.sSize.x) * 1.02f,
                camera.sSize.y / (((float)camera.room.TileHeight + 40f) * 20f) * 1.04f,
                (camera.room.cameraPositions[camera.currentCameraPosition].x + 400f) / (((float)camera.room.TileWidth + 40f) * 20f),
                (camera.room.cameraPositions[camera.currentCameraPosition].y + 400f) / (((float)camera.room.TileHeight + 40f) * 20f)));
        }

        // ---- Effects that only work when cameras[0] is in the room ----------------
        // Vanilla gates several cosmetics on game.cameras[0]: environment-coloured
        // lights and light beams sample cameras[0]'s palette only while camera 0 is
        // in their room, and gold flakes, fairy particles, zero-g specks and the
        // Outer Expanse clouds spawn around cameras[0].pos. On a view whose camera
        // is not camera 0 those simply did not happen. Run their updates with the
        // camera that is viewing the room standing in for cameras[0].
        public void LightSource_Update(On.LightSource.orig_Update orig, LightSource self, bool eu)
        {
            WithViewingCameraAsPrimary(self.room, () => orig(self, eu));
        }

        public void LightBeam_Update(On.LightBeam.orig_Update orig, LightBeam self, bool eu)
        {
            WithViewingCameraAsPrimary(self.room, () => orig(self, eu));
        }

        public void EnergySwirl_Update(On.MoreSlugcats.EnergySwirl.orig_Update orig, MoreSlugcats.EnergySwirl self, bool eu)
        {
            WithViewingCameraAsPrimary(self.room, () => orig(self, eu));
        }

        public void GoldFlakes_Update(On.GoldFlakes.orig_Update orig, GoldFlakes self, bool eu)
        {
            WithViewingCameraAsPrimary(self.room, () => orig(self, eu));
        }

        public void GoldFlake_Update(On.GoldFlakes.GoldFlake.orig_Update orig, GoldFlakes.GoldFlake self, bool eu)
        {
            WithViewingCameraAsPrimary(self.room, () => orig(self, eu));
        }

        public void FairyParticle_Update(On.MoreSlugcats.FairyParticle.orig_Update orig, MoreSlugcats.FairyParticle self, bool eu)
        {
            WithViewingCameraAsPrimary(self.room, () => orig(self, eu));
        }

        public void GenericZeroGSpeck_Update(On.GenericZeroGSpeck.orig_Update orig, GenericZeroGSpeck self, bool eu)
        {
            WithViewingCameraAsPrimary(self.room, () => orig(self, eu));
        }

        public void AboveCloudsView_Update(On.AboveCloudsView.orig_Update orig, AboveCloudsView self, bool eu)
        {
            WithViewingCameraAsPrimary(self.room, () => orig(self, eu));
        }

        public delegate void delSetGlobalColor(int nameID, Color vec);
        public void Shader_SetGlobalColor(delSetGlobalColor orig, int nameID, Color vec)
        {
            orig(nameID, vec);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(nameID);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderColors[nameID] = vec;
            RoomShaderState room = CurrentRoomRecord();
            if (room != null) room.colors[nameID] = vec;
            if (found == 0 && (nameID == RainWorld.ShadPropMapCol || nameID == RainWorld.ShadPropMapWaterCol) &&
                !(rainworldGameObject.processManager?.currentMainLoop is RainWorldGame))
            {
                cameraListeners[0].ShaderColors[nameID] = vec;
            }
        }

        public delegate void delSetGlobalVectorArrayArray(int nameID, Vector4[] values);
        public void Shader_SetGlobalVectorArrayArray(delSetGlobalVectorArrayArray orig, int nameID, Vector4[] values)
        {
            orig(nameID, values);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(nameID);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderVectorArrays[nameID] = values?.ToArray();
            RoomShaderState room = CurrentRoomRecord();
            if (room != null) room.vectorArrays[nameID] = values?.ToArray();
        }

        public delegate void delSetGlobalVectorArrayList(int nameID, List<Vector4> values);
        public void Shader_SetGlobalVectorArrayList(delSetGlobalVectorArrayList orig, int nameID, List<Vector4> values)
        {
            orig(nameID, values);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(nameID);
            for (int i = 0; i < found; i++)
                shaderRecordScratch[i].ShaderVectorLists[nameID] = values == null ? null : new List<Vector4>(values);
        }

        public delegate void delSetGlobalVector(int nameID, Vector4 vec);
        public void Shader_SetGlobalVector(delSetGlobalVector orig, int nameID, Vector4 vec)
        {
            orig(nameID, vec);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(nameID);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderVectors[nameID] = vec;
            RoomShaderState room = CurrentRoomRecord();
            if (room != null) room.vectors[nameID] = vec;
            if (found == 0 && nameID == RainWorld.ShadPropMapPan &&
                !(rainworldGameObject.processManager?.currentMainLoop is RainWorldGame))
            {
                cameraListeners[0].ShaderVectors[nameID] = vec;
            }
        }

        public delegate void delSetGlobalFloat(int nameID, float f);
        public void Shader_SetGlobalFloat(delSetGlobalFloat orig, int nameID, float f)
        {
            orig(nameID, f);
            if (restoringShaderState || nameID == RainWorld.ShadPropRain) return;
            int found = CollectShaderRecipients(nameID);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderFloats[nameID] = f;
            RoomShaderState room = CurrentRoomRecord();
            if (room != null) room.floats[nameID] = f;
        }

        public delegate void delSetGlobalInt(int nameID, int i);
        public void Shader_SetGlobalInt(delSetGlobalInt orig, int nameID, int i)
        {
            orig(nameID, i);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(nameID);
            // underlying handler is the same
            for (int index = 0; index < found; index++) shaderRecordScratch[index].ShaderFloats[nameID] = i;
        }

        public delegate void delSetGlobalTexture(int nameID, Texture t);
        public void Shader_SetGlobalTexture(delSetGlobalTexture orig, int nameID, Texture t)
        {
            orig(nameID, t);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(nameID);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderTextures[nameID] = t;
            RoomShaderState room = CurrentRoomRecord();
            if (room != null) room.textures[nameID] = t;
            if (found == 0 && nameID == RainWorld.ShadPropMapFogTexture &&
                !(rainworldGameObject.processManager?.currentMainLoop is RainWorldGame))
            {
                cameraListeners[0].ShaderTextures[nameID] = t;
            }
        }

        public delegate void delSetGlobalColorString(string name, Color value);
        public void Shader_SetGlobalColorString(delSetGlobalColorString orig, string name, Color value)
        {
            orig(name, value);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(name);
            RoomShaderState room = CurrentRoomRecord();
            if (found == 0 && room == null) return;
            int id = Shader.PropertyToID(name);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderColors[id] = value;
            if (room != null) room.colors[id] = value;
        }

        public delegate void delSetGlobalVectorString(string name, Vector4 value);
        public void Shader_SetGlobalVectorString(delSetGlobalVectorString orig, string name, Vector4 value)
        {
            orig(name, value);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(name);
            RoomShaderState room = CurrentRoomRecord();
            if (found == 0 && room == null) return;
            int id = Shader.PropertyToID(name);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderVectors[id] = value;
            if (room != null) room.vectors[id] = value;
        }

        public delegate void delSetGlobalVectorArrayArrayString(string name, Vector4[] values);
        public void Shader_SetGlobalVectorArrayArrayString(delSetGlobalVectorArrayArrayString orig, string name, Vector4[] values)
        {
            orig(name, values);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(name);
            if (found == 0) return;
            int id = Shader.PropertyToID(name);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderVectorArrays[id] = values?.ToArray();
        }

        public delegate void delSetGlobalVectorArrayListString(string name, List<Vector4> values);
        public void Shader_SetGlobalVectorArrayListString(delSetGlobalVectorArrayListString orig, string name, List<Vector4> values)
        {
            orig(name, values);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(name);
            if (found == 0) return;
            int id = Shader.PropertyToID(name);
            for (int i = 0; i < found; i++)
                shaderRecordScratch[i].ShaderVectorLists[id] = values == null ? null : new List<Vector4>(values);
        }

        public delegate void delSetGlobalFloatString(string name, float value);
        public void Shader_SetGlobalFloatString(delSetGlobalFloatString orig, string name, float value)
        {
            orig(name, value);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(name);
            RoomShaderState room = CurrentRoomRecord();
            if (found == 0 && room == null) return;
            int id = Shader.PropertyToID(name);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderFloats[id] = value;
            if (room != null) room.floats[id] = value;
        }

        public delegate void delSetGlobalIntString(string name, int value);
        public void Shader_SetGlobalIntString(delSetGlobalIntString orig, string name, int value)
        {
            orig(name, value);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(name);
            if (found == 0) return;
            int id = Shader.PropertyToID(name);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderFloats[id] = value;
        }

        public delegate void delSetGlobalTextureString(string name, Texture value);
        public void Shader_SetGlobalTextureString(delSetGlobalTextureString orig, string name, Texture value)
        {
            orig(name, value);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients(name);
            RoomShaderState room = CurrentRoomRecord();
            if (found == 0 && room == null) return;
            int id = Shader.PropertyToID(name);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderTextures[id] = value;
            if (room != null) room.textures[id] = value;
        }

        private static void CaptureRoomCameraShaderKeywords(RoomCamera camera)
        {
            if (camera == null || camera.cameraNumber < 0 || camera.cameraNumber >= cameraListeners.Length) return;
            CameraListener listener = cameraListeners[camera.cameraNumber];
            if (listener == null) return;

            Room room = camera.room ?? camera.loadingRoom;
            listener.ShaderKeywords["VOIDSEA"] = camera.voidSeaMode;
            listener.ShaderKeywords["COMBINEDLEVEL"] = camera.levelTexCombiner != null && camera.levelTexCombiner.isActive;
            listener.ShaderKeywords["RIPPLE"] = camera.rippleData != null &&
                (camera.rippleData.isPassAdded || camera.rippleData.gameplayRippleActive);
            listener.ShaderKeywords["GAMEPLAYRIPPLETEXTURE"] = camera.rippleData != null &&
                camera.rippleData.hasGameplayScreen;

            // These textures are authoritative per RoomCamera. A different
            // camera's DrawUpdate can overwrite Unity's global bindings before
            // this camera renders, especially during camera-position fades.
            // currentPalette.texture is the same object the game binds, but it is
            // only rebuilt when a fade is applied, so read the field the game
            // itself binds rather than the snapshot taken alongside it.
            if (camera.paletteTexture != null)
                listener.ShaderTextures[RainWorld.ShadPropPalTex] = camera.paletteTexture;
            if (camera.levelTexture != null)
                listener.ShaderTextures[RainWorld.ShadPropLevelTex] = camera.levelTexture;
            listener.ShaderTextures[Shader.PropertyToID("_terrainPalette")] =
                camera.terrainPalette?.texture;

            if (room == null) return;
            listener.ShaderKeywords["RoomHasWater"] = !room.abstractRoom.gate && !room.abstractRoom.shelter && room.waterObject != null;
            listener.ShaderKeywords["RoomHasBrainMold"] = room.brainMold != null;
            listener.ShaderKeywords["RoomHasDeathFall"] = room.deathFallGraphic != null;
            listener.ShaderKeywords["Gutter"] = room.roomSettings.GetEffectAmount(RoomSettings.RoomEffect.Type.DirtyWater) > 0f;
            listener.ShaderKeywords["SNOW_ON"] = room.snowObject != null && room.snowObject.visibleSnow > 0;
            listener.ShaderKeywords["URBANLIFE"] = room.urbanLifeCount > 0;
            listener.ShaderKeywords["HR"] = Region.IsRubiconRegion(room.world.name) ||
                room.roomSettings.GetEffect(RoomSettings.RoomEffect.Type.LavaSurface) != null;
        }

        private static void RecordCameraShaderKeyword(RoomCamera camera, string keyword, bool enabled)
        {
            if (camera == null || string.IsNullOrEmpty(keyword)) return;
            int index = camera.cameraNumber;
            if (index >= 0 && index < cameraListeners.Length && cameraListeners[index] != null)
                cameraListeners[index].ShaderKeywords[keyword] = enabled;
        }
    }
}
