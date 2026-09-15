
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        internal static bool restoringShaderState;
        internal static int lastCompositorClearFrame = -1;
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
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener l)
            {
                l.ShaderColors[nameID] = vec;
            }
            else if ((nameID == RainWorld.ShadPropMapCol || nameID == RainWorld.ShadPropMapWaterCol) && !(rainworldGameObject.processManager?.currentMainLoop is RainWorldGame game))
            {
                cameraListeners[0].ShaderColors[nameID] = vec;
            }
        }

        public delegate void delSetGlobalVectorArrayArray(int nameID, Vector4[] values);
        public void Shader_SetGlobalVectorArrayArray(delSetGlobalVectorArrayArray orig, int nameID, Vector4[] values)
        {
            orig(nameID, values);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener l)
            {
                l.ShaderVectorArrays[nameID] = values?.ToArray();
            }
        }
        public delegate void delSetGlobalVectorArrayList(int nameID, List<Vector4> values);
        public void Shader_SetGlobalVectorArrayList(delSetGlobalVectorArrayList orig, int nameID, List<Vector4> values)
        {
            orig(nameID, values);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener l)
            {
                l.ShaderVectorLists[nameID] = values == null ? null : new List<Vector4>(values);
            }
        }

        public delegate void delSetGlobalVector(int nameID, Vector4 vec);
        public void Shader_SetGlobalVector(delSetGlobalVector orig, int nameID, Vector4 vec)
        {
            orig(nameID, vec);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener l)
            {
                l.ShaderVectors[nameID] = vec;
            }
            else if (nameID == RainWorld.ShadPropMapPan && !(rainworldGameObject.processManager?.currentMainLoop is RainWorldGame game))
            {
                cameraListeners[0].ShaderVectors[nameID] = vec;
            }
        }

        public delegate void delSetGlobalFloat(int nameID, float f);
        public void Shader_SetGlobalFloat(delSetGlobalFloat orig, int nameID, float f)
        {
            orig(nameID, f);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener l && (nameID != RainWorld.ShadPropRain))
            {
                l.ShaderFloats[nameID] = f;
            }
        }

        public delegate void delSetGlobalInt(int nameID, int i);
        public void Shader_SetGlobalInt(delSetGlobalInt orig, int nameID, int i)
        {
            orig(nameID, i);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener l)
            {
                l.ShaderFloats[nameID] = i; // underlying handler is the same
            }
        }

        public delegate void delSetGlobalTexture(int nameID, Texture t);
        public void Shader_SetGlobalTexture(delSetGlobalTexture orig, int nameID, Texture t)
        {
            orig(nameID, t);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener l)
            {
                l.ShaderTextures[nameID] = t;
            }
            else if (nameID == RainWorld.ShadPropMapFogTexture && !(rainworldGameObject.processManager?.currentMainLoop is RainWorldGame game))
            {
                cameraListeners[0].ShaderTextures[nameID] = t;
            }
        }

        public delegate void delSetGlobalColorString(string name, Color value);
        public void Shader_SetGlobalColorString(delSetGlobalColorString orig, string name, Color value)
        {
            orig(name, value);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener listener)
                listener.ShaderColors[Shader.PropertyToID(name)] = value;
        }

        public delegate void delSetGlobalVectorString(string name, Vector4 value);
        public void Shader_SetGlobalVectorString(delSetGlobalVectorString orig, string name, Vector4 value)
        {
            orig(name, value);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener listener)
                listener.ShaderVectors[Shader.PropertyToID(name)] = value;
        }

        public delegate void delSetGlobalVectorArrayArrayString(string name, Vector4[] values);
        public void Shader_SetGlobalVectorArrayArrayString(delSetGlobalVectorArrayArrayString orig, string name, Vector4[] values)
        {
            orig(name, values);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener listener)
                listener.ShaderVectorArrays[Shader.PropertyToID(name)] = values?.ToArray();
        }

        public delegate void delSetGlobalVectorArrayListString(string name, List<Vector4> values);
        public void Shader_SetGlobalVectorArrayListString(delSetGlobalVectorArrayListString orig, string name, List<Vector4> values)
        {
            orig(name, values);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener listener)
                listener.ShaderVectorLists[Shader.PropertyToID(name)] = values == null ? null : new List<Vector4>(values);
        }

        public delegate void delSetGlobalFloatString(string name, float value);
        public void Shader_SetGlobalFloatString(delSetGlobalFloatString orig, string name, float value)
        {
            orig(name, value);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener listener)
                listener.ShaderFloats[Shader.PropertyToID(name)] = value;
        }

        public delegate void delSetGlobalIntString(string name, int value);
        public void Shader_SetGlobalIntString(delSetGlobalIntString orig, string name, int value)
        {
            orig(name, value);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener listener)
                listener.ShaderFloats[Shader.PropertyToID(name)] = value;
        }

        public delegate void delSetGlobalTextureString(string name, Texture value);
        public void Shader_SetGlobalTextureString(delSetGlobalTextureString orig, string name, Texture value)
        {
            orig(name, value);
            if (restoringShaderState) return;
            if (curCamera >= 0 && cameraListeners[curCamera] is CameraListener listener)
                listener.ShaderTextures[Shader.PropertyToID(name)] = value;
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
