
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
                CaptureRoomCameraShaderKeywords(self);
                OffsetHud(self);
                RouteGlobalMeters(self);
            }
            finally
            {
                curCamera = prev;
            }
        }

        public void RoomCamera_Update(On.RoomCamera.orig_Update orig, RoomCamera self)
        {
            var prev = curCamera;
            try
            {
                curCamera = self.cameraNumber;
                orig(self);
                NoteRoomCameraUpdated(self);
                CaptureRoomCameraShaderKeywords(self);
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

        public void RoomCamera_UpdateSnowLight(On.RoomCamera.orig_UpdateSnowLight orig, RoomCamera self)
        {
            if (cameraListeners[self.cameraNumber] is CameraListener l)
            {
                l.OnPreRender();
            }
            orig(self);
        }

        public delegate void delSetGlobalColor(int nameID, Color vec);
        public void Shader_SetGlobalColor(delSetGlobalColor orig, int nameID, Color vec)
        {
            orig(nameID, vec);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients();
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
            int found = CollectShaderRecipients();
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderVectorArrays[nameID] = values?.ToArray();
            RoomShaderState room = CurrentRoomRecord();
            if (room != null) room.vectorArrays[nameID] = values?.ToArray();
        }

        public delegate void delSetGlobalVectorArrayList(int nameID, List<Vector4> values);
        public void Shader_SetGlobalVectorArrayList(delSetGlobalVectorArrayList orig, int nameID, List<Vector4> values)
        {
            orig(nameID, values);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients();
            for (int i = 0; i < found; i++)
                shaderRecordScratch[i].ShaderVectorLists[nameID] = values == null ? null : new List<Vector4>(values);
        }

        public delegate void delSetGlobalVector(int nameID, Vector4 vec);
        public void Shader_SetGlobalVector(delSetGlobalVector orig, int nameID, Vector4 vec)
        {
            orig(nameID, vec);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients();
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
            int found = CollectShaderRecipients();
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderFloats[nameID] = f;
            RoomShaderState room = CurrentRoomRecord();
            if (room != null) room.floats[nameID] = f;
        }

        public delegate void delSetGlobalInt(int nameID, int i);
        public void Shader_SetGlobalInt(delSetGlobalInt orig, int nameID, int i)
        {
            orig(nameID, i);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients();
            // underlying handler is the same
            for (int index = 0; index < found; index++) shaderRecordScratch[index].ShaderFloats[nameID] = i;
        }

        public delegate void delSetGlobalTexture(int nameID, Texture t);
        public void Shader_SetGlobalTexture(delSetGlobalTexture orig, int nameID, Texture t)
        {
            orig(nameID, t);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients();
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
            int found = CollectShaderRecipients();
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
            int found = CollectShaderRecipients();
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
            int found = CollectShaderRecipients();
            if (found == 0) return;
            int id = Shader.PropertyToID(name);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderVectorArrays[id] = values?.ToArray();
        }

        public delegate void delSetGlobalVectorArrayListString(string name, List<Vector4> values);
        public void Shader_SetGlobalVectorArrayListString(delSetGlobalVectorArrayListString orig, string name, List<Vector4> values)
        {
            orig(name, values);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients();
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
            int found = CollectShaderRecipients();
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
            int found = CollectShaderRecipients();
            if (found == 0) return;
            int id = Shader.PropertyToID(name);
            for (int i = 0; i < found; i++) shaderRecordScratch[i].ShaderFloats[id] = value;
        }

        public delegate void delSetGlobalTextureString(string name, Texture value);
        public void Shader_SetGlobalTextureString(delSetGlobalTextureString orig, string name, Texture value)
        {
            orig(name, value);
            if (restoringShaderState) return;
            int found = CollectShaderRecipients();
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
