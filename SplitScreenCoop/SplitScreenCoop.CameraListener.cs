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
                fcamera.targetTexture = _direct ? display.Extras().renderTexture : this.renderTexture;
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
                }
                finally
                {
                    restoringShaderState = false;
                }
            }

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
                if (!_direct)
                {
                    RenderTexture destination = display.Extras().renderTexture;
                    if (destination == null || srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0) return;

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
            }

            internal void BindToDisplay(Display display)
            {
                this.display = display;
                Retarget();
            }
        }
    }
}
