using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;

namespace SplitScreenCoop
{
    static class DisplayExtensions
    {
        static ConditionalWeakTable<Display, SplitScreenCoop.DisplayExtras> map = new();
        public static SplitScreenCoop.DisplayExtras Extras(this Display display)
        {
            return map.GetValue(display, (e) => new SplitScreenCoop.DisplayExtras(e));
        }
    }

    public partial class SplitScreenCoop
    {
        public class DisplayExtras
        {
            public Display display;
            public RenderTexture renderTexture;
            private RawImage rawImage;
            /// <summary>
            /// A secondary display shows Futile's main screen (menus, one survivor)
            /// rather than its own camera texture. Kept here, not on a listener,
            /// because the camera bound to this display changes with every split-mode
            /// update and the mapping has to survive a screen-texture rebuild.
            /// </summary>
            public bool mirrorMain;
            public DisplayExtras(Display display)
            {
                this.display = display;
                displayExtras.Add(this);
                if (display == Display.main)
                {
                    rawImage = Futile.instance._cameraImage;
                }
                else
                {
                    var canvasHolder = GameObject.Instantiate(Futile.instance._cameraImage.transform.parent.gameObject); // dupe
                    var dummyCamera = canvasHolder.AddComponent<Camera>(); // its 2023 and unity still has this sort of bugs
                    dummyCamera.targetDisplay = 1;
                    dummyCamera.cullingMask = 0;
                    var canvas = canvasHolder.GetComponent<Canvas>();
                    canvas.targetDisplay = 1;
                    sLogger.LogInfo(canvas.isActiveAndEnabled);
                    rawImage = canvasHolder.GetComponentInChildren<RawImage>();
                    // The main image fills a canvas whose aspect is the main display's.
                    // On a second display of another aspect the copy would stretch;
                    // letterbox it to the main display's aspect instead.
                    var fitter = rawImage.GetComponent<AspectRatioFitter>() ?? rawImage.gameObject.AddComponent<AspectRatioFitter>();
                    fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                    fitter.aspectRatio = (float)Display.main.systemWidth / Mathf.Max(1, Display.main.systemHeight);
                }
                ReinitRenderTexture();
                SyncImageRect();
            }

            /// <summary>
            /// Futile crops its screen image (uvRect) for aspect irregularities in
            /// UpdateCameraPosition; the duplicate on a second display must crop alike.
            /// </summary>
            public void SyncImageRect()
            {
                if (display == Display.main || rawImage == null || Futile.instance?._cameraImage == null) return;
                rawImage.uvRect = Futile.instance._cameraImage.uvRect;
            }

            public void DiscardRenderTexture()
            {
                if (renderTexture != null)
                {
                    // Futile owns the primary render texture. Releasing it here used to
                    // invalidate the texture that FScreen had just recreated.
                    if (display != Display.main)
                    {
                        renderTexture.Release();
                        renderTexture.DiscardContents();
                    }
                    renderTexture = null;
                }
            }

            public void ReinitRenderTexture()
            {
                DiscardRenderTexture();
                if (display == Display.main)
                {
                    renderTexture = Futile.screen.renderTexture;
                }
                else
                {
                    renderTexture = new RenderTexture(Futile.screen.renderTexture);
                    rawImage.texture = mirrorMain ? Futile.screen.renderTexture : renderTexture;
                }
            }

            public void MapToTexture(RenderTexture renderTexture)
            {
                rawImage.texture = renderTexture;
                mirrorMain = renderTexture != null && renderTexture == Futile.screen?.renderTexture;
            }
        }
    }
}
