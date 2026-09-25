using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        /// <summary>
        /// Monobehavior that listen to camera events, so we can pre and post
        /// We store per-room-camera shader values and apply them per-unity-camera
        /// In split mode, Unity camera renders to an individual renderTexture and blits to the main tex post
        /// </summary>
        public class CameraListener : MonoBehaviour
        {
            public Camera fcamera;
            public Display display;
            public RenderTexture renderTexture;
            public RenderTexture tempTex;
            /// <summary>
            /// This camera's previous rendered frame, bound as _GrabTexture before it
            /// renders (see BindOwnGrab). Unity's GrabPass leaves the last grab bound
            /// as a global; a shader that reads _GrabTexture without taking its own
            /// grab therefore sees whatever camera grabbed last, and the only such
            /// shader drawn before this camera's first grab (DisplaySnowShader, queue
            /// AlphaTest) painted the snow with raw data in the twelfth playtest.
            /// </summary>
            public RenderTexture lastFrame;
            private static readonly int GrabTextureId = Shader.PropertyToID("_GrabTexture");
            private static Texture2D grabFloorTexture;
            private static Material grabFloorMaterial;
            public Dictionary<int, Color> ShaderColors = new Dictionary<int, Color>();
            public Dictionary<int, Vector4> ShaderVectors = new Dictionary<int, Vector4>();
            public Dictionary<int, Vector4[]> ShaderVectorArrays = new Dictionary<int, Vector4[]>();
            public Dictionary<int, List<Vector4>> ShaderVectorLists = new Dictionary<int, List<Vector4>>();
            public Dictionary<int, float> ShaderFloats = new Dictionary<int, float>();
            public Dictionary<int, Texture> ShaderTextures = new Dictionary<int, Texture>();
            public Dictionary<string, bool> ShaderKeywords = new Dictionary<string, bool>();
            public Rect sourceRect;
            public Rect targetRect;
            public int srcX;
            public int srcY;
            public int srcWidth;
            public int srcHeight;
            public int dstX;
            public int dstY;
            public int dstWidth;
            public int dstHeight;
            public int lastPreRenderFrame = -1;
            public int lastPostRenderFrame = -1;
            public int lastCompositeFrame = -1;
            public int renderingExpectedSinceFrame = -1;
            public int lastRecoveryFrame = -10000;


            public bool _direct = true;
            public bool dynamicCompositing;
            /// <summary>
            /// bypass intermediate rendertexture and blit
            /// </summary>
            public bool direct
            {
                get => _direct; set
                {
                    _direct = value;
                    Retarget();
                }
            }

            public bool mirrorMain
            {
                set
                {
                    if (display != Display.main)
                    {
                        fcamera.enabled = !value;
                        display.Extras().MapToTexture(value ? Futile.screen.renderTexture : display.Extras().renderTexture);
                    }
                }
            }

            public void Retarget()
            {
                if (fcamera == null || display == null) return;
                // Called several times per camera per tick; assign only on a change.
                RenderTexture target = _direct && !dynamicCompositing ? display.Extras().renderTexture : this.renderTexture;
                if (fcamera.targetTexture != target) fcamera.targetTexture = target;
            }


            /// <summary>
            /// Effectively ctor
            /// </summary>
            public void AttachTo(Camera fcamera, Display display)
            {
                this.fcamera = fcamera;
                this.display = display;
                ReinitRenderTexture();
                Retarget();
            }

            public void ReinitRenderTexture(bool reinitDisplay = true)
            {
                if (fcamera != null) fcamera.targetTexture = null;
                ReleaseTexture(ref renderTexture);
                ReleaseTexture(ref tempTex);
                if (display == null || Futile.screen?.renderTexture == null) return;
                if (reinitDisplay) display.Extras().ReinitRenderTexture();
                renderTexture = new RenderTexture(Futile.screen.renderTexture);
                renderTexture.name = $"SplitScreen camera {Array.IndexOf(cameraListeners, this)}";
                // As ReadSettings sets it. A new RenderTexture filters bilinear whatever
                // the one it copies, so Classic views went soft after every rebuild.
                renderTexture.filterMode = dynamicStyle && Options != null
                    ? Options.ZoomedFilter.Value == "Point" ? FilterMode.Point : FilterMode.Bilinear
                    : Futile.screen.renderTexture.filterMode;
                renderTexture.wrapMode = TextureWrapMode.Clamp;
                renderTexture.Create();
                SetMap(this.sourceRect, this.targetRect);
                Retarget();
            }

            public void MarkRenderingExpected(bool expected)
            {
                if (expected)
                {
                    if (renderingExpectedSinceFrame < 0) renderingExpectedSinceFrame = Time.frameCount;
                }
                else
                {
                    renderingExpectedSinceFrame = -1;
                }
            }

            public void PrepareForRendering()
            {
                if (Futile.screen?.renderTexture == null) return;
                bool invalid = renderTexture == null || !renderTexture.IsCreated() ||
                    renderTexture.width != Futile.screen.renderTexture.width ||
                    renderTexture.height != Futile.screen.renderTexture.height;
                if (invalid) ReinitRenderTexture(false);
                else Retarget();
            }

            public void RecoverRendering()
            {
                if (fcamera == null) return;
                bool shouldBeEnabled = fcamera.enabled;
                fcamera.enabled = false;
                ReinitRenderTexture(false);
                fcamera.enabled = shouldBeEnabled;
                renderingExpectedSinceFrame = Time.frameCount;
                lastRecoveryFrame = Time.frameCount;
            }

            private static void ReleaseTexture(ref RenderTexture texture)
            {
                if (texture == null) return;
                texture.Release();
                texture.DiscardContents();
                UnityEngine.Object.Destroy(texture);
                texture = null;
            }
            
            /// <summary>
            /// Camera.rect but for our custom blit
            /// </summary>
            public void SetMap(Rect sourceRect, Rect targetRect)
            {
                if (sourceRect.width <= 0f || sourceRect.height <= 0f) sourceRect = new Rect(0f, 0f, 1f, 1f);
                if (targetRect.width <= 0f || targetRect.height <= 0f) targetRect = new Rect(0f, 0f, 1f, 1f);
                var h = renderTexture.height;
                var w = renderTexture.width;
                srcX = Mathf.FloorToInt(w * sourceRect.x);
                srcY = Mathf.FloorToInt(h * sourceRect.y);
                srcWidth = Mathf.FloorToInt(w * sourceRect.width);
                srcHeight = Mathf.FloorToInt(h * sourceRect.height);
                dstX = Mathf.FloorToInt(w * targetRect.x);
                dstY = Mathf.FloorToInt(h * targetRect.y);
                dstWidth = Mathf.FloorToInt(w * targetRect.width);
                dstHeight = Mathf.FloorToInt(h * targetRect.height);
                this.sourceRect = sourceRect;
                this.targetRect = targetRect;
            }

            
            /// <summary>
            /// Apply shader vars from this roomcamera
            /// </summary>
            public void OnPreRender()
            {
                lastPreRenderFrame = Time.frameCount;
                // Only in a game. Menus set their own globals; replaying the game's over them
                // gave the sleep, death and fast-travel maps camera 0's last in-game map
                // values (_mapSize among them) whenever their region differed.
                if (!(rainworldGameObject?.processManager?.currentMainLoop is RainWorldGame)) return;
                long start = phaseWatch.ElapsedTicks;
                restoringShaderState = true;
                try
                {
                    foreach (var kv in ShaderColors) Shader.SetGlobalColor(kv.Key, kv.Value);
                    foreach (var kv in ShaderVectors) Shader.SetGlobalVector(kv.Key, kv.Value);
                    foreach (var kv in ShaderVectorArrays) Shader.SetGlobalVectorArray(kv.Key, kv.Value);
                    foreach (var kv in ShaderVectorLists) Shader.SetGlobalVectorArray(kv.Key, kv.Value);
                    foreach (var kv in ShaderFloats) Shader.SetGlobalFloat(kv.Key, kv.Value);
                    foreach (var kv in ShaderTextures) Shader.SetGlobalTexture(kv.Key, kv.Value);
                    foreach (var kv in ShaderKeywords) SetKeyword(kv.Key, kv.Value);
                    BindOwnGrab();
                }
                finally
                {
                    restoringShaderState = false;
                    // [Perf] replayMs: whether skipping unchanged values would be worth it.
                    frameReplayMs += (phaseWatch.ElapsedTicks - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    frameReplays++;
                }
            }

            /// <summary>
            /// The previous-frame capture and binding matter only with several cameras: a
            /// camera alone draws after its own last grab, as in vanilla. Solo play with the
            /// mod installed paid a full-screen copy and a blend every frame for nothing.
            /// </summary>
            private static bool SeveralCameras()
            {
                return rainworldGameObject?.processManager?.currentMainLoop is RainWorldGame game &&
                    game.cameras != null && game.cameras.Length > 1;
            }

            /// <summary>
            /// Vanilla semantics for a shader that samples _GrabTexture without a
            /// GrabPass of its own are "the previous grab": with one camera that is
            /// this camera's own scene, a frame old. With several cameras it was the
            /// last grab of whichever camera rendered before, and after a dark room
            /// it could be black, which DisplaySnowShader treats as "nothing behind
            /// me" and then draws its sky-sentinel pixels with an extrapolated fog
            /// colour (lime and red snow). Bind this camera's own last frame instead,
            /// lifted by 1% so no pixel is exactly black; white until it exists.
            /// Anything with a real GrabPass overwrites this when it draws.
            /// </summary>
            private void BindOwnGrab()
            {
                if (!SeveralCameras()) return;
                Shader.SetGlobalTexture(GrabTextureId,
                    lastFrame != null && lastFrame.IsCreated() ? (Texture)lastFrame : Texture2D.whiteTexture);
            }

            private void CaptureLastFrame()
            {
                if (!SeveralCameras()) return;
                RenderTexture source = fcamera != null ? fcamera.targetTexture : null;
                if (source == null || !source.IsCreated()) return;
                try
                {
                    if (lastFrame == null || lastFrame.width != source.width || lastFrame.height != source.height ||
                        lastFrame.format != source.format)
                    {
                        ReleaseTexture(ref lastFrame);
                        lastFrame = new RenderTexture(source.width, source.height, 0, source.format)
                        {
                            name = $"SplitScreen last frame {Array.IndexOf(cameraListeners, this)}",
                            filterMode = FilterMode.Point,
                            wrapMode = TextureWrapMode.Clamp
                        };
                        lastFrame.Create();
                    }
                    Graphics.CopyTexture(source, lastFrame);
                    if (grabFloorMaterial == null && FShader.Basic?.shader != null)
                    {
                        grabFloorTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false) { name = "SplitScreen grab floor" };
                        grabFloorTexture.SetPixel(0, 0, new Color(1f, 1f, 1f, 0.012f));
                        grabFloorTexture.Apply();
                        grabFloorMaterial = new Material(FShader.Basic.shader) { name = "SplitScreen grab floor" };
                    }
                    if (grabFloorMaterial != null)
                    {
                        // Basic blends SrcAlpha/OneMinusSrcAlpha: every channel becomes
                        // 0.988 * old + 0.012, so a pitch-black room still reads as "lit".
                        RenderTexture previous = RenderTexture.active;
                        Graphics.Blit(grabFloorTexture, lastFrame, grabFloorMaterial);
                        RenderTexture.active = previous;
                    }
                }
                catch (Exception error)
                {
                    sLogger?.LogWarning("[CameraLayout] last-frame copy failed: " + error.Message);
                    ReleaseTexture(ref lastFrame);
                }
            }

            /// <summary>
            /// Before this camera culls: Watcher mask meshes are shared between the
            /// cameras, so put each one where this camera's DrawUpdate asked for it.
            /// </summary>
            public void OnPreCull()
            {
                renderStartTicks = phaseWatch.ElapsedTicks;
                PlaceMaskSourcesFor(Array.IndexOf(cameraListeners, this));
            }

            /// <summary>Main-thread time from this camera's cull to its OnPostRender, for [FrameHitch] and [Perf].</summary>
            private long renderStartTicks = -1;

            private static void SetKeyword(string keyword, bool enabled)
            {
                if (enabled) Shader.EnableKeyword(keyword);
                else Shader.DisableKeyword(keyword);
            }

            /// <summary>
            /// Blit into display texture
            /// </summary>
            public void OnPostRender()
            {
                lastPostRenderFrame = Time.frameCount;
                if (renderStartTicks >= 0)
                {
                    frameRenderMs += (phaseWatch.ElapsedTicks - renderStartTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    renderStartTicks = -1;
                }
                CaptureLastFrame();
                PauseOverlayTick(this);
                if (dynamicCompositing) return; // The final compositor camera draws all polygons after split cameras render.
                if (!_direct)
                {
                    RenderTexture destination = display.Extras().renderTexture;
                    if (destination == null || srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0) return;
                    // Only forward what this camera just rendered. If the camera has
                    // been pointed elsewhere (a menu, after RestoreMenuCameras) the
                    // split texture is stale, and copying it here painted the last
                    // frame of the game over the sleep screen every frame.
                    if (fcamera == null || fcamera.targetTexture != renderTexture) return;

                    int cameraNumber = Array.IndexOf(cameraListeners, this);
                    if (renderedCameraNumbers.Count > 0 && cameraNumber == renderedCameraNumbers[0] && lastCompositorClearFrame != Time.frameCount)
                    {
                        var previous = RenderTexture.active;
                        Graphics.SetRenderTarget(destination);
                        GL.Clear(true, true, Color.black);
                        RenderTexture.active = previous;
                        lastCompositorClearFrame = Time.frameCount;
                    }

                    if (srcWidth != dstWidth || srcHeight != dstHeight)
                    {
                        if (tempTex == null || tempTex.width != dstWidth || tempTex.height != dstHeight)
                        {
                            ReleaseTexture(ref tempTex);
                            tempTex = new RenderTexture(dstWidth, dstHeight, 0, renderTexture.format)
                            {
                                filterMode = FilterMode.Point,
                                name = "SplitScreen scaled camera"
                            };
                            tempTex.Create();
                        }
                        Graphics.Blit(renderTexture, tempTex, new Vector2(sourceRect.width, sourceRect.height), new Vector2(sourceRect.x, sourceRect.y));
                        Graphics.CopyTexture(tempTex, 0, 0, 0, 0, dstWidth, dstHeight, destination, 0, 0, dstX, dstY);
                    }
                    else
                    {
                        Graphics.CopyTexture(renderTexture, 0, 0, srcX, srcY, srcWidth, srcHeight, destination, 0, 0, dstX, dstY);
                    }
                    lastCompositeFrame = Time.frameCount;
                }
                else lastCompositeFrame = Time.frameCount;
            }

            /// <summary>Forget every recorded global: per session, so nothing of the last game is replayed into the next.</summary>
            public void ClearRecordedShaderState()
            {
                ShaderColors.Clear();
                ShaderVectors.Clear();
                ShaderVectorArrays.Clear();
                ShaderVectorLists.Clear();
                ShaderFloats.Clear();
                ShaderTextures.Clear();
                ShaderKeywords.Clear();
            }

            public void OnDestroy()
            {
                ShaderTextures.Clear();
                ShaderKeywords.Clear();
                renderingExpectedSinceFrame = -1;
                if (fcamera != null) fcamera.targetTexture = null;
                fcamera = null;
                display = null;
                ReleaseTexture(ref renderTexture);
                ReleaseTexture(ref tempTex);
                ReleaseTexture(ref lastFrame);
            }

            internal void BindToDisplay(Display display)
            {
                this.display = display;
                Retarget();
            }
        }
    }
}
