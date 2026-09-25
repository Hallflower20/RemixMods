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
            // What the mesh's transform holds now, so PlaceMaskSourcesFor writes only what
            // changed: the camera it was last placed for (by the setters during that
            // camera's draw, or by PlaceMaskSourcesFor), -1 when unknown, and the values.
            public int appliedCamera = -1;
            public Vector3 appliedPosition, appliedScale;
            public Quaternion appliedRotation;
            public int appliedLayer = -1;

            /// <summary>The transform now holds camera <paramref name="number"/>'s recorded pose.</summary>
            public void Applied(int number)
            {
                appliedCamera = number;
                appliedPosition = position[number];
                appliedRotation = rotation[number];
                appliedScale = scale[number];
            }
        }

        private struct PlacedMask
        {
            public Watcher.MaskSource source;
            public MaskPlacement placement;
        }

        private static readonly ConditionalWeakTable<Watcher.MaskSource, MaskPlacement> maskPlacements = new();
        /// <summary>Every known source with its placement, so the per-camera pass needs no table lookup.</summary>
        private static readonly List<PlacedMask> placedMaskSources = new();
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
                placedMaskSources.Add(new PlacedMask { source = source, placement = placement });
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
                if (self?.obj != null)
                {
                    MaskPlacement unscoped = PlacementOf(self);
                    unscoped.requested = value;
                    unscoped.appliedCamera = -1; // moved for nobody in particular
                }
                orig(self, value);
                return;
            }
            MaskPlacement placement = PlacementOf(self);
            placement.requested = value;
            orig(self, value + (Vector3)camOffsets[number]);
            placement.positionFrame[number] = Time.frameCount;
            RecordMaskPlacement(self, placement, number);
            // The pose was just read back from the transform: it now holds this camera's.
            placement.Applied(number);
        }

        public void MaskSource_set_GameObjectRotation(orig_MaskSourceSetVector orig, Watcher.MaskSource self, Vector3 value)
        {
            orig(self, value);
            SetterMovedMask(self);
        }

        public void MaskSource_set_GameObjectScale(orig_MaskSourceSetVector orig, Watcher.MaskSource self, Vector3 value)
        {
            orig(self, value);
            SetterMovedMask(self);
        }

        /// <summary>After a rotation or scale setter: record it, and keep the applied pose only if it was already this camera's.</summary>
        private static void SetterMovedMask(Watcher.MaskSource self)
        {
            if (self?.obj == null) return;
            MaskPlacement placement = PlacementOf(self);
            int number;
            if (!MaskCamera(out number))
            {
                placement.appliedCamera = -1;
                return;
            }
            RecordMaskPlacement(self, placement, number);
            if (placement.appliedCamera == number) placement.Applied(number);
            else placement.appliedCamera = -1; // another camera's position, this one's rotation or scale
        }

        /// <summary>Known from birth, so PlaceMaskSourcesFor hides it from every camera until one has asked for it.</summary>
        private void MaskSource_CreateGameObject(On.Watcher.MaskSource.orig_CreateGameObject orig, Watcher.MaskSource self)
        {
            orig(self);
            if (self?.obj == null) return;
            // A new GameObject: nothing written to the old one applies.
            MaskPlacement placement = PlacementOf(self);
            placement.appliedCamera = -1;
            placement.appliedLayer = -1;
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
                Watcher.MaskSource source = placedMaskSources[i].source;
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
        /// no camera has asked for yet. Every camera, every frame, every source ever
        /// seen (311 in one log): only what differs from the mesh's current state is
        /// written. With the cameras taking turns, the rendering camera's own draw has
        /// usually just placed its meshes through the setters.
        /// </summary>
        internal static void PlaceMaskSourcesFor(int cameraNumber)
        {
            if (cameraNumber < 0 || cameraNumber >= 4 || placedMaskSources.Count == 0) return;
            bool isolate = (dynamicActive || dualDisplays) && worldLayers[cameraNumber] > 0;
            int layer = isolate ? worldLayers[cameraNumber] : 0;
            for (int i = placedMaskSources.Count - 1; i >= 0; i--)
            {
                PlacedMask entry = placedMaskSources[i];
                Watcher.MaskSource source = entry.source;
                MaskPlacement placement = entry.placement;
                if (source == null || source.beingDeleted || source.obj == null || placement == null)
                {
                    placedMaskSources.RemoveAt(i);
                    continue;
                }
                bool shown = placement.lastFrame >= 0 && placement.frame[cameraNumber] == placement.lastFrame;
                MeshRenderer renderer = source.meshRenderer;
                if (renderer != null && renderer.enabled != shown) renderer.enabled = shown;
                if (!shown) continue;
                Vector3 position = placement.position[cameraNumber], scale = placement.scale[cameraNumber];
                Quaternion rotation = placement.rotation[cameraNumber];
                if (placement.appliedCamera != cameraNumber || placement.appliedPosition != position ||
                    placement.appliedRotation != rotation || placement.appliedScale != scale)
                {
                    Transform transform = source.obj.transform;
                    transform.localPosition = position;
                    transform.localRotation = rotation;
                    transform.localScale = scale;
                    placement.Applied(cameraNumber);
                }
                if (placement.appliedLayer != layer)
                {
                    source.obj.layer = layer;
                    placement.appliedLayer = layer;
                }
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

        /// <summary>
        /// The camera viewing a room stands in for cameras[0] while vanilla code that
        /// only works for camera 0 runs (lights sample its palette, particles spawn
        /// around its position). A struct used as Begin, try { orig } finally { End }:
        /// these hooks run for every light and particle of every realized room, every
        /// tick (up to 1000 zero-g specks in one room), and the lambda wrapper made
        /// about four heap objects per call. It leaves curCamera alone, so a global the
        /// object writes stays in room scope and reaches every camera showing the room
        /// (AboveCloudsView's sky colours reached only the first).
        /// </summary>
        private struct PrimaryCameraSwap
        {
            private RoomCamera[] cameras;
            private int index;
            private RoomCamera primary;
            /// <summary>The first camera showing the room, or null.</summary>
            public RoomCamera viewing;

            public static PrimaryCameraSwap Begin(Room room)
            {
                var swap = new PrimaryCameraSwap();
                RoomCamera[] cameras = room?.game?.cameras;
                if (cameras == null) return swap;
                for (int i = 0; i < cameras.Length; i++)
                {
                    if (cameras[i] == null || cameras[i].room != room) continue;
                    swap.viewing = cameras[i];
                    if (i > 0)
                    {
                        swap.cameras = cameras;
                        swap.index = i;
                        swap.primary = cameras[0];
                        cameras[0] = cameras[i];
                        cameras[i] = swap.primary;
                    }
                    break;
                }
                return swap;
            }

            public void End()
            {
                if (cameras == null) return;
                cameras[0] = primary;
                cameras[index] = viewing;
                cameras = null;
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

        // The pass's 1x1 source and its material, one per camera and kept. Vanilla makes
        // a new temporary texture and material on every screen change and never frees
        // them (RemovePass and RemoveAllBuffers drop only the command buffer); per camera,
        // and again on every Auto rendering switch, that was ~4 leaked textures per screen
        // change in the log of 2026-09-19 (12 -> 124 "(unnamed)" render textures).
        private static readonly RenderTexture[] dynamicElementSources = new RenderTexture[4];
        private static readonly Material[] dynamicElementMaterials = new Material[4];

        private void DynamicLevelElement_AddLevelCombiner(On.Watcher.DynamicLevelElement.orig_AddLevelCombiner orig, RoomCamera camera)
        {
            if (camera?.levelTexCombiner == null || camera.levelTexCombiner.bufferIDs.Contains("DynamicLevelElement")) return;
            int number = camera.cameraNumber;
            if (number < 0 || number >= dynamicElementSources.Length) return;
            if (dynamicElementSources[number] == null)
                dynamicElementSources[number] = RenderTexture.GetTemporary(1, 1);
            if (dynamicElementMaterials[number] == null)
            {
                Shader shader = Shader.Find("Futile/DynamicLevelElementCombiner");
                if (shader == null) return;
                dynamicElementMaterials[number] = new Material(shader) { name = "SplitScreen dynamic elements " + number };
            }
            RenderTexture source = dynamicElementSources[number];
            Material material = dynamicElementMaterials[number];
            WithUnityMainCamera(camera, () => camera.levelTexCombiner.AddPass(source, material, "DynamicLevelElement", CameraEvent.AfterForwardOpaque));
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
            // Every flow object, every tick: no closures. The ripple data belongs to the
            // viewing camera, so its globals are recorded in that camera's scope.
            PrimaryCameraSwap swap = PrimaryCameraSwap.Begin(self.room);
            int previous = curCamera;
            try
            {
                if (swap.viewing != null) curCamera = swap.viewing.cameraNumber;
                RoomCamera camera = self.room?.game?.cameras?[0];
                if (camera != null && camera.rippleData == null)
                    camera.UpdateRippleData(self.room, camera.currentCameraPosition);
                orig(self, eu);
            }
            finally
            {
                curCamera = previous;
                swap.End();
            }
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

        // ---- Warp timer --------------------------------------------------------
        // A warp's screen effect runs on one WarpPointTimer, and vanilla always puts it
        // on cameras[0], whichever player warped (WarpPoint.ChangeState(EnterWarp),
        // OverWorld). Progress() advances it through the first half, then HOLDS at the
        // halfway point, the peak of the effect, until MovePastHalfPoint(); vanilla
        // calls that from cameras[0].MoveCamera(Room) and WarpMoveCameraActual, i.e.
        // "the camera has arrived in the destination". With one camera that always
        // happens during the hold. With a camera per player camera 0 can arrive early
        // (a move in the first half only adds one tick), or never change room at all:
        // its player was already in the destination room, or is dead, or the warp was
        // within one room. Then the timer holds for ever, and it is not a local effect:
        //  - every WarpPoint in the world asks cameras[0].warpPointTimer != null for
        //    warpSequenceInProgress, so all of them believe another warp is running;
        //  - camera 0's microphone stays muffled at the peak (globalSoundMuffle 1,
        //    warpTransition 4);
        //  - _playerWarpPointTime stays at 0.5, and RoomCamera.GetCameraBestIndex does
        //    not change screens within a room while WarpInProgress reads it >= 0.
        // (2026-09-22: "gate warp special effects sometimes linger after going through
        // gate, no matter where you are".) Once the players have been moved (the origin
        // warp point has left EnterWarp, which is where it waits for the destination to
        // load) and camera 0 has had two seconds to release it the vanilla way, release
        // it here, exactly as vanilla does: hold frame, then MovePastHalfPoint.
        private const int WarpHoldGraceTicks = 80;
        private Watcher.WarpPoint.WarpPointTimer trackedWarpTimer;
        private int warpHeldTicks;
        private int warpTimerStartFrame;

        private static string WarpDestination(Watcher.WarpPoint.WarpPointTimer timer)
        {
            Watcher.WarpPoint origin = timer?.origin_warpPoint;
            return origin?.overrideData?.destRoom ?? origin?.Data?.destRoom;
        }

        private void WatchWarpTimer(RainWorldGame game)
        {
            RoomCamera owner = game?.cameras != null && game.cameras.Length > 1 ? game.cameras[0] : null;
            Watcher.WarpPoint.WarpPointTimer timer = owner?.warpPointTimer;
            if (!ReferenceEquals(timer, trackedWarpTimer))
            {
                if (trackedWarpTimer != null)
                    Logger.LogInfo($"[Warp] frame={Time.frameCount} warp effect finished after {Time.frameCount - warpTimerStartFrame} frames; cam0 room={RoomName(owner?.room)}");
                trackedWarpTimer = timer;
                warpHeldTicks = 0;
                warpTimerStartFrame = Time.frameCount;
                if (timer != null)
                    Logger.LogInfo($"[Warp] frame={Time.frameCount} warp effect started (vanilla runs it on cam0 whoever warps); " +
                        $"origin={RoomName(timer.origin_warpPoint?.room)} dest={WarpDestination(timer) ?? "?"} cam0 room={RoomName(owner.room)} " +
                        $"cam0 follows p{PlayerNumber(owner.followAbstractCreature)} cutscene p{PlayerNumber(owner.cutscenePlayer)}");
            }
            if (timer == null || timer.finished) return;
            // Progress()'s own test for the hold: neither below nor above the halfway point.
            float half = timer.duration / 2f;
            bool holding = !(timer.progress < half) && !(timer.progress > half);
            if (!holding) { warpHeldTicks = 0; return; }
            Watcher.WarpPoint origin = timer.origin_warpPoint;
            bool moved = origin == null || origin.slatedForDeletetion || origin.room == null ||
                origin.currentState != Watcher.WarpPoint.State.EnterWarp;
            if (!moved) { warpHeldTicks = 0; return; } // still loading the destination: vanilla's hold
            if (++warpHeldTicks < WarpHoldGraceTicks) return;
            Logger.LogWarning($"[Warp] frame={Time.frameCount} released a warp effect held at its peak for {warpHeldTicks} ticks after the players were moved. " +
                $"Vanilla releases it only when cam0 changes room, and cam0 stayed in {RoomName(owner.room)} " +
                $"(dest={WarpDestination(timer) ?? "?"}, follows p{PlayerNumber(owner.followAbstractCreature)}, origin state={origin?.currentState})");
            warpHeldTicks = 0;
            RenderTexture previous = RenderTexture.active;
            try
            {
                // The hold frame blit reads camera globals; give it camera 0's.
                if (cameraListeners.Length > 0) cameraListeners[0]?.OnPreRender();
                owner.WarpPointHoldFrame();
            }
            catch (Exception error) { LogHookError("WatchWarpTimer.holdFrame", error); }
            finally { RenderTexture.active = previous; }
            timer.MovePastHalfPoint();
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
