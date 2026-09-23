using System;
using System.Collections.Generic;
using RWCustom;
using UnityEngine;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        // ---- Adaptive split style --------------------------------------------------
        // Decisions live in AdaptiveLayout (Unity-free, covered by the layout tests):
        // one full view while everyone is on one prebaked screen, otherwise halves or a
        // still grid of quarters, zooms between the two, reflow on deaths. This file
        // feeds it once per tick, animates it once per frame, and draws it with the
        // dynamic pipeline's cameras, HUD textures and compositor. It also fills
        // dynamicLayout, so everything else that reads the layout (audio, the HUD and
        // camera enabling, the debug overlay) works unchanged.
        public static bool adaptiveStyle;
        internal const string SpareMapAndMeters = "Map and meters";
        internal const string SpareMetersOnly = "Meters only";
        internal const string SpareBlack = "Black";
        public static string spareQuarterMode = SpareMapAndMeters;

        private readonly AdaptiveLayout adaptiveLayout = new AdaptiveLayout();
        private readonly AdaptiveLayout.Settings adaptiveSettings = new AdaptiveLayout.Settings();
        private readonly List<AdaptiveLayout.Player> adaptivePlayers = new List<AdaptiveLayout.Player>(4);
        private readonly List<AdaptiveLayout.View> adaptiveDrawOrder = new List<AdaptiveLayout.View>(5);
        /// <summary>Per camera: whose render its view draws (players on one screen share a picture).</summary>
        private readonly int[] adaptiveImageCameras = { -1, -1, -1, -1 };
        /// <summary>Per camera: the screen its view's picture showed last frame; a change is a cut, and the framing snaps.</summary>
        private readonly long[] adaptivePictureKeys = { long.MinValue, long.MinValue, long.MinValue, long.MinValue };
        private readonly Vector2[] adaptivePolygon = new Vector2[4];
        private const float HalfWidth = 0.5f;

        // The shared meters (food, karma, rain) sit in the spare quarter when there is
        // one. They are routed into their own container of the global HUD stage, below
        // the pause menu, which the offset moves.
        private FContainer globalPromptContainer;
        private FContainer globalMeterContainer;
        private Vector2 meterOffset, meterOffsetVelocity;

        // The group map in the spare quarter, in map units (one per tile).
        private Vector2 groupMapCenter, groupMapCenterVelocity;
        private float groupMapWidth, groupMapWidthVelocity;
        private bool groupMapFramed;
        private const float GroupMapMargin = 45f;
        private const float GroupMapMinWidth = 220f;
        private readonly HashSet<string> visitedRooms = new HashSet<string>();
        private int visitedRoomsRegion = -2, visitedRoomsCount = -1;
        private static readonly Color[] FallbackPlayerColors =
        {
            new Color(1f, 1f, 1f), new Color(1f, 1f, 0.45f), new Color(1f, 0.45f, 0.45f), new Color(0.4f, 0.65f, 1f)
        };

        private void ResetAdaptiveLayout()
        {
            adaptiveLayout.Reset();
            adaptiveSnapshot = null;
            ResetHudCameraModes();
            adaptivePlayers.Clear();
            for (int i = 0; i < 4; i++)
            {
                adaptiveImageCameras[i] = -1;
                adaptivePictureKeys[i] = long.MinValue;
            }
            meterOffset = meterOffsetVelocity = Vector2.zero;
            globalMeterContainer?.SetPosition(Vector2.zero);
            groupMapFramed = false;
            visitedRooms.Clear();
            visitedRoomsRegion = -2;
            visitedRoomsCount = -1;
        }

        /// <summary>
        /// Equal for cameras that show the same prebaked screen. A camera still loading
        /// is keyed on the room it is heading for, so a group taking a pipe together
        /// stays together. "Permanent split" gives everyone their own key: no merging.
        /// </summary>
        private long AdaptiveScreenKey(RainWorldGame game, RoomCamera camera, int number)
        {
            if (NeverMerge) return long.MinValue + 200000L + number;
            AbstractCreature player = GetPlayerForCamera(game, number);
            Room room = player?.realizedCreature?.room ?? camera?.room;
            if (room != null && camera?.room == room) return ScreenKey(room, camera.currentCameraPosition);
            AbstractRoom pending = camera?.loadingRoom?.abstractRoom ?? camera?.room?.abstractRoom;
            return pending != null ? long.MinValue + 1L + pending.index : long.MinValue + 100000L + number;
        }

        private static bool SameScreen(RoomCamera a, RoomCamera b)
        {
            return a?.room != null && b?.room != null && a.room == b.room && a.currentCameraPosition == b.currentCameraPosition;
        }

        /// <summary>Once per tick, from UpdateDynamicLayout.</summary>
        private void UpdateAdaptiveLayout(RainWorldGame game, List<int> aliveCameras)
        {
            adaptivePlayers.Clear();
            foreach (int number in aliveCameras)
            {
                if (number < 0 || number >= 4) continue;
                RoomCamera camera = CameraByNumber(game, number);
                adaptivePlayers.Add(new AdaptiveLayout.Player { camera = number, screenKey = AdaptiveScreenKey(game, camera, number) });
            }
            if (adaptivePlayers.Count == 0) return;
            float tick = 1f / Mathf.Clamp(game.framesPerSecond, 1, 400);
            HangMarker = "UpdateAdaptiveLayout";
            adaptiveLayout.Update(adaptivePlayers, tick, adaptiveSettings);
            if (adaptiveLayout.lastEvent.Length > 0) LogAdaptiveEvent(game);

            // Whose picture each view shows. In one view that is the owner's. Split,
            // players on one screen share the lowest camera's render: the same picture
            // (up to the cameras' 20 px of follow slack), rendered once.
            for (int i = 0; i < 4; i++) adaptiveImageCameras[i] = -1;
            foreach (AdaptiveLayout.Player player in adaptivePlayers)
            {
                int image = player.camera;
                if (adaptiveLayout.full && adaptiveLayout.fullCamera >= 0) image = adaptiveLayout.fullCamera;
                else
                {
                    RoomCamera own = CameraByNumber(game, player.camera);
                    foreach (AdaptiveLayout.Player other in adaptivePlayers)
                        if (other.camera < image && SameScreen(own, CameraByNumber(game, other.camera))) image = other.camera;
                }
                adaptiveImageCameras[player.camera] = image;
            }
            BuildAdaptiveDynamicLayout();
            SelectGlobalHudSources(game, aliveCameras);
            RouteGlobalMeters(game);
            ApplyDynamicCameraRendering(game);
            HangMarker = "idle";
        }

        private SplitLayoutSolver.Layout adaptiveSnapshot;

        /// <summary>
        /// The dynamic pipeline's view of the layout, for its other readers: audio mutes
        /// duplicate listeners through sharesImageWith, the camera and HUD enabling reads
        /// the viewports and base cameras, the debug overlay prints them.
        /// </summary>
        private void BuildAdaptiveDynamicLayout()
        {
            int count = adaptivePlayers.Count;
            // Rebuilt 40 times a second: keep the objects, refill them.
            if (adaptiveSnapshot == null || adaptiveSnapshot.viewports.Length != count)
            {
                adaptiveSnapshot = new SplitLayoutSolver.Layout
                {
                    viewports = new SplitLayoutSolver.ViewportState[count],
                    dividers = new SplitLayoutSolver.DividerSegment[0],
                    pairSplitAmounts = new float[count, count],
                    effectiveInputs = new SplitLayoutSolver.PlayerInput[count],
                };
                for (int i = 0; i < count; i++)
                    adaptiveSnapshot.viewports[i] = new SplitLayoutSolver.ViewportState
                        { polygon = new Vector2[4], targetPolygon = new Vector2[4] };
            }
            var viewports = adaptiveSnapshot.viewports;
            if (baseCameraNumbers.Length != count) baseCameraNumbers = new int[count];
            var bases = baseCameraNumbers;
            for (int i = 0; i < lastBaseByCamera.Length; i++) lastBaseByCamera[i] = -1;
            for (int i = 0; i < count; i++)
            {
                int camera = adaptivePlayers[i].camera;
                AdaptiveLayout.View view = adaptiveLayout.ViewOf(camera);
                AdaptiveLayout.Box cell = view?.cell ?? AdaptiveLayout.Box.Full;
                AdaptiveLayout.Box target = view?.TargetCell ?? cell;
                int image = adaptiveImageCameras[camera] >= 0 ? adaptiveImageCameras[camera] : camera;
                bases[i] = image;
                lastBaseByCamera[camera] = image;
                SplitLayoutSolver.ViewportState viewport = viewports[i];
                viewport.cameraNumber = camera;
                viewport.ghost = false;
                viewport.rendering = view != null && view.visible;
                viewport.sharesImageWith = image != camera ? image : -1;
                FillPolygon(viewport.polygon, cell);
                FillPolygon(viewport.targetPolygon, target);
                viewport.centroid = viewport.site = viewport.regionAnchor = cell.Center;
                // The smaller of now and target, so a camera is filtered for minifying
                // for the whole of a zoom into or out of a quarter.
                viewport.zoom = view == null ? 1f : Mathf.Min(view.zoom, view.TargetZoom);
                viewport.splitAmount = adaptiveLayout.full ? 0f : 1f;
                viewport.areaFraction = viewport.groupAreaFraction = cell.w * cell.h;
            }
            dynamicLayout = adaptiveSnapshot;
            dynamicInputs = adaptiveSnapshot.effectiveInputs;
            if (ownScreenPositions.Length != count) ownScreenPositions = new Vector2[count];
        }

        private static void FillPolygon(Vector2[] polygon, AdaptiveLayout.Box cell)
        {
            polygon[0] = new Vector2(cell.x, cell.y);
            polygon[1] = new Vector2(cell.xMax, cell.y);
            polygon[2] = new Vector2(cell.xMax, cell.yMax);
            polygon[3] = new Vector2(cell.x, cell.yMax);
        }

        /// <summary>
        /// The Unity cameras that render: only the owner's while one view is shown and
        /// nothing moves, otherwise the picture of every drawn view (the spare quarter
        /// has none). Returns true for the single-view case.
        /// </summary>
        private bool FillAdaptiveRenderedCameras()
        {
            renderedCameraNumbers.Clear();
            if (adaptiveLayout.FullAtRest && adaptiveLayout.fullCamera >= 0)
            {
                renderedCameraNumbers.Add(adaptiveLayout.fullCamera);
                return true;
            }
            foreach (AdaptiveLayout.View view in adaptiveLayout.views)
            {
                if (!view.visible || view.camera < 0 || view.camera >= 4) continue;
                int image = adaptiveImageCameras[view.camera] >= 0 ? adaptiveImageCameras[view.camera] : view.camera;
                if (!renderedCameraNumbers.Contains(image)) renderedCameraNumbers.Add(image);
            }
            if (renderedCameraNumbers.Count == 0 && adaptiveLayout.fullCamera >= 0) renderedCameraNumbers.Add(adaptiveLayout.fullCamera);
            renderedCameraNumbers.Sort();
            return false;
        }

        /// <summary>Once per rendered frame, before the game draws: framing, animation, meters, map, HUD camera modes.</summary>
        private void RefreshAdaptiveLayout(RainWorldGame game, float timeStacker)
        {
            // With nobody alive (adaptivePlayers empty, the layout frozen) transitions still
            // run to their end rather than stopping mid-slide.
            if (!dynamicActive || game?.cameras == null || adaptiveLayout.views.Count == 0) return;
            float dt = Mathf.Clamp(Time.deltaTime, 0.0001f, 0.1f);
            foreach (AdaptiveLayout.Player player in adaptivePlayers)
            {
                AdaptiveLayout.View view = adaptiveLayout.ViewOf(player.camera);
                if (view == null) continue;
                int image = adaptiveImageCameras[player.camera] >= 0 ? adaptiveImageCameras[player.camera] : player.camera;
                RoomCamera imageCamera = CameraByNumber(game, image);
                if (imageCamera?.room == null) continue;
                long pictureKey = ScreenKey(imageCamera.room, imageCamera.currentCameraPosition);
                bool cut = pictureKey != adaptivePictureKeys[player.camera];
                Vector2 source;
                if (!TryGetInterpolatedScreenPosition(imageCamera, GetPlayerForCamera(game, player.camera), timeStacker, out source)) continue;
                // Only now: a cut seen on a frame without a position (the player in a pipe)
                // must still snap the framing on the next frame, not turn into a swipe.
                adaptivePictureKeys[player.camera] = pictureKey;
                float framing = view.framing < 0f || cut
                    ? AdaptiveLayout.BestFraming(source.x, HalfWidth)
                    : AdaptiveLayout.StepFraming(view.framing, source.x, HalfWidth, adaptiveSettings.edgeMargin);
                adaptiveLayout.SetFraming(player.camera, framing, cut, adaptiveSettings);
            }
            adaptiveLayout.Animate(dt);

            AdaptiveLayout.View spare = adaptiveLayout.spare;
            Vector2 meterTarget = Vector2.zero;
            if (spare != null && spare.visible && !spare.leaving && spareQuarterMode != SpareBlack)
                meterTarget = new Vector2(spare.cell.x * game.rainWorld.screenSize.x, spare.cell.y * game.rainWorld.screenSize.y);
            meterOffset.x = Mathf.SmoothDamp(meterOffset.x, meterTarget.x, ref meterOffsetVelocity.x, 0.12f, Mathf.Infinity, dt);
            meterOffset.y = Mathf.SmoothDamp(meterOffset.y, meterTarget.y, ref meterOffsetVelocity.y, 0.12f, Mathf.Infinity, dt);
            globalMeterContainer?.SetPosition(new Vector2(Mathf.Round(meterOffset.x), Mathf.Round(meterOffset.y)));
            UpdateGroupMapFraming(game, dt);
            ApplyAdaptiveHudMode();
        }

        private void LogAdaptiveEvent(RainWorldGame game)
        {
            var screens = new List<string>(4);
            foreach (AdaptiveLayout.Player player in adaptivePlayers)
            {
                RoomCamera camera = CameraByNumber(game, player.camera);
                screens.Add($"p{player.camera}={RoomName(camera?.room)}:{camera?.currentCameraPosition}");
            }
            var cells = new List<string>(5);
            foreach (AdaptiveLayout.View view in adaptiveLayout.views)
                cells.Add($"{(view.camera < 0 ? "spare" : "p" + view.camera)}{view.TargetCell}{(view.visible ? "" : " hidden")}{(view.native ? " half" : "")}");
            Logger.LogInfo($"[CameraLayout] frame={Time.frameCount} adaptive {adaptiveLayout.lastEvent}: shape={adaptiveLayout.shape} " +
                $"oneView={adaptiveLayout.full} owner=p{adaptiveLayout.fullCamera} screens=[{string.Join(", ", screens)}] views=[{string.Join(", ", cells)}]");
        }

        /// <summary>
        /// Hypothermia meter: a half view shows a window of its HUD, so a meter drawn at
        /// the HUD's bottom-left needs to move by that window to sit in the half's own
        /// corner. Quarters show the whole HUD at half size and need nothing.
        /// </summary>
        private Vector2 AdaptiveHudOffset(RoomCamera camera)
        {
            AdaptiveLayout.View view = adaptiveLayout.ViewOf(camera.cameraNumber);
            if (view == null || !view.visible || adaptiveLayout.FullAtRest) return Vector2.zero;
            return new Vector2(view.window.x * camera.sSize.x, view.window.y * camera.sSize.y);
        }

        // ---- Drawing ---------------------------------------------------------------

        private void CompositeAdaptiveLayout(DynamicCompositor compositor)
        {
            if (!dynamicActive || compositor.texturedMaterial == null) return;
            HangMarker = "CompositeAdaptiveLayout";
            RenderTexture destination = Display.main.Extras().renderTexture;
            if (destination == null) return;
            RenderTexture previous = RenderTexture.active;
            Graphics.SetRenderTarget(destination);
            GL.PushMatrix();
            try
            {
                GL.LoadOrtho();
                GL.Clear(true, true, Color.black);
                adaptiveDrawOrder.Clear();
                // Add one by one: AddRange of another list makes a temporary array (every frame here).
                for (int i = 0; i < adaptiveLayout.views.Count; i++) adaptiveDrawOrder.Add(adaptiveLayout.views[i]);
                // Stable by order: the spare first, the growing or shrinking view last.
                for (int i = 1; i < adaptiveDrawOrder.Count; i++)
                    for (int j = i; j > 0 && adaptiveDrawOrder[j - 1].order > adaptiveDrawOrder[j].order; j--)
                    {
                        AdaptiveLayout.View swap = adaptiveDrawOrder[j];
                        adaptiveDrawOrder[j] = adaptiveDrawOrder[j - 1];
                        adaptiveDrawOrder[j - 1] = swap;
                    }
                bool oneViewAtRest = adaptiveLayout.FullAtRest;
                RainWorldGame game = rainworldGameObject?.processManager?.currentMainLoop as RainWorldGame;
                // While views slide after a death or a revival, the dead view is gone at once
                // and the survivors are on their way: draw each where it is going first, so
                // the screen under the slide is the layout it becomes, not black.
                if (!oneViewAtRest && AdaptiveViewsSliding())
                    foreach (AdaptiveLayout.View view in adaptiveDrawOrder)
                        if (view.visible && view.camera >= 0)
                            DrawAdaptiveView(compositor, view, view.TargetCell, view.TargetZoom, view.TargetWindow);
                foreach (AdaptiveLayout.View view in adaptiveDrawOrder)
                {
                    if (!view.visible || view.cell.w <= 0.0001f || view.cell.h <= 0.0001f) continue;
                    if (view.camera < 0)
                    {
                        if (spareQuarterMode == SpareMapAndMeters && game != null) DrawGroupMapSafely(compositor.solidMaterial, view.cell, game);
                        DrawAdaptiveBorder(compositor.solidMaterial, view.cell);
                        continue;
                    }
                    if (oneViewAtRest) DrawAdaptiveImage(compositor.texturedMaterial, view);
                    else DrawAdaptiveView(compositor, view, view.cell, view.zoom, view.window);
                    DrawAdaptiveBorder(compositor.solidMaterial, view.cell);
                }
                // One view: every player's HUD over the whole screen, as when merged in Dynamic.
                // With "HUD drawn onto its view" the HUD cameras draw straight onto the screen
                // after this (ApplyAdaptiveHudMode) instead of through their textures.
                if (oneViewAtRest)
                    foreach (AdaptiveLayout.Player player in adaptivePlayers)
                        if (player.camera < 0 || player.camera >= hudDirect.Length || !hudDirect[player.camera])
                            DrawAdaptiveHud(compositor.texturedMaterial, player.camera, AdaptiveLayout.Box.Full, 1f, Vector2.zero);
                if (dynamicDebugOverlay) DrawDynamicOutlines(compositor.solidMaterial);
                compositor.lastCompositeFrame = Time.frameCount;
                compositorRecoveries = 0;
                for (int i = 0; i < renderedCameraNumbers.Count; i++)
                    cameraListeners[renderedCameraNumbers[i]].lastCompositeFrame = Time.frameCount;
            }
            catch (Exception error)
            {
                Logger.LogError($"[CameraLayout] adaptive compositor error: {error}");
                dynamicPipelineFailed = true;
            }
            finally
            {
                GL.PopMatrix();
                RenderTexture.active = previous;
                HangMarker = "idle";
            }
        }

        private Vector2[] AdaptivePolygon(AdaptiveLayout.Box cell)
        {
            FillPolygon(adaptivePolygon, cell);
            return adaptivePolygon;
        }

        /// <summary>texcoord = window + (p - cell.min) / zoom, in DrawDynamicPolygon's p / zoom + shift form.</summary>
        private static Vector2 AdaptiveShift(AdaptiveLayout.Box cell, float zoom, Vector2 window)
        {
            return window - cell.Min / Mathf.Max(0.00001f, zoom);
        }

        /// <summary>
        /// The render view <paramref name="own"/> shows: its image camera's if that rendered
        /// within a round of the rotation, else its own camera's; failing both (the first
        /// frames after a camera is switched on) the one it drew last frame, rather than a
        /// black cell. SelectFrameCamera gives a camera just switched on the next turn, so
        /// that fallback lasts at most the frame it was switched on in.
        /// </summary>
        private CameraListener AdaptivePictureListener(int own)
        {
            int image = own >= 0 && own < 4 && adaptiveImageCameras[own] >= 0 ? adaptiveImageCameras[own] : own;
            CameraListener listener = FreshListener(image) ?? FreshListener(own);
            if (listener == null && own >= 0 && own < lastDrawnListeners.Length && lastDrawnListeners[own]?.renderTexture != null)
                listener = lastDrawnListeners[own];
            return listener;
        }

        private void DrawAdaptiveImage(Material material, AdaptiveLayout.View view)
        {
            DrawAdaptiveImageAt(material, view.camera, view.cell, view.zoom, view.window);
        }

        private void DrawAdaptiveImageAt(Material material, int own, AdaptiveLayout.Box cell, float zoom, Vector2 window)
        {
            CameraListener listener = AdaptivePictureListener(own);
            if (listener == null) return;
            if (own >= 0 && own < lastDrawnListeners.Length) lastDrawnListeners[own] = listener;
            DrawDynamicPolygon(material, listener.renderTexture, AdaptivePolygon(cell), AdaptiveShift(cell, zoom, window), 1f, zoom);
        }

        private void DrawAdaptiveHud(Material material, int camera, AdaptiveLayout.Box cell, float zoom, Vector2 window)
        {
            if (camera < 0 || camera >= hudTextures.Length || hudTextures[camera] == null) return;
            DrawDynamicPolygon(material, hudTextures[camera], AdaptivePolygon(cell), AdaptiveShift(cell, zoom, window), 1f, zoom);
        }

        /// <summary>
        /// One view at a given cell and mapping, its HUD riding on it (so a view covering
        /// another covers its HUD too): picture and HUD in one opaque draw when the view's
        /// HUD camera has drawn it onto its picture, else the picture with the HUD texture
        /// blended over it.
        /// </summary>
        private void DrawAdaptiveView(DynamicCompositor compositor, AdaptiveLayout.View view,
            AdaptiveLayout.Box cell, float zoom, Vector2 window)
        {
            int own = view.camera;
            if (HudCombinedReady(own) && compositor.opaqueMaterial != null)
            {
                CameraListener listener = AdaptivePictureListener(own);
                if (listener != null && own < lastDrawnListeners.Length) lastDrawnListeners[own] = listener;
                DrawDynamicPolygon(compositor.opaqueMaterial, hudTextures[own], AdaptivePolygon(cell), AdaptiveShift(cell, zoom, window), 1f, zoom);
                return;
            }
            DrawAdaptiveImageAt(compositor.texturedMaterial, own, cell, zoom, window);
            DrawAdaptiveHud(compositor.texturedMaterial, own, cell, zoom, window);
        }

        /// <summary>A view's cell is on its way somewhere (a reflow or a zoom), not just its window.</summary>
        private bool AdaptiveViewsSliding()
        {
            foreach (AdaptiveLayout.View view in adaptiveLayout.views)
                if (view.visible && view.camera >= 0 && !view.cell.Approximately(view.TargetCell)) return true;
            return false;
        }

        // ---- HUD drawn onto its view ("HUD drawn onto its view", Adaptive) ------------
        // The per-player HUD used to be drawn by its camera into a cleared, transparent
        // texture and that texture blended over the picture: every see-through HUD sprite
        // was blended twice and came out dark (what greyed the shared meters until they got
        // a camera of their own), and in quarters it was point-sampled at half size, which
        // drops every other texel of text and thin lines. Now, while the views are split,
        // a HUD camera copies its view's picture into its texture and draws the HUD over
        // it, and the compositor draws that one texture per view, opaque and filtered like
        // the picture. While one view rests, the HUD cameras draw straight onto the screen
        // after the compositor, as vanilla draws its HUD over the world.
        internal static bool hudOntoPicture = true;
        private readonly bool[] hudCombined = new bool[4];
        private readonly bool[] hudDirect = new bool[4];
        /// <summary>Frame a HUD camera switched to drawing onto its picture; its texture holds a combined image once it has drawn since.</summary>
        private readonly int[] hudCombinedSince = { -1, -1, -1, -1 };

        private bool HudCombinedReady(int camera)
        {
            return camera >= 0 && camera < hudCombined.Length && hudCombined[camera] && hudTextures[camera] != null &&
                hudCombinedSince[camera] >= 0 && lastHudPostFrames[camera] >= hudCombinedSince[camera];
        }

        /// <summary>Per frame, before anything renders: each HUD camera's target, clear and depth for how the compositor will draw this frame.</summary>
        private void ApplyAdaptiveHudMode()
        {
            bool onto = hudOntoPicture && dynamicCompositor?.opaqueMaterial != null;
            bool atRest = adaptiveLayout.FullAtRest;
            RenderTexture display = Display.main != null ? Display.main.Extras().renderTexture : null;
            FilterMode zoomFilter = Options?.ZoomedFilter.Value == "Point" ? FilterMode.Point : FilterMode.Bilinear;
            for (int i = 0; i < hudCameras.Length; i++)
            {
                Camera hud = hudCameras[i];
                if (hud == null) continue;
                bool direct = onto && atRest && display != null;
                bool combined = onto && !direct;
                if (combined && !hudCombined[i])
                {
                    hudCombinedSince[i] = Time.frameCount;
                    // Fill it this frame even if the rotation gave this camera no turn.
                    if (hudCameraExpected[i] && !hud.enabled) hud.enabled = true;
                }
                if (!combined) hudCombinedSince[i] = -1;
                hudCombined[i] = combined;
                hudDirect[i] = direct;
                RenderTexture target = direct ? display : hudTextures[i];
                if (target != null && hud.targetTexture != target) hud.targetTexture = target;
                // Depth only: the colour is the copied picture, or the finished screen.
                CameraClearFlags clear = onto ? CameraClearFlags.Depth : CameraClearFlags.Color;
                if (hud.clearFlags != clear) hud.clearFlags = clear;
                // Straight onto the screen they draw after the compositor (200), under the
                // overlay (220) that holds the shared meters, the pause menu and the pointer.
                float depth = direct ? 205f + i : 150f + i;
                if (hud.depth != depth) hud.depth = depth;
                if (hudTextures[i] != null)
                {
                    AdaptiveLayout.View view = adaptiveLayout.ViewOf(i);
                    FilterMode filter = view != null && Mathf.Min(view.zoom, view.TargetZoom) < 0.999f ? zoomFilter : FilterMode.Point;
                    if (hudTextures[i].filterMode != filter) hudTextures[i].filterMode = filter;
                }
            }
        }

        /// <summary>Every HUD camera back to drawing into its own cleared texture (the other styles, a new session).</summary>
        private void ResetHudCameraModes()
        {
            for (int i = 0; i < hudCameras.Length; i++)
            {
                hudCombined[i] = hudDirect[i] = false;
                hudCombinedSince[i] = -1;
                Camera hud = hudCameras[i];
                if (hud == null) continue;
                if (hudTextures[i] != null && hud.targetTexture != hudTextures[i]) hud.targetTexture = hudTextures[i];
                hud.clearFlags = CameraClearFlags.Color;
                hud.depth = 150f + i;
                if (hudTextures[i] != null) hudTextures[i].filterMode = FilterMode.Point;
            }
        }

        /// <summary>From the HUD camera's OnPreRender: its view's picture into its texture, for the HUD to be drawn over.</summary>
        private void CopyPictureIntoHud(int camera)
        {
            RenderTexture target = camera >= 0 && camera < hudTextures.Length ? hudTextures[camera] : null;
            if (target == null) return;
            RenderTexture source = AdaptivePictureListener(camera)?.renderTexture;
            RenderTexture previous = RenderTexture.active;
            try
            {
                if (source == null || !source.IsCreated())
                {
                    RenderTexture.active = target;
                    GL.Clear(false, true, Color.black);
                }
                else if (source.width == target.width && source.height == target.height && source.format == target.format)
                    Graphics.CopyTexture(source, target);
                else
                    Graphics.Blit(source, target);
            }
            catch (Exception error) { LogHookError("CopyPictureIntoHud", error); }
            finally { RenderTexture.active = previous; }
        }

        /// <summary>A line along each edge of the cell that is not the screen's edge; a moving cell carries its own.</summary>
        private void DrawAdaptiveBorder(Material material, AdaptiveLayout.Box cell)
        {
            if (material == null || !material.SetPass(0)) return;
            float half = Mathf.Max(0.5f, dynamicSettings.dividerWidth) * 0.5f;
            float hx = half / Mathf.Max(1f, Futile.screen.pixelWidth), hy = half / Mathf.Max(1f, Futile.screen.pixelHeight);
            GL.Begin(GL.QUADS);
            GL.Color(Color.black);
            if (cell.x > 0.001f) GLRect(cell.x - hx, cell.y, cell.x + hx, cell.yMax);
            if (cell.xMax < 0.999f) GLRect(cell.xMax - hx, cell.y, cell.xMax + hx, cell.yMax);
            if (cell.y > 0.001f) GLRect(cell.x, cell.y - hy, cell.xMax, cell.y + hy);
            if (cell.yMax < 0.999f) GLRect(cell.x, cell.yMax - hy, cell.xMax, cell.yMax + hy);
            GL.End();
        }

        private static void GLRect(float x0, float y0, float x1, float y1)
        {
            GL.Vertex3(x0, y0, 0f);
            GL.Vertex3(x1, y0, 0f);
            GL.Vertex3(x1, y1, 0f);
            GL.Vertex3(x0, y1, 0f);
        }

        // ---- Group map (spare quarter) ------------------------------------------------
        // Drawn from the game's own map data: every room the save has visited, at its
        // map position and size, plus the rooms players are in now; shelters and gates
        // picked out; a marker per player in their Jolly colour. It frames the living
        // players with some margin and glides after them. Deliberately not the painted
        // map art, which is one shader-driven HUD per player and tied to that player's
        // map button; this needs nobody's input and cannot disturb anyone's own map.

        private HUD.Map.MapData GroupMapData(RainWorldGame game)
        {
            foreach (AdaptiveLayout.Player player in adaptivePlayers)
            {
                HUD.Map.MapData data = CameraByNumber(game, player.camera)?.hud?.map?.mapData;
                if (data?.roomPositions != null && data.roomSizes != null && data.roomNames != null) return data;
            }
            return null;
        }

        private static bool MapRoomIndex(HUD.Map.MapData data, int room, out int index)
        {
            index = room - data.firstRoomIndex;
            return index >= 0 && index < data.roomPositions.Length && index < data.roomSizes.Length && index < data.roomNames.Length;
        }

        /// <summary>A creature's position in map units (tiles, relative to the region map), as HUD.Map.RoomToMapPos computes it before panning.</summary>
        private static bool TryGroupMapPosition(HUD.Map.MapData data, AbstractCreature creature, out Vector2 position)
        {
            position = Vector2.zero;
            if (data == null || creature?.Room == null) return false;
            int room = creature.Room.index;
            if (!MapRoomIndex(data, room, out int index)) return false;
            Vector2 inRoom = creature.realizedCreature?.mainBodyChunk != null && creature.realizedCreature.room != null
                ? creature.realizedCreature.mainBodyChunk.pos
                : new Vector2(creature.pos.x * 20f + 10f, creature.pos.y * 20f + 10f);
            IntVector2 size = data.roomSizes[index];
            position = data.roomPositions[index] / 3f + (inRoom - new Vector2(size.x * 10f, size.y * 10f)) / 20f;
            return FiniteVector(position);
        }

        private float GroupMapAspect(AdaptiveLayout.Box cell)
        {
            float pixelWidth = Futile.screen != null ? Futile.screen.pixelWidth : 1366f;
            float pixelHeight = Futile.screen != null ? Futile.screen.pixelHeight : 768f;
            return Mathf.Max(0.1f, cell.w * pixelWidth / Mathf.Max(1f, cell.h * pixelHeight));
        }

        private void UpdateGroupMapFraming(RainWorldGame game, float dt)
        {
            AdaptiveLayout.View spare = adaptiveLayout.spare;
            if (spareQuarterMode != SpareMapAndMeters || spare == null || !spare.visible) { groupMapFramed = false; return; }
            HUD.Map.MapData data = GroupMapData(game);
            if (data == null) return;
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            int found = 0;
            foreach (AdaptiveLayout.Player player in adaptivePlayers)
            {
                if (!TryGroupMapPosition(data, GetPlayerForCamera(game, player.camera), out Vector2 position)) continue;
                min.x = Mathf.Min(min.x, position.x); min.y = Mathf.Min(min.y, position.y);
                max.x = Mathf.Max(max.x, position.x); max.y = Mathf.Max(max.y, position.y);
                found++;
            }
            if (found == 0) return;
            float aspect = GroupMapAspect(spare.cell);
            Vector2 center = (min + max) * 0.5f;
            float width = Mathf.Max(GroupMapMinWidth, max.x - min.x + 2f * GroupMapMargin, (max.y - min.y + 2f * GroupMapMargin) * aspect);
            if (!groupMapFramed)
            {
                groupMapCenter = center;
                groupMapWidth = width;
                groupMapCenterVelocity = Vector2.zero;
                groupMapWidthVelocity = 0f;
                groupMapFramed = true;
                return;
            }
            groupMapCenter.x = Mathf.SmoothDamp(groupMapCenter.x, center.x, ref groupMapCenterVelocity.x, 0.6f, Mathf.Infinity, dt);
            groupMapCenter.y = Mathf.SmoothDamp(groupMapCenter.y, center.y, ref groupMapCenterVelocity.y, 0.6f, Mathf.Infinity, dt);
            groupMapWidth = Mathf.SmoothDamp(groupMapWidth, width, ref groupMapWidthVelocity, 0.6f, Mathf.Infinity, dt);
        }

        private void RefreshVisitedRooms(RainWorldGame game)
        {
            int region = game.world?.region?.regionNumber ?? -1;
            List<string> visited = null;
            try
            {
                var states = game.GetStorySession?.saveState?.regionStates;
                if (states != null && region >= 0 && region < states.Length) visited = states[region]?.roomsVisited;
            }
            catch (Exception) { visited = null; }
            int count = visited?.Count ?? 0;
            if (region == visitedRoomsRegion && count == visitedRoomsCount) return;
            visitedRoomsRegion = region;
            visitedRoomsCount = count;
            visitedRooms.Clear();
            if (visited != null) foreach (string name in visited) if (name != null) visitedRooms.Add(name);
        }

        private static Color PlayerMarkerColor(AbstractCreature creature, int camera)
        {
            int number = (creature?.state as PlayerState)?.playerNumber ?? camera;
            Color color = Color.gray;
            try { if (ModManager.CoopAvailable) color = PlayerGraphics.JollyColor(number, 0); }
            catch (Exception) { color = Color.gray; }
            if (color == Color.gray || color == Color.grey)
                color = FallbackPlayerColors[Mathf.Clamp(number, 0, FallbackPlayerColors.Length - 1)];
            color.a = 1f;
            return color;
        }

        private readonly HashSet<int> groupMapOccupied = new HashSet<int>();
        private bool groupMapBatchOpen;

        /// <summary>
        /// The map is an extra: an exception from it must not reach the compositor's
        /// handler, which gives up on the whole dynamic pipeline. Logged once in a while
        /// and skipped, with its GL batch closed so the rest of the frame still draws.
        /// </summary>
        private void DrawGroupMapSafely(Material material, AdaptiveLayout.Box cell, RainWorldGame game)
        {
            try { DrawGroupMap(material, cell, game); }
            catch (Exception error)
            {
                if (groupMapBatchOpen) GL.End();
                LogHookError("DrawGroupMap", error);
            }
            finally { groupMapBatchOpen = false; }
        }

        private void DrawGroupMap(Material material, AdaptiveLayout.Box cell, RainWorldGame game)
        {
            if (!groupMapFramed || material == null) return;
            HUD.Map.MapData data = GroupMapData(game);
            if (data == null || !material.SetPass(0)) return;
            RefreshVisitedRooms(game);
            float aspect = GroupMapAspect(cell);
            float width = Mathf.Max(1f, groupMapWidth), height = width / aspect;
            float originX = groupMapCenter.x - width * 0.5f, originY = groupMapCenter.y - height * 0.5f;
            float scaleX = cell.w / width, scaleY = cell.h / height;

            HashSet<int> occupied = groupMapOccupied;
            occupied.Clear();
            foreach (AdaptiveLayout.Player player in adaptivePlayers)
            {
                AbstractCreature creature = GetPlayerForCamera(game, player.camera);
                if (creature?.Room != null) occupied.Add(creature.Room.index);
            }

            GL.Begin(GL.QUADS);
            groupMapBatchOpen = true;
            GL.Color(new Color(0.035f, 0.035f, 0.045f, 1f));
            GLRect(cell.x, cell.y, cell.xMax, cell.yMax);
            for (int index = 0; index < data.roomNames.Length && index < data.roomPositions.Length && index < data.roomSizes.Length; index++)
            {
                int room = data.firstRoomIndex + index;
                bool here = occupied.Contains(room);
                if (!here && (data.roomNames[index] == null || !visitedRooms.Contains(data.roomNames[index]))) continue;
                IntVector2 size = data.roomSizes[index];
                if (size.x <= 0 || size.y <= 0) continue;
                Vector2 center = data.roomPositions[index] / 3f;
                float x0 = cell.x + (center.x - size.x * 0.5f - originX) * scaleX;
                float x1 = cell.x + (center.x + size.x * 0.5f - originX) * scaleX;
                float y0 = cell.y + (center.y - size.y * 0.5f - originY) * scaleY;
                float y1 = cell.y + (center.y + size.y * 0.5f - originY) * scaleY;
                if (x1 <= cell.x || x0 >= cell.xMax || y1 <= cell.y || y0 >= cell.yMax) continue;
                AbstractRoom abstractRoom = game.world?.GetAbstractRoom(room);
                Color fill = abstractRoom != null && abstractRoom.shelter ? new Color(0.62f, 0.66f, 0.7f, 1f)
                    : abstractRoom != null && abstractRoom.gate ? new Color(0.55f, 0.46f, 0.26f, 1f)
                    : here ? new Color(0.4f, 0.41f, 0.45f, 1f)
                    : new Color(0.21f, 0.22f, 0.25f, 1f);
                GL.Color(fill);
                GLRect(Mathf.Max(x0, cell.x), Mathf.Max(y0, cell.y), Mathf.Min(x1, cell.xMax), Mathf.Min(y1, cell.yMax));
            }
            // Players on top, clamped to the quarter so someone far away still shows at its edge.
            float pixelWidth = Futile.screen != null ? Futile.screen.pixelWidth : 1366f;
            float pixelHeight = Futile.screen != null ? Futile.screen.pixelHeight : 768f;
            float outerX = 6f / pixelWidth, outerY = 6f / pixelHeight, innerX = 4f / pixelWidth, innerY = 4f / pixelHeight;
            foreach (AdaptiveLayout.Player player in adaptivePlayers)
            {
                AbstractCreature creature = GetPlayerForCamera(game, player.camera);
                if (!TryGroupMapPosition(data, creature, out Vector2 position)) continue;
                float x = Mathf.Clamp(cell.x + (position.x - originX) * scaleX, cell.x + outerX, cell.xMax - outerX);
                float y = Mathf.Clamp(cell.y + (position.y - originY) * scaleY, cell.y + outerY, cell.yMax - outerY);
                GL.Color(Color.black);
                GLRect(x - outerX, y - outerY, x + outerX, y + outerY);
                GL.Color(PlayerMarkerColor(creature, player.camera));
                GLRect(x - innerX, y - innerY, x + innerX, y + innerY);
            }
            GL.End();
            groupMapBatchOpen = false;
        }
    }
}
