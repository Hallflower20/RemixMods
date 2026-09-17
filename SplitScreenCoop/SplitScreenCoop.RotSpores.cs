using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        /// <summary>
        /// SentientRotSpores (the Watcher rot particle cloud) keeps ONE mesh
        /// GameObject per room object and assumes one camera. Its InitiateSprites
        /// disposes that node before creating a new one, so when a second camera
        /// enters the room the first camera's sprite leaser is left with an empty
        /// container, and its next DrawSprites does GetChildAt(0) on it: an
        /// ArgumentOutOfRangeException inside RoomCamera.DrawUpdate, every frame,
        /// which stops the whole draw loop (black screen with audio; crash log of
        /// the sixth playtest). Give every camera its own renderer GameObject
        /// sharing one mesh and the object's particle buffer instead.
        /// </summary>
        private sealed class RotSporeRenderers
        {
            public Mesh mesh;
            public Texture atlasTexture;
            public readonly Dictionary<int, FGameObjectNode> nodes = new Dictionary<int, FGameObjectNode>();
        }

        private static readonly ConditionalWeakTable<SentientRotSpores, RotSporeRenderers> rotSporeRenderers =
            new ConditionalWeakTable<SentientRotSpores, RotSporeRenderers>();
        private static readonly FieldInfo rotSporeParticleBuffer =
            typeof(SentientRotSpores).GetField("particleBuffer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static Material rotSporeMaterial;

        private void SentientRotSpores_InitiateSprites(On.SentientRotSpores.orig_InitiateSprites orig,
            SentientRotSpores self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
        {
            if (self.slatedForDeletetion) return;
            if (!InitiateRotSporeRenderer(self, sLeaser, rCam))
            {
                orig(self, sLeaser, rCam);
            }
        }

        private static bool InitiateRotSporeRenderer(SentientRotSpores self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
        {
            var buffer = rotSporeParticleBuffer?.GetValue(self) as ComputeBuffer;
            if (buffer == null || rCam == null) return false;
            try
            {
                RotSporeRenderers state = rotSporeRenderers.GetValue(self, _ => new RotSporeRenderers());
                if (state.mesh == null) BuildRotSporeMesh(state, buffer.count);
                int number = rCam.cameraNumber;
                FGameObjectNode previous;
                if (state.nodes.TryGetValue(number, out previous) && previous != null)
                {
                    previous.RemoveFromContainer();
                    if (previous.gameObject) UnityEngine.Object.Destroy(previous.gameObject);
                }
                var gameObject = new GameObject("Rot Particle Renderer " + number, typeof(MeshFilter), typeof(MeshRenderer));
                gameObject.GetComponent<MeshFilter>().sharedMesh = state.mesh;
                MeshRenderer renderer = gameObject.GetComponent<MeshRenderer>();
                if (rotSporeMaterial == null) rotSporeMaterial = new Material(Shader.Find("Futile/RotParticleMesh"));
                renderer.sharedMaterial = rotSporeMaterial;
                var block = new MaterialPropertyBlock();
                block.SetTexture("_MainTex", state.atlasTexture);
                block.SetBuffer("_particleMap", buffer);
                renderer.SetPropertyBlock(block);
                var node = new FGameObjectNode(gameObject, true, true, true) { shouldDestroyOnRemoveFromStage = true };
                state.nodes[number] = node;
                sLeaser.sprites = new FSprite[0];
                sLeaser.containers = new[] { new FContainer() };
                sLeaser.containers[0].AddChild(node);
                self.AddToContainer(sLeaser, rCam, null);
                return true;
            }
            catch (Exception error)
            {
                LogHookError("SentientRotSpores_InitiateSprites", error);
                return false;
            }
        }

        /// <summary>Same quad-per-particle mesh vanilla builds, once per object.</summary>
        private static void BuildRotSporeMesh(RotSporeRenderers state, int count)
        {
            var vertices = new Vector3[count * 4];
            var uvs = new Vector2[count * 4];
            var indices = new int[count * 6];
            var elements = new List<FAtlasElement>(14);
            for (int i = 1; i <= 14; i++) elements.Add(Futile.atlasManager.GetElementWithName("Pebble" + i));
            int v = 0, u = 0, t = 0;
            for (int p = 0; p < count; p++)
            {
                indices[t++] = v; indices[t++] = v + 1; indices[t++] = v + 2;
                indices[t++] = v; indices[t++] = v + 2; indices[t++] = v + 3;
                FAtlasElement element = elements[UnityEngine.Random.Range(0, elements.Count)];
                float halfWidth = element.sourceRect.width / 2f, halfHeight = element.sourceRect.height / 2f;
                vertices[v++] = new Vector3(-halfWidth, -halfHeight, 0f);
                vertices[v++] = new Vector3(-halfWidth, halfHeight, 0f);
                vertices[v++] = new Vector3(halfWidth, halfHeight, 0f);
                vertices[v++] = new Vector3(halfWidth, -halfHeight, 0f);
                uvs[u++] = element.uvBottomLeft;
                uvs[u++] = element.uvTopLeft;
                uvs[u++] = element.uvTopRight;
                uvs[u++] = element.uvBottomRight;
            }
            state.mesh = new Mesh { name = "Rot Particle Mesh (split screen)" };
            state.mesh.SetVertices(vertices);
            state.mesh.SetUVs(0, uvs);
            state.mesh.SetIndices(indices, MeshTopology.Triangles, 0);
            state.mesh.bounds = new Bounds(Vector3.zero, new Vector3(100000f, 100000f, 100000f));
            state.atlasTexture = elements[0].atlas.texture;
        }

        private void SentientRotSpores_DrawSprites(On.SentientRotSpores.orig_DrawSprites orig, SentientRotSpores self,
            RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            if (self.slatedForDeletetion) return;
            // Whatever emptied this camera's container (vanilla's single node being
            // moved, or a destroyed GameObject), never let GetChildAt(0) throw
            // inside the draw loop: rebuild this camera's renderer first.
            if (sLeaser.containers == null || sLeaser.containers.Length == 0 || sLeaser.containers[0] == null ||
                sLeaser.containers[0].GetChildCount() == 0)
            {
                if (!InitiateRotSporeRenderer(self, sLeaser, rCam)) return;
            }
            orig(self, sLeaser, rCam, timeStacker, camPos);
        }

        private void SentientRotSpores_Destroy(On.SentientRotSpores.orig_Destroy orig, SentientRotSpores self)
        {
            orig(self);
            RotSporeRenderers state;
            if (!rotSporeRenderers.TryGetValue(self, out state)) return;
            foreach (FGameObjectNode node in state.nodes.Values)
                if (node != null && node.gameObject) UnityEngine.Object.Destroy(node.gameObject);
            state.nodes.Clear();
            if (state.mesh != null) UnityEngine.Object.Destroy(state.mesh);
            state.mesh = null;
            rotSporeRenderers.Remove(self);
        }
    }
}
