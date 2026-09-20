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
        /// (Watcher sloped terrain, mud pits, foliage and other dynamic level elements,
        /// grass, candles, pearl readers, ripples, warp tears, urban shadows) are raw
        /// Unity meshes on layer 0, not Futile sprites. A camera renders them in the
        /// opaque queue with a shader that writes DATA, not colour; a full-screen grab
        /// quad captures that into _SlopedTerrainMask, _DynamicLevelElements or a
        /// ripple/shadow grab and wipes it again; the level is drawn over it later.
        /// Vanilla places the meshes once per frame for cameras[0]. With several
        /// cameras the last RoomCamera.DrawUpdate won, so every other camera worked
        /// from a stale or black grab (foliage without colour on cameras 1-3).
        ///
        /// Every camera therefore records the transform it asks for and
        /// <see cref="PlaceMaskSourcesFor"/> applies it, with that camera's world
        /// layer, right before the camera culls. Until 2026-09-19 only
        /// MaskSource.DrawUpdate was recorded, and half of vanilla does not use it:
        /// TerrainCurveMaskSource, Grass, UrbanCandle(Holder), PearlContent and
        /// UrbanShadow write setGameObjectPos/Rotation/Scale themselves, MudPit and
        /// UrbanLife never move the object at all. Those meshes stayed on layer 0 at
        /// the last camera's position: no isolated world camera drew them (no terrain
        /// mask, grass or candles in Dynamic style), and the overlay camera, which
        /// draws layer 0 over the finished composite for the pause pointer, DID - with
        /// no grab quad to wipe them. That was the "snow artifact": SlopedTerrainMask
        /// writes R = 0.67 - depth / 3, G = a 0/1 flag, so a snow dune appeared as a
        /// flat lime hill with a dark red top, across the split, unclipped by any cell.
        /// Now the three setters themselves are hooked, so it no longer matters how a
        /// drawable moves its mesh; sprite leasers record what nobody moved; a source
        /// is known from the moment its GameObject exists and hidden until a camera
        /// has asked for it; and the overlay camera refuses any mask mesh outright.
        /// </summary>
        private sealed class MaskPlacement
        {
            public readonly Vector3[] position = new Vector3[4];
            public readonly Quaternion[] rotation = new Quaternion[4];
            public readonly Vector3[] scale = new Vector3[4];
            public readonly int[] frame = { -1, -1, -1, -1 };
            /// <summary>Frame in which camera n last set the position itself, through the hooked setter.</summary>
            public readonly int[] positionFrame = { -1, -1, -1, -1 };
            /// <summary>The last position vanilla asked for, before any camera offset. Zero for meshes nobody moves (MudPit bakes the camera into its vertices).</summary>
            public Vector3 requested;
            /// <summary>Last frame any camera drew the source; a camera's placement is current while it matches. -1: nobody has yet.</summary>
            public int lastFrame = -1;
        }

        private static readonly ConditionalWeakTable<Watcher.MaskSource, MaskPlacement> maskPlacements = new();
        private static readonly List<Watcher.MaskSource> placedMaskSources = new();
        private static readonly HashSet<string> strayMaskNames = new HashSet<string>();
        // The three setters arrive back to back for one source; skip the table lookup for the second and third.
        private static Watcher.MaskSource cachedPlacementSource;
        private static MaskPlacement cachedPlacement;

        private static MaskPlacement PlacementOf(Watcher.MaskSource source)
        {
            if (ReferenceEquals(source, cachedPlacementSource) && cachedPlacement != null) return cachedPlacement;
            MaskPlacement placement;
            if (!maskPlacements.TryGetValue(source, out placement))
            {
                placement = new MaskPlacement();
                maskPlacements.Add(source, placement);
                placedMaskSources.Add(source);
            }
            cachedPlacementSource = source;
            cachedPlacement = placement;
            return placement;
        }

        /// <summary>The camera whose update or draw is running, if any.</summary>
        private static bool MaskCamera(out int number)
        {
            number = curCamera;
            return number >= 0 && number < 4 && number < camOffsets.Length;
        }

        private static void RecordMaskPlacement(Watcher.MaskSource source, MaskPlacement placement, int number)
        {
            Transform transform = source.obj.transform;
            int frame = Time.frameCount;
            // A camera that only scaled or rotated the mesh this frame (or did nothing,
            // see RecordLeaserMaskSources) still needs it in front of itself.
            placement.position[number] = placement.positionFrame[number] == frame
                ? transform.localPosition
                : placement.requested + (Vector3)camOffsets[number];
            placement.rotation[number] = transform.localRotation;
            placement.scale[number] = transform.localScale;
            placement.frame[number] = frame;
            placement.lastFrame = frame;
        }

        public delegate void orig_MaskSourceSetVector(Watcher.MaskSource self, Vector3 value);

        public void MaskSource_set_GameObjectPos(orig_MaskSourceSetVector orig, Watcher.MaskSource self, Vector3 value)
        {
            int number = -1;
            if (self?.obj == null || !MaskCamera(out number))
            {
                if (self?.obj != null) PlacementOf(self).requested = value;
                orig(self, value);
                return;
            }
            MaskPlacement placement = PlacementOf(self);
            placement.requested = value;
            orig(self, value + (Vector3)camOffsets[number]);
            placement.positionFrame[number] = Time.frameCount;
            RecordMaskPlacement(self, placement, number);
        }

        public void MaskSource_set_GameObjectRotation(orig_MaskSourceSetVector orig, Watcher.MaskSource self, Vector3 value)
        {
            orig(self, value);
            int number;
            if (self?.obj != null && MaskCamera(out number)) RecordMaskPlacement(self, PlacementOf(self), number);
        }

        public void MaskSource_set_GameObjectScale(orig_MaskSourceSetVector orig, Watcher.MaskSource self, Vector3 value)
        {
            orig(self, value);
            int number;
            if (self?.obj != null && MaskCamera(out number)) RecordMaskPlacement(self, PlacementOf(self), number);
        }

        /// <summary>Known from birth, so PlaceMaskSourcesFor hides it from every camera until one has asked for it.</summary>
        private void MaskSource_CreateGameObject(On.Watcher.MaskSource.orig_CreateGameObject orig, Watcher.MaskSource self)
        {
            orig(self);
            if (self?.obj != null) PlacementOf(self);
        }

        /// <summary>
        /// After a leaser's DrawSprites for <paramref name="rCam"/>: its mask sources
        /// that nobody moved this frame still get this camera's offset and layer.
        /// MudPit writes camera-relative vertices into the mesh and leaves the object
        /// at the origin; UrbanLife only scales its two quads when they are created.
        /// </summary>
        internal static void RecordLeaserMaskSources(RoomCamera.SpriteLeaser leaser, RoomCamera rCam)
        {
            Watcher.MaskSource[] sources = leaser?.maskSources;
            if (sources == null || sources.Length == 0 || rCam == null) return;
            int number = rCam.cameraNumber;
            if (number < 0 || number >= 4 || number >= camOffsets.Length) return;
            int frame = Time.frameCount;
            foreach (Watcher.MaskSource source in sources)
            {
                if (source == null || source.beingDeleted || source.obj == null) continue;
                MaskPlacement placement = PlacementOf(source);
                if (placement.frame[number] != frame) RecordMaskPlacement(source, placement, number);
            }
        }

        /// <summary>
        /// Called before the overlay camera culls. It draws Futile's root stage (layer
        /// 0: pause pointer, fades, other mods) over the finished composite and has no
        /// grab quad, so a mask mesh it can see stays on screen as raw data. None
        /// should be left on its layers; if one is, hide it and say which.
        /// </summary>
        internal static void HideMaskSourcesFromOverlay(Camera overlay)
        {
            if (overlay == null || placedMaskSources.Count == 0) return;
            int mask = overlay.cullingMask;
            for (int i = placedMaskSources.Count - 1; i >= 0; i--)
            {
                Watcher.MaskSource source = placedMaskSources[i];
                if (source == null || source.beingDeleted || source.obj == null || source.meshRenderer == null) continue;
                if (!source.meshRenderer.enabled || !source.obj.activeSelf || (mask & (1 << source.obj.layer)) == 0) continue;
                source.meshRenderer.enabled = false;
                string name = source.obj.name;
                if (strayMaskNames.Add(name))
                    sLogger?.LogWarning($"[MaskAudit] frame={Time.frameCount} raw mask mesh '{name}' (layer {source.obj.layer}) was in the overlay camera's view and was hidden; " +
                        "it would have shown as flat data colours over the split. Something places this mesh without the hooked setters.");
            }
        }

        /// <summary>
        /// Called from <see cref="CameraListener.OnPreCull"/> of camera
        /// <paramref name="cameraNumber"/>. Meshes this camera drew this frame get its
        /// transform and its isolated world layer; meshes it did not draw are hidden
        /// for its pass (they belong to rooms it does not show), and so are sources
        /// no camera has asked for yet.
        /// </summary>
        internal static void PlaceMaskSourcesFor(int cameraNumber)
        {
            if (cameraNumber < 0 || cameraNumber >= 4 || placedMaskSources.Count == 0) return;
            bool isolate = (dynamicActive || dualDisplays) && worldLayers[cameraNumber] > 0;
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
                bool shown = placement.lastFrame >= 0 && placement.frame[cameraNumber] == placement.lastFrame;
                if (source.meshRenderer != null) source.meshRenderer.enabled = shown;
                if (!shown) continue;
                Transform transform = source.obj.transform;
                transform.localPosition = placement.position[cameraNumber];
                transform.localRotation = placement.rotation[cameraNumber];
                transform.localScale = placement.scale[cameraNumber];
                source.obj.layer = isolate ? worldLayers[cameraNumber] : 0;
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
                else
                {
                    // The combiner pass samples the grab quad's named GrabPass texture.
                    // Unity performs a named GrabPass once per *frame*, for the first
                    // camera that draws the quad; every later camera that frame reuses
                    // that texture. So the pass of cameras 1-3 combined camera 0's grab
                    // into their level texture and their foliage, grass, rot tubes and
                    // candles came out black or missing. Take this camera's own grab
                    // here instead: by AfterForwardOpaque the mask meshes (opaque
                    // queue) have all been drawn into this camera's target.
                    // Only while several cameras render in one frame: with one camera
                    // per frame the vanilla grab is already this camera's own, and it
                    // is cleaner than a copy of the whole opaque target.
                    RenderTexture grab = id == "DynamicLevelElement" && !alternateFrames ? DynamicElementGrab(owner.cameraNumber) : null;
                    if (grab != null)
                    {
                        buffer.Blit(BuiltinRenderTextureType.CameraTarget, grab);
                        buffer.SetGlobalTexture("_DynamicLevelElements", grab);
                    }
                    if (material != null)
                        buffer.Blit(texture, self.intermediateTex, material);
                    else
                        buffer.Blit(texture, self.intermediateTex);
                }

                buffer.CopyTexture(self.intermediateTex, self.combinedLevelTex);
                buffer.SetGlobalTexture("_LevelTex", self.combinedLevelTex);
                // Every blit above rebinds the render target (to the grab copy, then to
                // the 1400x800 combiner texture that holds the raw, unpaletted level).
                // Leave the camera's own target bound when the buffer ends: a screen
                // grab taken with the combiner texture still bound shows the raw level
                // encoding (the green/red water of the twelfth playtest's screenshot).
                buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
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

        /// <summary>
        /// LevelTexCombiner_CreateBuffer bakes "emulate this camera's grab" into the
        /// command buffer from alternateFrames at build time. Buffers are usually
        /// built while the players stand together; rebuild the pass on every camera
        /// when the rendering mode changes so the buffer matches the mode it runs in.
        /// </summary>
        private void RebuildDynamicElementPasses(RainWorldGame game)
        {
            if (game?.cameras == null) return;
            foreach (RoomCamera camera in game.cameras)
            {
                Watcher.LevelTexCombiner combiner = camera?.levelTexCombiner;
                if (combiner?.bufferIDs == null || !combiner.bufferIDs.Contains("DynamicLevelElement")) continue;
                try
                {
                    combiner.RemovePass("DynamicLevelElement");
                    DynamicLevelElement_AddLevelCombiner(null, camera);
                }
                catch (Exception error) { LogHookError("RebuildDynamicElementPasses", error); }
            }
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
            // The three setters this calls add the camera's offset and record the
            // placement for curCamera; make sure that is the camera being drawn.
            int previous = curCamera;
            try
            {
                if (rCam != null) curCamera = rCam.cameraNumber;
                orig(self, timeStacker, rCam, camPos);
            }
            finally { curCamera = previous; }
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

        /// <summary>Per-camera copy of the screen at the dynamic level element pass; see LevelTexCombiner_CreateBuffer.</summary>
        private static readonly RenderTexture[] dynamicElementGrabs = new RenderTexture[4];

        private static RenderTexture DynamicElementGrab(int cameraNumber)
        {
            if (cameraNumber < 0 || cameraNumber >= dynamicElementGrabs.Length) return null;
            RenderTexture reference = cameraNumber < cameraListeners.Length ? cameraListeners[cameraNumber]?.renderTexture : null;
            int width = reference != null ? reference.width : Futile.screen?.renderTexture?.width ?? 0;
            int height = reference != null ? reference.height : Futile.screen?.renderTexture?.height ?? 0;
            if (width <= 0 || height <= 0) return null;
            RenderTexture grab = dynamicElementGrabs[cameraNumber];
            if (grab != null && (grab.width != width || grab.height != height))
            {
                grab.Release();
                UnityEngine.Object.Destroy(grab);
                grab = null;
            }
            if (grab == null)
            {
                grab = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    name = "SplitScreen dynamic elements grab " + cameraNumber,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                grab.Create();
                dynamicElementGrabs[cameraNumber] = grab;
            }
            return grab;
        }

        private static void DisposeWatcherMasks()
        {
            if (Watcher.MaskMaker.isInstanced) Watcher.MaskMaker.maskMaker.Dispose();
            placedMaskSources.Clear();
            strayMaskNames.Clear();
            cachedPlacementSource = null;
            cachedPlacement = null;
            for (int i = 0; i < dynamicElementGrabs.Length; i++)
            {
                if (dynamicElementGrabs[i] == null) continue;
                dynamicElementGrabs[i].Release();
                UnityEngine.Object.Destroy(dynamicElementGrabs[i]);
                dynamicElementGrabs[i] = null;
            }
        }
    }
}
