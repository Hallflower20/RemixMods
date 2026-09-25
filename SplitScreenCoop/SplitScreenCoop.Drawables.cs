using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        // ---- Drawables built for one camera ------------------------------------------
        // Vanilla rebuilds some objects' sprites inside DrawSprites from state kept on the
        // object instead of on the camera's sprite leaser, and only the first camera to draw
        // sees the rebuild: every later camera keeps sprites made for the old state. At best
        // they are stale; where the new state has more vertices or sprites, that camera's
        // DrawSprites indexes past its own and throws every frame from then on. RippleTree
        // did exactly that (log of 2026-09-22, dual displays and Adaptive: 12699 throws), and
        // because RoomCamera.DrawUpdate draws all its leasers in one loop, every sprite that
        // camera drew after the tree froze on screen and appeared to follow the view. Each
        // camera rebuilds its own leaser here, and SpriteLeaser_Update contains whatever
        // still throws (LogDrawableError).

        /// <summary>
        /// RippleTree (Watcher): its stalks and the scale they were generated for live on the
        /// tree. While a tree grows, DrawSprites rebuilds the first camera's meshes and records
        /// the scale, so no later camera ever rebuilds. A leaser whose meshes do not fit the
        /// current stalks takes vanilla's own rebuild path ("the scale changed"). The stalks
        /// come from the tree's seed, so a second rebuild in the same frame generates the same
        /// stalks and the first camera's meshes stay valid.
        /// </summary>
        private void RippleTree_DrawSprites(On.RippleTree.orig_DrawSprites orig, RippleTree self,
            RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            if (sLeaser != null && self.stalk != null && !RippleTreeMeshesMatch(self, sLeaser))
                self.lastUseScale = float.NaN; // equal to no scale: vanilla rebuilds this leaser
            orig(self, sLeaser, rCam, timeStacker, camPos);
        }

        private static bool RippleTreeMeshesMatch(RippleTree tree, RoomCamera.SpriteLeaser sLeaser)
        {
            FSprite[] sprites = sLeaser.sprites;
            if (tree.stalk.Length < 2) return sprites == null || sprites.Length == 0;
            if (sprites == null || sprites.Length != (tree.threeStems ? 7 : 2)) return false;
            for (int j = 0; j < (tree.threeStems ? 6 : 2); j++)
            {
                // As DrawSprites pairs them: 0-1 the stalk, 2-3 the left stalk, 4-5 the right.
                Vector2[] stalk = j < 2 ? tree.stalk : j < 4 ? tree.leftStalk : tree.rightStalk;
                if (stalk == null || !(sprites[j] is TriangleMesh mesh) ||
                    mesh.vertices.Length != OneToOneLongMeshVertices(stalk.Length)) return false;
            }
            return true;
        }

        /// <summary>Vertices of TriangleMesh.MakeOneToOneLongMesh(segments): two per segment, never fewer than three.</summary>
        private static int OneToOneLongMeshVertices(int segments)
        {
            return Math.Max(3, segments * 2);
        }

        /// <summary>
        /// FloatingDebris.Aurora (Watcher): a change in its floater count rebuilds the mesh on
        /// the next draw (needRefresh, cleared inside InitiateSprites), for the first camera
        /// only; a later camera kept a mesh of the old size and moved vertices past its end.
        /// A leaser whose sprite does not fit the current point count is refreshed.
        /// </summary>
        private void Aurora_DrawSprites(On.Watcher.FloatingDebris.Aurora.orig_DrawSprites orig, Watcher.FloatingDebris.Aurora self,
            RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            if (sLeaser?.sprites != null && sLeaser.sprites.Length > 0 && !AuroraMeshMatches(self, sLeaser.sprites[0]))
                self.needRefresh = true;
            orig(self, sLeaser, rCam, timeStacker, camPos);
        }

        private static bool AuroraMeshMatches(Watcher.FloatingDebris.Aurora aurora, FSprite sprite)
        {
            // No points: a plain sprite. Otherwise (points - 1) * 2 triangles over two vertices per point.
            if (aurora.numberOfPoints == 0) return !(sprite is TriangleMesh);
            return sprite is TriangleMesh mesh && mesh.vertices.Length == Math.Max(3, aurora.numberOfPoints * 2);
        }

        // ---- One-shot rebuild flags ----------------------------------------------------
        // WarpPoint.refreshGraphics (a warp locked or sealed), Spear.reinitiateSpritesOnDraw
        // (its poison wore off), DangleFruit.convertToRot (a fruit rotting in a rot room),
        // Pomegranate.refreshSprites (smashed), TerrainCurve.spritesDirty (resized) and
        // SaintsJourneyIllustration's imageDirty: each is set once and consumed by the first
        // camera that draws the object. A request stays owed to every other camera until each
        // has drawn with it; vanilla's own branch does the rebuild.
        private sealed class OwedRebuild { public int cameras; }
        private static ConditionalWeakTable<object, OwedRebuild> owedRebuilds = new ConditionalWeakTable<object, OwedRebuild>();
        private static readonly ConditionalWeakTable<object, OwedRebuild>.CreateValueCallback NewOwedRebuild = key => new OwedRebuild();
        /// <summary>Requests still owed to some camera. While there are none a draw never touches the table.</summary>
        private static int owedRebuildRequests;

        /// <summary>Per session: nothing the last game asked for is owed in the next.</summary>
        private static void ForgetOwedRebuilds()
        {
            owedRebuilds = new ConditionalWeakTable<object, OwedRebuild>();
            owedRebuildRequests = 0;
        }

        /// <summary>
        /// Whether this camera's draw must rebuild. <paramref name="requested"/> is the flag as
        /// the draw finds it: set, it is a new request, owed from now on to every other camera;
        /// clear, an earlier camera may have consumed a request this camera still owes.
        /// </summary>
        private static bool RebuildOwed(object drawable, bool requested, RoomCamera rCam)
        {
            RoomCamera[] cameras = rCam?.game?.cameras;
            if (cameras == null || cameras.Length < 2 || rCam.cameraNumber < 0 || rCam.cameraNumber > 30) return requested;
            OwedRebuild owed;
            if (requested)
            {
                int others = 0;
                for (int i = 0; i < cameras.Length; i++)
                    if (cameras[i] != null && cameras[i] != rCam && cameras[i].cameraNumber >= 0 && cameras[i].cameraNumber <= 30)
                        others |= 1 << cameras[i].cameraNumber;
                owed = owedRebuilds.GetValue(drawable, NewOwedRebuild);
                if (owed.cameras == 0 && others != 0) owedRebuildRequests++;
                else if (owed.cameras != 0 && others == 0) owedRebuildRequests--;
                owed.cameras = others;
                return true;
            }
            int self = 1 << rCam.cameraNumber;
            if (owedRebuildRequests <= 0 || !owedRebuilds.TryGetValue(drawable, out owed) || (owed.cameras & self) == 0)
                return false;
            owed.cameras &= ~self;
            if (owed.cameras == 0) owedRebuildRequests--;
            return true;
        }

        private void WarpPoint_DrawSprites(On.Watcher.WarpPoint.orig_DrawSprites orig, Watcher.WarpPoint self,
            RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            self.refreshGraphics = RebuildOwed(self, self.refreshGraphics, rCam);
            orig(self, sLeaser, rCam, timeStacker, camPos);
        }

        private void Spear_DrawSprites(On.Spear.orig_DrawSprites orig, Spear self,
            RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            self.reinitiateSpritesOnDraw = RebuildOwed(self, self.reinitiateSpritesOnDraw, rCam);
            orig(self, sLeaser, rCam, timeStacker, camPos);
        }

        private void DangleFruit_DrawSprites(On.DangleFruit.orig_DrawSprites orig, DangleFruit self,
            RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            self.convertToRot = RebuildOwed(self, self.convertToRot, rCam);
            orig(self, sLeaser, rCam, timeStacker, camPos);
        }

        private void Pomegranate_DrawSprites(On.Pomegranate.orig_DrawSprites orig, Pomegranate self,
            RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            self.refreshSprites = RebuildOwed(self, self.refreshSprites, rCam);
            orig(self, sLeaser, rCam, timeStacker, camPos);
        }

        private void TerrainCurve_DrawSprites(On.TerrainCurve.orig_DrawSprites orig, TerrainCurve self,
            RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            self.spritesDirty = RebuildOwed(self, self.spritesDirty, rCam);
            orig(self, sLeaser, rCam, timeStacker, camPos);
        }

        private void SaintsJourneyIllustration_DrawSprites(On.BackgroundScene.SaintsJourneyIllustration.orig_DrawSprites orig,
            BackgroundScene.SaintsJourneyIllustration self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            self.imageDirty = RebuildOwed(self, self.imageDirty, rCam);
            orig(self, sLeaser, rCam, timeStacker, camPos);
        }

        // ---- Containment ----------------------------------------------------------------
        private static readonly Dictionary<Type, int> drawableErrorLogged = new Dictionary<Type, int>();
        private static readonly Dictionary<Type, int> drawableErrorCounts = new Dictionary<Type, int>();

        /// <summary>
        /// A drawable threw in DrawSprites. Its own sprites keep this frame's state; the
        /// camera still draws everything else. Logged per type, at most every 10 s, with the
        /// running count, so a new one-camera assumption names itself in the next log.
        /// </summary>
        internal static void LogDrawableError(RoomCamera.SpriteLeaser leaser, RoomCamera rCam, Exception error)
        {
            Type type = leaser?.drawableObject?.GetType() ?? typeof(RoomCamera.SpriteLeaser);
            int count;
            drawableErrorCounts.TryGetValue(type, out count);
            drawableErrorCounts[type] = ++count;
            int last;
            if (drawableErrorLogged.TryGetValue(type, out last) && Time.frameCount - last < FramesFor(10f)) return;
            drawableErrorLogged[type] = Time.frameCount;
            sLogger?.LogError($"[HookError] frame={Time.frameCount} {type.FullName}.DrawSprites threw on camera {rCam?.cameraNumber ?? -1} (x{count}); that camera still draws everything else: {error}");
        }
    }
}
