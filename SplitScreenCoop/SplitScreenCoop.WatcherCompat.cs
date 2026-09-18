using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Where a mask source's GameObject must sit for each camera. Mask sources
        /// (Watcher foliage and other dynamic level elements, grass, ripples, warp
        /// tears, urban shadows) are Unity meshes, not Futile sprites: one GameObject
        /// per object, however many cameras show it. A camera renders them, a
        /// full-screen grab quad captures them into _DynamicLevelElements (or the
        /// ripple/shadow grab), and that camera's level combiner composites the grab
        /// into its _LevelTex. Vanilla positions the meshes once per frame from
        /// cameras[0]; with several cameras the last RoomCamera.DrawUpdate won, so at
        /// most one camera ever had the meshes in view and every other camera's
        /// combiner worked from a stale or black grab - foliage kept its shape but lost
        /// its colour on cameras 1-3. Each camera's DrawUpdate now records the
        /// transform it wants and <see cref="PlaceMaskSourcesFor"/> applies it, with
        /// that camera's world layer, right before the camera culls.
        /// </summary>
        private sealed class MaskPlacement
        {
            public readonly Vector3[] position = new Vector3[4];
            public readonly Quaternion[] rotation = new Quaternion[4];
            public readonly Vector3[] scale = new Vector3[4];
            public readonly int[] frame = { -1, -1, -1, -1 };
            /// <summary>Last frame any camera drew the source; a camera's placement is current while it matches.</summary>
            public int lastFrame = -1;
        }

        private static readonly ConditionalWeakTable<Watcher.MaskSource, MaskPlacement> maskPlacements = new();
        private static readonly List<Watcher.MaskSource> placedMaskSources = new();

        /// <summary>
        /// Called from <see cref="CameraListener.OnPreCull"/> of camera
        /// <paramref name="cameraNumber"/>. Meshes this camera drew this frame get its
        /// transform and its isolated world layer; meshes it did not draw are hidden
        /// for its pass (they belong to rooms it does not show). Sources nobody has
        /// placed yet are left alone.
        /// </summary>
        internal static void PlaceMaskSourcesFor(int cameraNumber)
        {
            if (cameraNumber < 0 || cameraNumber >= 4 || placedMaskSources.Count == 0) return;
            bool isolate = dynamicActive && worldLayers[cameraNumber] > 0;
            for (int i = placedMaskSources.Count - 1; i >= 0; i--)
            {
                Watcher.MaskSource source = placedMaskSources[i];
                MaskPlacement placement;
                if (source == null || source.beingDeleted || source.obj == null ||
                    !maskPlacements.TryGetValue(source, out placement))
                {
                    placedMaskSources.RemoveAt(i);
                    continue;
                }
                bool shown = placement.frame[cameraNumber] == placement.lastFrame;
                if (source.meshRenderer != null) source.meshRenderer.enabled = shown;
                if (!shown) continue;
                Transform transform = source.obj.transform;
                transform.localPosition = placement.position[cameraNumber];
                transform.localRotation = placement.rotation[cameraNumber];
                transform.localScale = placement.scale[cameraNumber];
                if (isolate) source.obj.layer = worldLayers[cameraNumber];
            }
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

        private static void WithUnityMainCamera(RoomCamera camera, Action action)
        {
            int ownerIndex = camera?.cameraNumber ?? -1;
            if (ownerIndex < 0 || ownerIndex >= fcameras.Length || fcameras[ownerIndex] == null)
            {
                WithWatcherCamera(camera, action);
                return;
            }

            string[] originalTags = new string[fcameras.Length];
            bool ownerWasEnabled = fcameras[ownerIndex].enabled;
            try
            {
                for (int i = 0; i < fcameras.Length; i++)
                {
                    if (fcameras[i] == null) continue;
                    originalTags[i] = fcameras[i].gameObject.tag;
                    fcameras[i].gameObject.tag = i == ownerIndex ? "MainCamera" : "Untagged";
                }
                // Camera.main ignores disabled cameras on some Unity versions.
                fcameras[ownerIndex].enabled = true;
                WithWatcherCamera(camera, action);
            }
            finally
            {
                fcameras[ownerIndex].enabled = ownerWasEnabled;
                for (int i = 0; i < fcameras.Length; i++)
                    if (fcameras[i] != null && originalTags[i] != null)
                        fcameras[i].gameObject.tag = originalTags[i];
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
            RecordCameraShaderKeyword(owner, "GAMEPLAYRIPPLETEXTURE", false);
        }

        private void RippleCameraData_AddCommandBuffer(On.Watcher.RippleCameraData.orig_AddCommandBuffer orig, Watcher.RippleCameraData self)
        {
            RoomCamera owner = RippleOwner(self);
            WithUnityMainCamera(owner, () => orig(self));
            RecordCameraShaderKeyword(owner, "RIPPLE", true);
        }

        private void RippleCameraData_RemoveCommandBuffer(On.Watcher.RippleCameraData.orig_RemoveCommandBuffer orig, Watcher.RippleCameraData self)
        {
            RoomCamera owner = RippleOwner(self);
            WithUnityMainCamera(owner, () => orig(self));
            RecordCameraShaderKeyword(owner, "RIPPLE", false);
        }

        private void RippleCameraData_SetGlobals(On.Watcher.RippleCameraData.orig_SetGlobals orig, Watcher.RippleCameraData self)
        {
            RoomCamera owner = RippleOwner(self);
            WithWatcherCamera(owner, () => orig(self));
            RecordCameraShaderKeyword(owner, "GAMEPLAYRIPPLETEXTURE", self.hasGameplayScreen);
        }

        private void LevelTexCombiner_Initialize(On.Watcher.LevelTexCombiner.orig_Initialize orig, Watcher.LevelTexCombiner self)
        {
            RoomCamera owner = LevelOwner(self);
            WithUnityMainCamera(owner, () => orig(self));
            RecordCameraShaderKeyword(owner, "COMBINEDLEVEL", true);
        }

        private void LevelTexCombiner_RemovePass(On.Watcher.LevelTexCombiner.orig_RemovePass orig,
            Watcher.LevelTexCombiner self, string id)
        {
            RoomCamera owner = LevelOwner(self);
            if (owner == null || owner.cameraNumber < 0 || owner.cameraNumber >= fcameras.Length || fcameras[owner.cameraNumber] == null)
            {
                orig(self, id);
                return;
            }

            WithWatcherCamera(owner, () =>
            {
                RemoveWatcherBuffers(fcameras[owner.cameraNumber], CameraEvent.AfterForwardOpaque, id);
                RemoveWatcherBuffers(fcameras[owner.cameraNumber], CameraEvent.BeforeForwardAlpha, id);
                self.bufferIDs.Remove(id);
            });
        }

        private void LevelTexCombiner_RemoveAllBuffers(On.Watcher.LevelTexCombiner.orig_RemoveAllBuffers orig,
            Watcher.LevelTexCombiner self)
        {
            RoomCamera owner = LevelOwner(self);
            if (owner == null || owner.cameraNumber < 0 || owner.cameraNumber >= fcameras.Length || fcameras[owner.cameraNumber] == null)
            {
                orig(self);
                return;
            }

            WithWatcherCamera(owner, () =>
            {
                Camera target = fcameras[owner.cameraNumber];
                RemoveWatcherBuffers(target, CameraEvent.BeforeForwardOpaque, self.bufferIDs);
                RemoveWatcherBuffers(target, CameraEvent.AfterForwardOpaque, self.bufferIDs);
                RemoveWatcherBuffers(target, CameraEvent.BeforeForwardAlpha, self.bufferIDs);
                self.bufferIDs.Clear();
                Shader.DisableKeyword("COMBINEDLEVEL");
                RecordCameraShaderKeyword(owner, "COMBINEDLEVEL", false);
                self.UnSetGlobals();
                self.DisposeRenderTextures();
            });
        }

        private static void RemoveWatcherBuffers(Camera camera, CameraEvent cameraEvent, string id)
        {
            foreach (CommandBuffer buffer in camera.GetCommandBuffers(cameraEvent))
                if (buffer.name == id)
                    camera.RemoveCommandBuffer(cameraEvent, buffer);
        }

        private static void RemoveWatcherBuffers(Camera camera, CameraEvent cameraEvent, System.Collections.Generic.ICollection<string> ids)
        {
            foreach (CommandBuffer buffer in camera.GetCommandBuffers(cameraEvent))
                if (ids.Contains(buffer.name))
                    camera.RemoveCommandBuffer(cameraEvent, buffer);
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
            WithUnityMainCamera(camera, () => camera.levelTexCombiner.AddPass(
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
            if (self.obj == null || rCam == null || rCam.cameraNumber < 0 || rCam.cameraNumber >= camOffsets.Length) return;
            int number = rCam.cameraNumber;
            Transform transform = self.obj.transform;
            transform.localPosition += (Vector3)camOffsets[number];
            // Remember this camera's transform; PlaceMaskSourcesFor re-applies it when
            // this camera is about to render, whichever camera drew last.
            MaskPlacement placement;
            if (!maskPlacements.TryGetValue(self, out placement))
            {
                placement = new MaskPlacement();
                maskPlacements.Add(self, placement);
                placedMaskSources.Add(self);
            }
            placement.position[number] = transform.localPosition;
            placement.rotation[number] = transform.localRotation;
            placement.scale[number] = transform.localScale;
            placement.frame[number] = Time.frameCount;
            placement.lastFrame = Time.frameCount;
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
            placedMaskSources.Clear();
        }
    }
}
