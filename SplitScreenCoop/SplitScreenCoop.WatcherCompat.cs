using System;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;
using UnityEngine.Rendering;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        private sealed class WatcherCameraOwner
        {
            public RoomCamera camera;
        }

        private static readonly ConditionalWeakTable<Watcher.LevelTexCombiner, WatcherCameraOwner> levelCombinerOwners = new();
        private static readonly ConditionalWeakTable<Watcher.RippleCameraData, WatcherCameraOwner> rippleOwners = new();

        private static RoomCamera LevelOwner(Watcher.LevelTexCombiner self)
        {
            return levelCombinerOwners.TryGetValue(self, out var owner) ? owner.camera : null;
        }

        private static RoomCamera RippleOwner(Watcher.RippleCameraData self)
        {
            return rippleOwners.TryGetValue(self, out var owner) ? owner.camera : null;
        }

        private static void RegisterWatcherCamera(RoomCamera camera)
        {
            if (camera?.levelTexCombiner != null)
            {
                levelCombinerOwners.Remove(camera.levelTexCombiner);
                levelCombinerOwners.Add(camera.levelTexCombiner, new WatcherCameraOwner { camera = camera });
            }
            if (camera?.rippleData != null) RegisterRippleOwner(camera.rippleData, camera);
            if (camera?.warpRippleData != null) RegisterRippleOwner(camera.warpRippleData, camera);
        }

        private static void RegisterRippleOwner(Watcher.RippleCameraData data, RoomCamera camera)
        {
            if (data == null || camera == null) return;
            rippleOwners.Remove(data);
            rippleOwners.Add(data, new WatcherCameraOwner { camera = camera });
        }

        public delegate Camera orig_CameraMain();
        public Camera Camera_get_main(orig_CameraMain orig)
        {
            if (curCamera >= 0 && curCamera < fcameras.Length && fcameras[curCamera] != null)
                return fcameras[curCamera];
            return orig();
        }

        public delegate bool orig_MaskSourceVisible(Watcher.MaskSource self);
        public bool MaskSource_get_IsVisible(orig_MaskSourceVisible orig, Watcher.MaskSource self)
        {
            return self?.obj != null && self.obj.activeSelf;
        }

        private static void WithWatcherCamera(RoomCamera camera, Action action)
        {
            int previous = curCamera;
            try
            {
                if (camera != null) curCamera = camera.cameraNumber;
                action();
            }
            finally
            {
                curCamera = previous;
            }
        }

        private static void WithViewingCameraAsPrimary(Room room, Action action)
        {
            RainWorldGame game = room?.game;
            if (game?.cameras == null)
            {
                action();
                return;
            }

            int index = Array.FindIndex(game.cameras, camera => camera?.room == room);
            if (index <= 0)
            {
                WithWatcherCamera(index == 0 ? game.cameras[0] : null, action);
                return;
            }

            RoomCamera primary = game.cameras[0];
            RoomCamera viewing = game.cameras[index];
            try
            {
                game.cameras[0] = viewing;
                game.cameras[index] = primary;
                WithWatcherCamera(viewing, action);
            }
            finally
            {
                game.cameras[0] = primary;
                game.cameras[index] = viewing;
            }
        }

        private void RippleCameraData_ctor(On.Watcher.RippleCameraData.orig_ctor orig, Watcher.RippleCameraData self, RoomCamera owner)
        {
            WithWatcherCamera(owner, () => orig(self, owner));
            RegisterRippleOwner(self, owner);
        }

        private void RippleCameraData_AddCommandBuffer(On.Watcher.RippleCameraData.orig_AddCommandBuffer orig, Watcher.RippleCameraData self)
        {
            WithWatcherCamera(RippleOwner(self), () => orig(self));
        }

        private void RippleCameraData_RemoveCommandBuffer(On.Watcher.RippleCameraData.orig_RemoveCommandBuffer orig, Watcher.RippleCameraData self)
        {
            WithWatcherCamera(RippleOwner(self), () => orig(self));
        }

        private void RippleCameraData_SetGlobals(On.Watcher.RippleCameraData.orig_SetGlobals orig, Watcher.RippleCameraData self)
        {
            WithWatcherCamera(RippleOwner(self), () => orig(self));
        }

        private void LevelTexCombiner_Initialize(On.Watcher.LevelTexCombiner.orig_Initialize orig, Watcher.LevelTexCombiner self)
        {
            WithWatcherCamera(LevelOwner(self), () => orig(self));
        }

        private void LevelTexCombiner_CreateBuffer(On.Watcher.LevelTexCombiner.orig_CreateBuffer orig, Watcher.LevelTexCombiner self,
            string id, RenderTargetIdentifier texture, Material material, CameraEvent evt)
        {
            RoomCamera owner = LevelOwner(self);
            if (owner == null)
            {
                orig(self, id, texture, material, evt);
                return;
            }

            WithWatcherCamera(owner, () =>
            {
                var buffer = new CommandBuffer { name = id };
                if (evt == CameraEvent.BeforeForwardOpaque)
                    buffer.Blit(Custom.rainWorld.persistentData.cameraTextures[owner.cameraNumber, 0], self.intermediateTex);
                else if (material != null)
                    buffer.Blit(texture, self.intermediateTex, material);
                else
                    buffer.Blit(texture, self.intermediateTex);

                buffer.CopyTexture(self.intermediateTex, self.combinedLevelTex);
                buffer.SetGlobalTexture("_LevelTex", self.combinedLevelTex);
                fcameras[owner.cameraNumber].AddCommandBuffer(evt, buffer);
                self.bufferIDs.Add(buffer.name);
                self.SetGlobals();
            });
        }

        private void LevelTexCombiner_SetGlobals(On.Watcher.LevelTexCombiner.orig_SetGlobals orig, Watcher.LevelTexCombiner self)
        {
            RoomCamera owner = LevelOwner(self);
            if (owner == null)
            {
                orig(self);
                return;
            }
            WithWatcherCamera(owner, () =>
            {
                Shader.SetGlobalTexture("_OrigLevelTex", Custom.rainWorld.persistentData.cameraTextures[owner.cameraNumber, 0]);
                Shader.SetGlobalTexture("_LevelTex", self.combinedLevelTex);
            });
        }

        private void LevelTexCombiner_UnSetGlobals(On.Watcher.LevelTexCombiner.orig_UnSetGlobals orig, Watcher.LevelTexCombiner self)
        {
            RoomCamera owner = LevelOwner(self);
            if (owner == null)
            {
                orig(self);
                return;
            }
            WithWatcherCamera(owner, () =>
                Shader.SetGlobalTexture("_LevelTex", Custom.rainWorld.persistentData.cameraTextures[owner.cameraNumber, 0]));
        }

        private void DynamicLevelElement_AddLevelCombiner(On.Watcher.DynamicLevelElement.orig_AddLevelCombiner orig, RoomCamera camera)
        {
            if (camera?.levelTexCombiner == null || camera.levelTexCombiner.bufferIDs.Contains("DynamicLevelElement")) return;
            WithWatcherCamera(camera, () => camera.levelTexCombiner.AddPass(
                RenderTexture.GetTemporary(1, 1),
                new Material(Shader.Find("Futile/DynamicLevelElementCombiner")),
                "DynamicLevelElement",
                CameraEvent.AfterForwardOpaque));
        }

        private void DynamicLevelElement_DrawSprites(On.Watcher.DynamicLevelElement.orig_DrawSprites orig, Watcher.DynamicLevelElement self,
            RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            DynamicLevelElement_AddLevelCombiner(null, rCam);
            orig(self, sLeaser, rCam, timeStacker, camPos);
        }

        private void MaskSource_DrawUpdate(On.Watcher.MaskSource.orig_DrawUpdate orig, Watcher.MaskSource self,
            float timeStacker, RoomCamera rCam, Vector2 camPos)
        {
            orig(self, timeStacker, rCam, camPos);
            if (self.obj != null && rCam != null && rCam.cameraNumber >= 0 && rCam.cameraNumber < camOffsets.Length)
                self.obj.transform.localPosition += (Vector3)camOffsets[rCam.cameraNumber];
        }

        private void RippleFlow_Update(On.Watcher.FloatingDebris.RippleFlow.orig_Update orig,
            Watcher.FloatingDebris.RippleFlow self, bool eu)
        {
            WithViewingCameraAsPrimary(self.room, () =>
            {
                RoomCamera camera = self.room?.game?.cameras?[0];
                if (camera != null && camera.rippleData == null)
                    camera.UpdateRippleData(self.room, camera.currentCameraPosition);
                orig(self, eu);
            });
        }

        private void RippleSpiderSpawner_SpawnRippleTear(On.Watcher.RippleSpider.RippleSpiderSpawner.orig_SpawnRippleTear orig,
            Watcher.RippleSpider.RippleSpiderSpawner self)
        {
            WithViewingCameraAsPrimary(self.room, () =>
            {
                foreach (RoomCamera camera in self.room.game.cameras)
                    if (camera?.room == self.room)
                        WithWatcherCamera(camera, () => camera.UpdateRippleData(self.room, camera.currentCameraPosition));
                orig(self);
            });
        }

        private void RoomCamera_ClearMaskMaker(On.RoomCamera.orig_ClearMaskMaker orig, RoomCamera self)
        {
            if (self?.game?.cameras == null || self.game.cameras.Length <= 1)
            {
                orig(self);
                return;
            }

            // MaskMaker is a Watcher-wide singleton. A secondary camera must not tear
            // down masks that another camera is still drawing.
            WithWatcherCamera(self, () =>
            {
                self.levelTexCombiner?.RemoveAllBuffers();
                Shader.SetGlobalTexture("_DynamicLevelElements", Texture2D.blackTexture);
                Watcher.DynamicLevelElement.levelCombinerAdded = false;
            });
        }

        private static void DisposeWatcherMasks()
        {
            if (Watcher.MaskMaker.isInstanced) Watcher.MaskMaker.maskMaker.Dispose();
        }
    }
}
