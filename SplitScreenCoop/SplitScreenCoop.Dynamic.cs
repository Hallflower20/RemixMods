using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        private readonly SplitLayoutSolver dynamicSolver = new SplitLayoutSolver();
        private readonly SplitLayoutSolver.Settings dynamicSettings = new SplitLayoutSolver.Settings();
        private SplitLayoutSolver.Layout dynamicLayout;
        private SplitLayoutSolver.PlayerInput[] dynamicInputs = new SplitLayoutSolver.PlayerInput[0];
        private Vector2[] ownScreenPositions = new Vector2[0];
        private int[] baseCameraNumbers = new int[0];
        // Which camera's image each camera's cell drew last tick, by camera number.
        private readonly int[] lastBaseByCamera = { -1, -1, -1, -1 };
        // Per camera: the uv translation its own image is drawn with. Refreshed every
        // rendered frame from interpolated positions and damped, so following is as
        // smooth as the sprites themselves instead of stepping at the 40 Hz tick.
        private readonly Vector2[] ownViewShifts = new Vector2[4];
        private readonly Vector2[] ownViewShiftVelocities = new Vector2[4];
        private readonly bool[] ownViewShiftValid = new bool[4];
        private readonly long[] ownViewSourceKeys = new long[4];
        private readonly Vector2[] ownViewCellCenters = new Vector2[4];
        // Which camera's image each cell's shift was computed against, and where that
        // camera stood. Two cameras on one prebaked screen still differ by their
        // follow slack (up to 40 px), so a cell switching its base camera keeps its
        // shift continuous in world terms instead of jolting by that slack.
        private readonly int[] ownViewBaseNumbers = { -1, -1, -1, -1 };
        private readonly Vector2[] ownViewBasePositions = new Vector2[4];
        // What each cell drew last frame, so a base switch to a camera that has not
        // rendered yet shows the previous image for a frame instead of nothing.
        private readonly CameraListener[] lastDrawnListeners = new CameraListener[4];
        private long lastLayoutSignature = long.MinValue;
        private const float ViewPanSmoothing = 0.12f;
        private float lastTimeStacker = 1f;
        private Camera dynamicCompositorCamera;
        private DynamicCompositor dynamicCompositor;
        private readonly FStage[] hudStages = new FStage[4];
        private readonly FStage[] worldStages = new FStage[4];
        private readonly FContainer[] worldNameContainers = new FContainer[4];
        private static readonly int[] worldLayers = new int[4];
        private static readonly FieldInfo PlayerNameLabelField =
            typeof(JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyPlayerArrow)
                .GetField("label", BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly Camera[] hudCameras = new Camera[4];
        private readonly RenderTexture[] hudTextures = new RenderTexture[4];
        private readonly int[] lastHudPostFrames = { -1, -1, -1, -1 };
        private readonly int[] hudExpectedSinceFrames = { -1, -1, -1, -1 };
        private readonly int[] lastHudRecoveryFrames = { -10000, -10000, -10000, -10000 };
        private int lastGlobalHudPostFrame = -1;
        private int compositorExpectedSinceFrame = -1;
        private int globalHudExpectedSinceFrame = -1;
        private int lastGlobalHudRecoveryFrame = -10000;
        private readonly int[] hudLayers = new int[4];
        private readonly int[] initialWorldCullingMasks = new int[4];
        private int globalHudLayer = -1;
        private FStage globalHudStage;
        private Camera globalHudCamera;
        private int globalMeterSource = -1;
        private int globalPromptSource = -1;
        private string lastDynamicLayoutKey;
        private readonly string[] lastWorldFallbackReasons = new string[4];
        public static bool dynamicStyle = true;
        public static bool dynamicActive;
        public static bool dynamicDebugOverlay;
        private bool dynamicPipelineAvailable;
        private bool dynamicPipelineFailed;
        private int compositorRecoveries;

        private sealed class DynamicCompositor : MonoBehaviour
        {
            public SplitScreenCoop owner;
            public Material texturedMaterial;
            public Material solidMaterial;
            public int lastCompositeFrame = -1;

            public void OnPostRender()
            {
                owner?.CompositeDynamicLayout(this);
            }

            public void LateUpdate()
            {
                // Menus have no game GrafUpdate; count frames here too so the hang
                // watchdog stays quiet on the death screen and menus.
                WatchdogFrame = Time.frameCount;
                owner?.EnforceMenuCameraState();
            }

            public void OnGUI()
            {
                if (dynamicActive && dynamicDebugOverlay) owner?.DrawDynamicDebugLabels();
            }

            public void OnDestroy()
            {
                if (texturedMaterial != null) UnityEngine.Object.Destroy(texturedMaterial);
                if (solidMaterial != null) UnityEngine.Object.Destroy(solidMaterial);
            }
        }

        private sealed class HudShaderRouter : MonoBehaviour
        {
            public SplitScreenCoop owner;
            public int cameraNumber;
            public bool global;
            public void OnPreRender()
            {
                int index = global ? owner.globalMeterSource : cameraNumber;
                if (index >= 0 && index < cameraListeners.Length)
                    cameraListeners[index]?.OnPreRender();
            }
            public void OnPostRender()
            {
                if (global) owner.lastGlobalHudPostFrame = Time.frameCount;
                else owner.lastHudPostFrames[cameraNumber] = Time.frameCount;
            }
        }

        private void InitDynamicCompositor(Futile futile)
        {
            var holder = new GameObject("SplitScreen polygon compositor");
            holder.transform.parent = futile.gameObject.transform;
            dynamicCompositorCamera = holder.AddComponent<Camera>();
            futile.InitCamera(dynamicCompositorCamera, 1);
            dynamicCompositorCamera.tag = "Untagged";
            dynamicCompositorCamera.depth = 200f;
            dynamicCompositorCamera.clearFlags = CameraClearFlags.Nothing;
            dynamicCompositorCamera.cullingMask = 0;
            dynamicCompositorCamera.targetTexture = Display.main.Extras().renderTexture;
            dynamicCompositor = holder.AddComponent<DynamicCompositor>();
            dynamicCompositor.owner = this;
            Shader basic = FShader.Basic?.shader;
            Shader solid = FShader.SolidColored?.shader;
            if (basic == null || solid == null)
            {
                Logger.LogError("[CameraLayout] Futile compositor materials are unavailable; Dynamic falls back to Classic.");
                dynamicStyle = false;
                dynamicCompositorCamera.enabled = false;
                return;
            }
            dynamicCompositor.texturedMaterial = new Material(basic) { hideFlags = HideFlags.HideAndDontSave };
            dynamicCompositor.solidMaterial = new Material(solid) { hideFlags = HideFlags.HideAndDontSave };
            dynamicCompositorCamera.enabled = false;
            InitHudRenderLayers(futile);
        }

        private void InitHudRenderLayers(Futile futile)
        {
            int found = 0;
            for (int layer = 31; layer >= 8 && found < 9; layer--)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(layer)))
                {
                    if (found < 4) hudLayers[found] = layer;
                    else if (found == 4) globalHudLayer = layer;
                    else worldLayers[found - 5] = layer;
                    found++;
                }
            if (found < 9)
            {
                Logger.LogError("[CameraLayout] Not enough unused Unity layers for isolated world views and HUD; Dynamic falls back to Classic.");
                dynamicStyle = false;
                return;
            }
            for (int i = 0; i < 4; i++)
            {
                initialWorldCullingMasks[i] = fcameras[i]?.cullingMask ?? 0;
                hudStages[i] = new FStage($"SplitScreen HUD {i}") { layer = hudLayers[i] };
                Futile.AddStage(hudStages[i]);
                var holder = new GameObject($"SplitScreen HUD camera {i}");
                holder.transform.parent = futile.gameObject.transform;
                hudCameras[i] = holder.AddComponent<Camera>();
                futile.InitCamera(hudCameras[i], i + 1);
                hudCameras[i].tag = "Untagged";
                hudCameras[i].depth = 150f + i;
                hudCameras[i].cullingMask = 1 << hudLayers[i];
                hudCameras[i].clearFlags = CameraClearFlags.Color;
                hudCameras[i].backgroundColor = Color.clear;
                hudCameras[i].enabled = false;
                var router = holder.AddComponent<HudShaderRouter>();
                router.owner = this;
                router.cameraNumber = i;
                ReinitHudTexture(i);
            }
            globalHudStage = new FStage("SplitScreen global HUD") { layer = globalHudLayer };
            Futile.AddStage(globalHudStage);
            for (int i = 0; i < worldStages.Length; i++)
            {
                worldStages[i] = new FStage($"SplitScreen world {i}") { layer = worldLayers[i] };
                Futile.AddStage(worldStages[i]);
                worldNameContainers[i] = new FContainer();
                worldNameContainers[i].SetPosition(camOffsets[i]);
                worldStages[i].AddChild(worldNameContainers[i]);
            }
            Logger.LogInfo($"[CameraLayout] isolated world layers=[{string.Join(",", worldLayers)}]; HUD layers=[{string.Join(",", hudLayers)}]; global HUD layer={globalHudLayer}");
            var globalHolder = new GameObject("SplitScreen global HUD camera");
            globalHolder.transform.parent = futile.gameObject.transform;
            globalHudCamera = globalHolder.AddComponent<Camera>();
            futile.InitCamera(globalHudCamera, 1);
            globalHudCamera.tag = "Untagged";
            // Render global meters after the polygon compositor. Rendering
            // them into an alpha texture and then blending that texture again
            // darkened vanilla white meter sprites to gray.
            globalHudCamera.depth = 220f;
            globalHudCamera.cullingMask = 1 << globalHudLayer;
            globalHudCamera.clearFlags = CameraClearFlags.Nothing;
            globalHudCamera.enabled = false;
            var globalRouter = globalHolder.AddComponent<HudShaderRouter>();
            globalRouter.owner = this;
            globalRouter.global = true;
            ReinitGlobalHudTexture();
            dynamicPipelineAvailable = true;
        }

        private void ReinitGlobalHudTexture()
        {
            if (globalHudCamera == null || Display.main == null) return;
            globalHudCamera.targetTexture = Display.main.Extras().renderTexture;
        }

        private void ReinitHudTexture(int index)
        {
            Camera camera = hudCameras[index];
            if (camera == null || Futile.screen?.renderTexture == null) return;
            camera.targetTexture = null;
            if (hudTextures[index] != null)
            {
                hudTextures[index].Release();
                UnityEngine.Object.Destroy(hudTextures[index]);
            }
            hudTextures[index] = new RenderTexture(Futile.screen.renderTexture)
            {
                name = $"SplitScreen unzoomed HUD {index}",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            hudTextures[index].Create();
            camera.targetTexture = hudTextures[index];
        }

        private void ReinitHudTextures()
        {
            for (int i = 0; i < hudTextures.Length; i++) ReinitHudTexture(i);
            ReinitGlobalHudTexture();
        }

        private void MoveCameraHudToOverlay(RoomCamera camera)
        {
            if (!dynamicStyle || dualDisplays || camera?.game == null ||
                camera.game.session?.Players == null || camera.game.session.Players.Count <= 1 ||
                camera.game.cameras == null || camera.game.cameras.Length <= 1 ||
                camera.cameraNumber < 0 ||
                camera.cameraNumber >= hudStages.Length || hudStages[camera.cameraNumber] == null) return;
            FStage stage = hudStages[camera.cameraNumber];
            stage.AddChild(camera.ReturnFContainer("HUD"));
            stage.AddChild(camera.ReturnFContainer("HUD2"));
            if (camera.hud?.map?.inFrontContainer != null)
                stage.AddChild(camera.hud.map.inFrontContainer);
        }

        private void MoveCameraWorldToStage(RoomCamera camera)
        {
            if (!dynamicStyle || dualDisplays || camera?.game?.cameras == null ||
                camera.game.cameras.Length <= 1 || camera.cameraNumber < 0 ||
                camera.cameraNumber >= worldStages.Length || worldStages[camera.cameraNumber] == null)
                return;
            FContainer hud = camera.ReturnFContainer("HUD");
            FContainer hud2 = camera.ReturnFContainer("HUD2");
            foreach (FContainer layer in camera.SpriteLayers)
                if (layer != hud && layer != hud2)
                    worldStages[camera.cameraNumber].AddChild(layer);
            worldStages[camera.cameraNumber].AddChild(worldNameContainers[camera.cameraNumber]);
            if (fcameras[camera.cameraNumber] != null)
                fcameras[camera.cameraNumber].cullingMask = 1 << worldLayers[camera.cameraNumber];
        }

        private void MovePlayerNamesToWorld(RoomCamera camera)
        {
            if (!dynamicStyle || dualDisplays || camera?.hud?.parts == null ||
                camera.game?.cameras?.Length <= 1 || camera.cameraNumber < 0 ||
                camera.cameraNumber >= worldNameContainers.Length ||
                worldNameContainers[camera.cameraNumber] == null) return;
            FContainer destination = worldNameContainers[camera.cameraNumber];
            foreach (HUD.HudPart part in camera.hud.parts)
                if (part is JollyCoop.JollyHUD.JollyPlayerSpecificHud jolly && jolly.playerArrow != null)
                {
                    destination.AddChild(jolly.playerArrow.gradient);
                    destination.AddChild(jolly.playerArrow.mainSprite);
                    if (PlayerNameLabelField?.GetValue(jolly.playerArrow) is FLabel label)
                        destination.AddChild(label);
                }
        }

        private void RestoreClassicPlayerNames(RoomCamera camera)
        {
            if (camera?.hud?.parts == null) return;
            FContainer destination = camera.ReturnFContainer("HUD2");
            foreach (HUD.HudPart part in camera.hud.parts)
                if (part is JollyCoop.JollyHUD.JollyPlayerSpecificHud jolly && jolly.playerArrow != null)
                {
                    destination.AddChild(jolly.playerArrow.gradient);
                    destination.AddChild(jolly.playerArrow.mainSprite);
                    if (PlayerNameLabelField?.GetValue(jolly.playerArrow) is FLabel label)
                        destination.AddChild(label);
                }
        }

        private void RestoreClassicWorld(RainWorldGame game)
        {
            if (game?.cameras == null) return;
            foreach (RoomCamera camera in game.cameras)
                if (camera?.SpriteLayers != null)
                    foreach (FContainer layer in camera.SpriteLayers)
                        Futile.stage.AddChild(layer);
        }

        private void RouteGlobalMeters(RainWorldGame game)
        {
            if (!dynamicStyle || dualDisplays || game?.cameras == null || game.cameras.Length <= 1 ||
                globalHudStage == null) return;
            foreach (RoomCamera camera in game.cameras) RouteGlobalMeters(camera);
        }

        private void RouteGlobalMeters(RoomCamera camera)
        {
            if (!dynamicStyle || dualDisplays || camera?.hud == null ||
                camera.game?.cameras?.Length <= 1 || globalHudStage == null) return;
            FContainer destination = camera.cameraNumber == globalMeterSource ? globalHudStage : null;
            // Order matters: AddChild appends, so the routing order is the draw
            // order. Vanilla builds the TextPrompt before every meter, which keeps
            // its semi-transparent letterbox bars *behind* the meters. Routing the
            // bars last put them over the food pips and greyed them out.
            HUD.TextPrompt prompt = camera.hud.textPrompt;
            if (prompt != null)
            {
                FContainer promptDestination = camera.cameraNumber == globalPromptSource ? globalHudStage : null;
                RouteNode(prompt.fullscreenFade, promptDestination);
                if (prompt.sprites != null)
                    foreach (FSprite sprite in prompt.sprites) RouteNode(sprite, promptDestination);
                RouteNode(prompt.label, promptDestination);
                RouteNode(prompt.musicSprite, promptDestination);
                if (prompt.symbols != null)
                    foreach (IconSymbol symbol in prompt.symbols)
                    {
                        RouteNode(symbol.shadowSprite1, promptDestination);
                        RouteNode(symbol.shadowSprite2, promptDestination);
                        RouteNode(symbol.symbolSprite, promptDestination);
                    }
            }
            RouteFoodMeter(camera.hud.foodMeter, destination);
            // Food plops and karma changes create short-lived FadeCircles after
            // the permanent meter nodes have been routed. Keep those circles with
            // the meter they belong to instead of leaving them in the per-view HUD2.
            if (camera.hud.fadeCircles != null)
                foreach (HUD.FadeCircle effect in camera.hud.fadeCircles)
                    if (effect?.circle?.sprite != null &&
                        IsGlobalMeterEffect(camera.hud, effect.circle.pos))
                        RouteNode(effect.circle.sprite, destination);
            HUD.KarmaMeter karma = camera.hud.karmaMeter;
            if (karma != null)
            {
                RouteNode(karma.darkFade, destination);
                RouteNode(karma.karmaSprite, destination);
                RouteNode(karma.glowSprite, destination);
                RouteNode(karma.ringSprite, destination);
                RouteNode(karma.vectorRingSprite, destination);
            }
            HUD.RainMeter rain = camera.hud.rainMeter;
            if (rain?.circles != null)
                foreach (HUD.HUDCircle circle in rain.circles) RouteNode(circle?.sprite, destination);
            // Gourmand's collection tracker belongs with the shared food meter.
            if (camera.hud.gourmandmeter != null)
                foreach (var symbol in camera.hud.gourmandmeter.CollectedSymbols)
                {
                    RouteNode(symbol.creatureSymbol?.symbolSprite, destination);
                    RouteNode(symbol.creatureSymbol?.shadowSprite1, destination);
                    RouteNode(symbol.creatureSymbol?.shadowSprite2, destination);
                    RouteNode(symbol.itemSymbol?.symbolSprite, destination);
                    RouteNode(symbol.itemSymbol?.shadowSprite1, destination);
                    RouteNode(symbol.itemSymbol?.shadowSprite2, destination);
                }
        }

        private static void RouteFoodMeter(HUD.FoodMeter food, FContainer destination)
        {
            if (food == null) return;
            RouteNode(food.darkFade, destination);
            RouteNode(food.lineSprite, destination);
            if (food.circles != null)
                foreach (HUD.FoodMeter.MeterCircle meter in food.circles)
                {
                    RouteNode(meter?.gradient, destination);
                    if (meter?.circles != null)
                        foreach (HUD.HUDCircle circle in meter.circles) RouteNode(circle?.sprite, destination);
                    RouteNode(meter?.backCircle?.sprite, destination);
                }
            RouteNode(food.quarterPipShower?.quarterPips, destination);
            if (food.pupBars != null)
                foreach (HUD.FoodMeter pup in food.pupBars) RouteFoodMeter(pup, destination);
        }

        /// <summary>
        /// A fade circle spawned by a globally routed meter has to travel with that
        /// meter. Left in the per-view HUD it is drawn shifted into the owning cell
        /// while the meter itself is drawn unshifted, so the flash ends up detached
        /// from the pips or karma symbol it came from. Jolly's circles spawn at a
        /// player's on-screen position and must stay with their own view.
        /// </summary>
        private static bool IsGlobalMeterEffect(HUD.HUD hud, Vector2 position)
        {
            if (IsFoodMeterEffect(hud.foodMeter, position)) return true;
            HUD.KarmaMeter karma = hud.karmaMeter;
            if (karma == null) return false;
            float reach = Mathf.Max(45f, karma.rad + 20f);
            return (karma.pos - position).sqrMagnitude < reach * reach;
        }

        private static bool IsFoodMeterEffect(HUD.FoodMeter food, Vector2 position)
        {
            if (food?.circles != null)
                foreach (HUD.FoodMeter.MeterCircle pip in food.circles)
                    if (pip?.circles != null && pip.circles.Length > 0 &&
                        pip.circles[0] != null &&
                        (pip.circles[0].pos - position).sqrMagnitude < 45f * 45f)
                        return true;
            if (food?.pupBars != null)
                foreach (HUD.FoodMeter pup in food.pupBars)
                    if (IsFoodMeterEffect(pup, position)) return true;
            return false;
        }

        private static void RouteNode(FNode node, FContainer destination)
        {
            if (node == null || node.container == destination) return;
            if (destination == null) node.RemoveFromContainer();
            else destination.AddChild(node);
        }

        private void ResetDynamicLayout()
        {
            dynamicSolver.Reset();
            compositorRecoveries = 0;
            dynamicLayout = null;
            dynamicInputs = new SplitLayoutSolver.PlayerInput[0];
            ownScreenPositions = new Vector2[0];
            baseCameraNumbers = new int[0];
            for (int i = 0; i < lastBaseByCamera.Length; i++) lastBaseByCamera[i] = -1;
            lastDynamicLayoutKey = null;
            lastLayoutSignature = long.MinValue;
            dynamicActive = false;
            globalMeterSource = -1;
            globalPromptSource = -1;
            if (dynamicCompositorCamera != null) dynamicCompositorCamera.enabled = false;
            compositorExpectedSinceFrame = -1;
            if (globalHudCamera != null) globalHudCamera.enabled = false;
            for (int i = 0; i < hudCameras.Length; i++)
            {
                if (hudCameras[i] != null) hudCameras[i].enabled = false;
                hudExpectedSinceFrames[i] = -1;
                lastHudPostFrames[i] = -1;
                worldNameContainers[i]?.RemoveAllChildren();
            }
            globalHudExpectedSinceFrame = -1;
            lastGlobalHudPostFrame = -1;
            for (int i = 0; i < cameraListeners.Length; i++)
            {
                if (fcameras[i] != null)
                    fcameras[i].cullingMask = initialWorldCullingMasks[i];
                if (cameraListeners[i] == null) continue;
                cameraListeners[i].dynamicCompositing = false;
                cameraListeners[i].Retarget();
                lastWorldFallbackReasons[i] = null;
                ownViewShiftValid[i] = false;
                ownViewBaseNumbers[i] = -1;
                lastDrawnListeners[i] = null;
                ownViewShiftVelocities[i] = Vector2.zero;
            }
        }

        private void RestoreClassicHud(RainWorldGame game)
        {
            if (game?.cameras == null) return;
            foreach (RoomCamera camera in game.cameras)
            {
                if (camera == null) continue;
                FContainer hud = camera.ReturnFContainer("HUD");
                FContainer hud2 = camera.ReturnFContainer("HUD2");
                Futile.stage.AddChild(hud);
                Futile.stage.AddChild(hud2);
                if (camera.hud?.map?.inFrontContainer != null)
                    Futile.stage.AddChild(camera.hud.map.inFrontContainer);
                if (camera.hud == null) continue;
                RestoreClassicPlayerNames(camera);
                RouteFoodMeter(camera.hud.foodMeter, hud2);
                HUD.KarmaMeter karma = camera.hud.karmaMeter;
                if (karma != null)
                {
                    RouteNode(karma.darkFade, hud2);
                    RouteNode(karma.karmaSprite, hud2);
                    RouteNode(karma.glowSprite, hud2);
                    RouteNode(karma.ringSprite, hud2);
                    RouteNode(karma.vectorRingSprite, hud2);
                }
                if (camera.hud.rainMeter?.circles != null)
                    foreach (HUD.HUDCircle circle in camera.hud.rainMeter.circles)
                        RouteNode(circle?.sprite, hud2);
                if (camera.hud.gourmandmeter != null)
                    foreach (var symbol in camera.hud.gourmandmeter.CollectedSymbols)
                    {
                        RouteNode(symbol.creatureSymbol?.symbolSprite, hud2);
                        RouteNode(symbol.creatureSymbol?.shadowSprite1, hud2);
                        RouteNode(symbol.creatureSymbol?.shadowSprite2, hud2);
                        RouteNode(symbol.itemSymbol?.symbolSprite, hud2);
                        RouteNode(symbol.itemSymbol?.shadowSprite1, hud2);
                        RouteNode(symbol.itemSymbol?.shadowSprite2, hud2);
                    }
                HUD.TextPrompt prompt = camera.hud.textPrompt;
                if (prompt != null)
                {
                    RouteNode(prompt.label, hud2);
                    RouteNode(prompt.musicSprite, hud2);
                    RouteNode(prompt.fullscreenFade, hud2);
                    if (prompt.sprites != null)
                        foreach (FSprite sprite in prompt.sprites) RouteNode(sprite, hud2);
                    if (prompt.symbols != null)
                        foreach (IconSymbol symbol in prompt.symbols)
                        {
                            RouteNode(symbol.symbolSprite, hud2);
                            RouteNode(symbol.shadowSprite1, hud2);
                            RouteNode(symbol.shadowSprite2, hud2);
                        }
                }
            }
        }

        private void RestoreClassicPauseMenus(RainWorldGame game)
        {
            if (game?.manager?.sideProcesses == null) return;
            foreach (var process in game.manager.sideProcesses)
                if (process is Menu.PauseMenu pause && pause.container != null)
                {
                    Futile.stage.AddChild(pause.container);
                    pause.container.SetPosition(Vector2.zero);
                }
        }

        private void ReinitDynamicCompositorTexture()
        {
            if (dynamicCompositorCamera != null && Display.main != null)
                dynamicCompositorCamera.targetTexture = Display.main.Extras().renderTexture;
        }

        private void UpdateDynamicLayout(RainWorldGame game, List<int> aliveCameras)
        {
            if (dynamicCompositor == null || dynamicCompositor.texturedMaterial == null || game?.cameras == null) return;
            int count = aliveCameras.Count;
            if (count == 0 && game.cameras.Length > 0) aliveCameras.Add(game.cameras[0].cameraNumber);
            count = aliveCameras.Count;
            if (count == 0) return;
            // Everyone is in the same shelter: there is one thing to look at and the
            // sleep sequence draws its own full-screen UI. Collapse to a single view
            // so only one HUD is composited instead of one per region.
            if (count > 1 && AllPlayersInOneShelter(game, aliveCameras))
            {
                aliveCameras = aliveCameras.GetRange(0, 1);
                count = 1;
            }
            var inputs = new SplitLayoutSolver.PlayerInput[count];
            var positions = new Vector2[count];
            var roomCameras = new RoomCamera[count];
            var roomPositions = new Vector2[count];
            var validRoomPositions = new bool[count];
            var playerRooms = new Room[count];
            var keys = new long[count];
            for (int i = 0; i < count; i++)
            {
                int cameraNumber = aliveCameras[i];
                RoomCamera camera = CameraByNumber(game, cameraNumber);
                roomCameras[i] = camera;
                AbstractCreature player = GetPlayerForCamera(game, cameraNumber);
                string fallback = null;
                Vector2 playerPos = Vector2.zero;
                bool hasPlayerPos = TryGetPlayerRoomPosition(camera, player, out playerPos);
                bool playerInShortcut = player?.realizedCreature?.inShortcut ?? false;
                roomPositions[i] = playerPos;
                Room room = player?.realizedCreature?.room ?? camera?.room;
                playerRooms[i] = room;
                bool cameraRoomReady = room != null && camera?.room == room;
                validRoomPositions[i] = hasPlayerPos && cameraRoomReady;
                // A camera with no usable room has no image of its own to show. Key it
                // on the room it is loading so cameras heading to the same place stay
                // merged; giving each one a unique key forces a hard split, which is
                // what made spawn-in open on a split screen for a couple of frames.
                AbstractRoom pending = camera?.loadingRoom?.abstractRoom ?? camera?.room?.abstractRoom;
                long screenKey = !cameraRoomReady
                    ? pending != null ? long.MinValue + 1L + pending.index : long.MinValue
                    : ScreenKey(room, camera.currentCameraPosition);
                // Players in one pipe share the pending room's key, so a group taking
                // a shortcut together stays one group through the transit and lands
                // on the same screen key when they arrive. (Holding each player's
                // previous key instead was tried: nobody renders that old screen once
                // the cameras move on, so the cells fell back to their own stale,
                // disabled cameras and flickered between sources.)
                keys[i] = screenKey;
                Vector2 worldPos = playerPos;
                bool validWorld = false;
                if (playerInShortcut) fallback = "shortcut movement; holding world direction while following vessel";
                else if (!hasPlayerPos) fallback = "warp or unrealized player position";
                else if (room?.world == game.world && room.abstractRoom != null &&
                    room.abstractRoom.mapPos.sqrMagnitude > 0.01f)
                {
                    worldPos = room.world.RoomToWorldPos(playerPos, room.abstractRoom.index);
                    validWorld = FiniteVector(worldPos);
                    if (!validWorld) fallback = "non-finite world map position";
                }
                else if (room != null)
                {
                    // Within one room the raw coordinates have a meaningful direction.
                    bool otherRoomPresent = false;
                    for (int j = 0; j < count; j++)
                    {
                        RoomCamera other = CameraByNumber(game, aliveCameras[j]);
                        if (other?.room != null && other.room != room) { otherRoomPresent = true; break; }
                    }
                    validWorld = !otherRoomPresent;
                    if (!validWorld) fallback = "different regions or missing room map position";
                }
                else fallback = "room unavailable";
                if (cameraNumber >= 0 && cameraNumber < lastWorldFallbackReasons.Length)
                {
                    if (fallback != null && lastWorldFallbackReasons[cameraNumber] != fallback)
                        Logger.LogInfo($"[CameraMode] frame={Time.frameCount} world-direction fallback cam={cameraNumber}; reason={fallback}; holding last valid direction");
                    lastWorldFallbackReasons[cameraNumber] = fallback;
                }
                long roomKey = room?.abstractRoom == null ? long.MinValue + cameraNumber :
                    ((long)(uint)(room.world?.name?.GetHashCode() ?? 0) << 32) | (uint)room.abstractRoom.index;
                inputs[i] = new SplitLayoutSolver.PlayerInput { playerIndex = cameraNumber, worldPos = worldPos,
                    sameScreenKey = screenKey, roomKey = roomKey, validWorldPos = validWorld, mergedScreenPos = new Vector2(0.5f, 0.5f) };
                positions[i] = CameraScreenPosition(camera, playerPos);
            }

            // A room can change one player's discrete camera position while
            // both players are still visible on another camera's current
            // prebaked screen. Keep that one image until sharing is no longer
            // possible, instead of forcing a premature same-room hard split.
            int sharedCandidate = -1, sharedCount = 1;
            bool[] sharedVisible = new bool[count];
            for (int candidate = 0; candidate < count; candidate++)
            {
                RoomCamera source = roomCameras[candidate];
                if (source?.room == null) continue;
                int visibleCount = 0;
                bool[] visible = new bool[count];
                for (int playerIndex = 0; playerIndex < count; playerIndex++)
                {
                    if (!validRoomPositions[playerIndex] || playerRooms[playerIndex] != source.room)
                        continue;
                    Vector2 screen = CameraScreenPosition(source, roomPositions[playerIndex]);
                    visible[playerIndex] = SplitLayoutSolver.SharedCameraCanShow(screen);
                    // One-way hysteresis. A cell may only *start* drawing another
                    // camera's image once its own camera sits on that same screen,
                    // which is the moment vanilla cuts and the two images are the
                    // same picture. Adopting it earlier, while the player is merely
                    // visible near the edge of the other screen, replaced the cell's
                    // content with a different screen and then panned it into place,
                    // which read as a swap followed by a swipe. Once sharing, keep
                    // sharing while the player stays visible, so a camera switching
                    // screens at the edge does not break a merged view apart.
                    if (visible[playerIndex] && playerIndex != candidate)
                    {
                        RoomCamera own = roomCameras[playerIndex];
                        int ownNumber = aliveCameras[playerIndex];
                        bool ownOnSameScreen = own != null && own.room == source.room &&
                            own.currentCameraPosition == source.currentCameraPosition;
                        bool alreadySharing = ownNumber >= 0 && ownNumber < lastBaseByCamera.Length &&
                            lastBaseByCamera[ownNumber] == aliveCameras[candidate];
                        visible[playerIndex] = ownOnSameScreen || alreadySharing;
                    }
                    if (visible[playerIndex]) visibleCount++;
                }
                if (visibleCount > sharedCount)
                {
                    sharedCount = visibleCount;
                    sharedCandidate = candidate;
                    sharedVisible = visible;
                }
            }
            int[] preferredBase = new int[count];
            for (int i = 0; i < count; i++) preferredBase[i] = -1;
            if (sharedCandidate >= 0)
            {
                RoomCamera source = roomCameras[sharedCandidate];
                long sharedKey = ScreenKey(source.room, source.currentCameraPosition);
                for (int i = 0; i < count; i++)
                {
                    if (sharedVisible[i])
                    {
                        keys[i] = sharedKey;
                        preferredBase[i] = aliveCameras[sharedCandidate];
                    }
                    else if (keys[i] == sharedKey)
                        keys[i] = long.MinValue + aliveCameras[i];
                    inputs[i].sameScreenKey = keys[i];
                }
            }

            // Every player sharing a prebaked camera screen is measured against the
            // same source camera, so its UV transform is exactly identity at t=0.
            int[] bases = new int[count];
            for (int i = 0; i < count; i++)
            {
                int baseIndex = preferredBase[i] >= 0 ? aliveCameras.IndexOf(preferredBase[i]) : i;
                if (preferredBase[i] >= 0)
                {
                    bases[i] = preferredBase[i];
                    inputs[i].mergedScreenPos = validRoomPositions[i]
                        ? CameraScreenPosition(roomCameras[baseIndex], roomPositions[i]) : positions[i];
                    continue;
                }
                for (int j = 0; j < count; j++)
                    if (keys[j] == keys[i])
                    {
                        bool candidateReady = validRoomPositions[j];
                        bool currentReady = validRoomPositions[baseIndex];
                        if ((candidateReady && !currentReady) ||
                            (candidateReady == currentReady && aliveCameras[j] < aliveCameras[baseIndex]))
                            baseIndex = j;
                    }
                bases[i] = aliveCameras[baseIndex];
                Vector2 sharedPosition = validRoomPositions[i]
                    ? CameraScreenPosition(roomCameras[baseIndex], roomPositions[i]) : positions[i];
                inputs[i].mergedScreenPos = sharedPosition;
            }
            dynamicSettings.screenAspect = Futile.screen == null ? 1.75f :
                (float)Futile.screen.pixelWidth / Mathf.Max(1f, Futile.screen.pixelHeight);
            dynamicSettings.permanentSplit = alwaysSplit;
            NoteFrameEvent("solve layout");
            HangMarker = "UpdateDynamicLayout.Solve";
            // This runs once per game tick, not once per rendered frame. At 240 fps
            // Time.deltaTime is a sixth of the tick, and every damping time, hold
            // time, ghost fade and slide in the solver ran six times slower than
            // configured: layouts crawled and kept re-deciding while they crawled.
            float tick = 1f / Mathf.Clamp(game.framesPerSecond, 1, 400);
            dynamicLayout = dynamicSolver.Solve(inputs, tick, dynamicSettings);
            HangMarker = "UpdateDynamicLayout.post";
            if (dynamicLayout.restructured)
            {
                Logger.LogInfo($"[CameraLayout] frame={Time.frameCount} restructured; cells=[{string.Join(",", Array.ConvertAll(dynamicLayout.viewports, v => $"{v.cameraNumber}@({v.centroid.x:0.00},{v.centroid.y:0.00})"))}]");
                dynamicLayout.restructured = false;
            }
            int effectiveCount = dynamicLayout.viewports.Length;
            var allInputs = dynamicLayout.effectiveInputs;
            var allPositions = new Vector2[effectiveCount];
            var allRoomCameras = new RoomCamera[effectiveCount];
            var allRoomPositions = new Vector2[effectiveCount];
            var allValid = new bool[effectiveCount];
            var allBases = new int[effectiveCount];
            for (int i = 0; i < effectiveCount; i++)
            {
                int number = dynamicLayout.viewports[i].cameraNumber;
                int aliveIndex = aliveCameras.IndexOf(number);
                if (aliveIndex >= 0)
                {
                    allPositions[i] = positions[aliveIndex];
                    allRoomCameras[i] = roomCameras[aliveIndex];
                    allRoomPositions[i] = roomPositions[aliveIndex];
                    allValid[i] = validRoomPositions[aliveIndex];
                    allBases[i] = bases[aliveIndex];
                }
                else
                {
                    RoomCamera camera = CameraByNumber(game, number);
                    allRoomCameras[i] = camera;
                    allValid[i] = TryGetPlayerRoomPosition(camera, GetPlayerForCamera(game, number),
                        out allRoomPositions[i]);
                    allPositions[i] = allValid[i]
                        ? CameraScreenPosition(camera, allRoomPositions[i]) : allInputs[i].mergedScreenPos;
                    allBases[i] = number;
                }
            }
            dynamicInputs = allInputs;
            ownScreenPositions = allPositions;
            baseCameraNumbers = allBases;
            for (int i = 0; i < lastBaseByCamera.Length; i++) lastBaseByCamera[i] = -1;
            for (int i = 0; i < effectiveCount; i++)
            {
                int number = dynamicLayout.viewports[i].cameraNumber;
                if (number >= 0 && number < lastBaseByCamera.Length) lastBaseByCamera[number] = allBases[i];
            }
            globalMeterSource = aliveCameras[0];
            for (int i = 0; i < aliveCameras.Count; i++)
                if (CameraByNumber(game, aliveCameras[i])?.hud != null)
                { globalMeterSource = aliveCameras[i]; break; }
            globalPromptSource = globalMeterSource;
            // Game over and pause are raised on camera 0's prompt by the game, no
            // matter which camera currently supplies the shared meters. Those modes
            // win outright; otherwise the first prompt with something to show does.
            bool promptLocked = false;
            foreach (RoomCamera camera in game.cameras)
            {
                if (!(camera?.hud?.textPrompt is HUD.TextPrompt active)) continue;
                if (active.gameOverMode || active.pausedMode)
                {
                    globalPromptSource = camera.cameraNumber;
                    promptLocked = true;
                    break;
                }
            }
            if (!promptLocked)
                foreach (RoomCamera camera in game.cameras)
                    if (camera?.hud?.textPrompt is HUD.TextPrompt active &&
                        (active.show > 0f || (active.messages != null && active.messages.Count > 0)))
                    { globalPromptSource = camera.cameraNumber; break; }
            RouteGlobalMeters(game);
            ApplyDynamicCameraRendering(game);
        }

        private readonly Vector2[] liveSourceScratch = new Vector2[4];

        /// <summary>
        /// Runs every rendered frame. Every cell draws the image of its base camera
        /// (the camera whose prebaked screen the players in that cell share), panned
        /// so the followed slugcat sits at the cell centre. The pan target lerps
        /// from "identity" at split 0 to "centred" at split 1, so two cells that show
        /// one screen line up exactly when their players are close and part again as
        /// they separate, with no blending anywhere. Members of one image group get
        /// one common pan. Positions are interpolated with the sprites' timeStacker,
        /// the result is damped, and it snaps only when the image underneath changed:
        /// a new room or camera position on the base camera, or a cell that moved
        /// elsewhere on screen.
        /// </summary>
        private void RefreshDynamicViewShifts(RainWorldGame game, float timeStacker)
        {
            if (!dynamicActive || dynamicLayout == null || game?.cameras == null) return;
            float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.1f);
            var viewports = dynamicLayout.viewports;
            int count = Mathf.Min(viewports.Length, liveSourceScratch.Length);
            for (int i = 0; i < count; i++)
            {
                var viewport = viewports[i];
                RoomCamera baseCamera = i < baseCameraNumbers.Length ? CameraByNumber(game, baseCameraNumbers[i]) : null;
                Vector2 source;
                if (viewport.ghost || baseCamera?.room == null ||
                    !TryGetInterpolatedScreenPosition(baseCamera, GetPlayerForCamera(game, viewport.cameraNumber), timeStacker, out source))
                    source = i < dynamicInputs.Length && FiniteVector(dynamicInputs[i].mergedScreenPos)
                        ? dynamicInputs[i].mergedScreenPos : new Vector2(0.5f, 0.5f);
                liveSourceScratch[i] = source;
            }
            for (int i = 0; i < count; i++)
            {
                var viewport = viewports[i];
                int number = viewport.cameraNumber;
                if (viewport.ghost || number < 0 || number >= ownViewShifts.Length) continue;
                RoomCamera baseCamera = i < baseCameraNumbers.Length ? CameraByNumber(game, baseCameraNumbers[i]) : null;
                if (baseCamera?.room == null) { ownViewShiftValid[number] = false; continue; }
                // Same two-stage pan as the solver, from live positions: the joined
                // group pans as one image (identical transform for every member, so
                // cells on one screen line up at split 0), then each cell blends
                // towards its own centre by its split against its group-mates.
                int leader = viewport.sharesImageWith >= 0 ? viewport.sharesImageWith : number;
                Vector2 groupSource = Vector2.zero;
                int members = 0;
                for (int j = 0; j < count; j++)
                {
                    var other = viewports[j];
                    int otherLeader = other.sharesImageWith >= 0 ? other.sharesImageWith : other.cameraNumber;
                    if (other.ghost || otherLeader != leader) continue;
                    groupSource += liveSourceScratch[j];
                    members++;
                }
                groupSource = members > 0 ? groupSource / members : liveSourceScratch[i];
                Vector2 source = liveSourceScratch[i];
                Vector2 groupCenter = (viewport.groupMin + viewport.groupMax) * 0.5f;
                Vector2 transformAnchor = Vector2.Lerp(groupSource, groupCenter, viewport.groupSplit);
                Vector2 groupAnchor = transformAnchor + (source - groupSource) * viewport.zoom;
                Vector2 anchor = SplitLayoutSolver.ClampVector(
                    Vector2.Lerp(groupAnchor, viewport.centroid, viewport.innerSplit), viewport.anchorMin, viewport.anchorMax);
                Vector2 target = SplitLayoutSolver.ClampedUvShift(source, anchor, viewport.windowMin, viewport.windowMax, viewport.zoom);
                long sourceKey = ((long)baseCamera.room.abstractRoom.index << 8) | (uint)(baseCamera.currentCameraPosition & 0xFF);
                Vector2 basePosition = InterpolatedCameraPos(baseCamera, timeStacker);
                bool snap = !ownViewShiftValid[number] || ownViewSourceKeys[number] != sourceKey ||
                    (ownViewCellCenters[number] - viewport.centroid).sqrMagnitude > 0.08f * 0.08f;
                if (snap)
                {
                    ownViewShifts[number] = target;
                    ownViewShiftVelocities[number] = Vector2.zero;
                }
                else
                {
                    if (ownViewBaseNumbers[number] != baseCamera.cameraNumber && ownViewBaseNumbers[number] >= 0)
                    {
                        // Same screen, different camera: the two images are offset by
                        // the cameras' follow slack. Carry the shift over so the world
                        // origin on display does not move, then damp to the new target.
                        Vector2 delta = ownViewBasePositions[number] - basePosition;
                        Vector2 size = baseCamera.sSize;
                        if (size.x > 0f && size.y > 0f)
                            ownViewShifts[number] += new Vector2(delta.x / size.x, delta.y / size.y);
                    }
                    Vector2 velocity = ownViewShiftVelocities[number];
                    Vector2 current = ownViewShifts[number];
                    current.x = Mathf.SmoothDamp(current.x, target.x, ref velocity.x, ViewPanSmoothing, Mathf.Infinity, dt);
                    current.y = Mathf.SmoothDamp(current.y, target.y, ref velocity.y, ViewPanSmoothing, Mathf.Infinity, dt);
                    ownViewShifts[number] = current;
                    ownViewShiftVelocities[number] = velocity;
                }
                ownViewShiftValid[number] = true;
                ownViewSourceKeys[number] = sourceKey;
                ownViewCellCenters[number] = viewport.centroid;
                ownViewBaseNumbers[number] = baseCamera.cameraNumber;
                ownViewBasePositions[number] = basePosition;
            }
        }

        public void RainWorldGame_GrafUpdate(On.RainWorldGame.orig_GrafUpdate orig, RainWorldGame self, float timeStacker)
        {
            HangMarker = "RainWorldGame.GrafUpdate(orig)";
            orig(self, timeStacker);
            HangMarker = "RefreshDynamicViewShifts";
            lastTimeStacker = self.pauseUpdate ? 1f : timeStacker;
            try { if (dynamicStyle && !dualDisplays) RefreshDynamicViewShifts(self, lastTimeStacker); }
            catch (Exception error) { LogHookError("RefreshDynamicViewShifts", error); }
            HangMarker = "idle";
            WatchdogFrame = Time.frameCount;
            NoteFrameTime();
        }

        /// <summary>
        /// Where the followed creature sits in the camera's render texture for this
        /// rendered frame, in normalized coordinates. Mirrors RoomCamera.DrawUpdate:
        /// the camera position is interpolated, clamped to its prebaked screen and
        /// floored before sprites are placed against it.
        /// </summary>
        private static bool TryGetInterpolatedScreenPosition(RoomCamera camera, AbstractCreature player,
            float timeStacker, out Vector2 source)
        {
            source = new Vector2(0.5f, 0.5f);
            Creature creature = player?.realizedCreature;
            if (creature == null || camera?.room == null || camera.sSize.x <= 0f || camera.sSize.y <= 0f) return false;
            Vector2 world;
            if (creature.inShortcut)
            {
                Vector2? shortcut = camera.game?.shortcuts?.OnScreenPositionOfInShortCutCreature(camera.room, creature);
                if (shortcut == null) return false;
                world = shortcut.Value;
            }
            else
            {
                if (creature.room != camera.room || creature.mainBodyChunk == null) return false;
                world = Vector2.Lerp(creature.mainBodyChunk.lastPos, creature.mainBodyChunk.pos, timeStacker);
            }
            Vector2 cameraPos = InterpolatedCameraPos(camera, timeStacker);
            source = new Vector2((world.x - cameraPos.x) / camera.sSize.x, (world.y - cameraPos.y) / camera.sSize.y);
            return FiniteVector(source);
        }

        /// <summary>The world position of the render texture's bottom-left corner this frame.</summary>
        private static Vector2 InterpolatedCameraPos(RoomCamera camera, float timeStacker)
        {
            Vector2 cameraPos = Vector2.Lerp(camera.lastPos, camera.pos, timeStacker);
            if (!camera.voidSeaMode && camera.freeMoveRect == null && camera.room?.cameraPositions != null &&
                camera.currentCameraPosition >= 0 && camera.currentCameraPosition < camera.room.cameraPositions.Length)
            {
                Vector2 screen = camera.room.cameraPositions[camera.currentCameraPosition];
                cameraPos.x = Mathf.Clamp(cameraPos.x, screen.x + camera.hDisplace + 8f - 20f, screen.x + camera.hDisplace + 8f + 20f);
                cameraPos.y = Mathf.Clamp(cameraPos.y, screen.y + 8f - 7f, screen.y + 33f);
            }
            return new Vector2(Mathf.Floor(cameraPos.x), Mathf.Floor(cameraPos.y));
        }

        private int ViewportIndex(int cameraNumber)
        {
            if (dynamicLayout?.viewports == null) return -1;
            for (int i = 0; i < dynamicLayout.viewports.Length; i++)
                if (dynamicLayout.viewports[i].cameraNumber == cameraNumber) return i;
            return -1;
        }

        /// <summary>
        /// How visible a divider is: how far apart, in world pixels, the two views
        /// meeting at it currently are. Each cell shows its base camera's picture
        /// offset by its pan, so the views coincide exactly when both cells map display
        /// space to the same world origin; then one camera frames both players and
        /// there is nothing to indicate. The ramp is wide (4 to 300 px) so that the
        /// line fades over the whole glide as two views converge, not just its last
        /// few pixels, and the result is damped so a camera cut (which moves a view by
        /// a screen in one frame) fades the line in rather than popping it.
        /// Player distance on its own was tried as the signal and hid the line while
        /// the views were still visibly apart.
        /// </summary>
        private float DividerAlpha(SplitLayoutSolver.DividerSegment edge)
        {
            int a = edge.firstCamera, b = edge.secondCamera;
            float target = DividerTargetAlpha(edge);
            if (a < 0 || b < 0 || a >= dividerAlphaState.GetLength(0) || b >= dividerAlphaState.GetLength(1)) return target;
            float dt = Mathf.Clamp(Time.deltaTime, 0.001f, 0.1f);
            if (dividerAlphaFrame[a, b] < Time.frameCount - 3)
            {
                dividerAlphaState[a, b] = target;
                dividerAlphaVelocity[a, b] = 0f;
            }
            else
                dividerAlphaState[a, b] = Mathf.SmoothDamp(dividerAlphaState[a, b], target, ref dividerAlphaVelocity[a, b],
                    DividerFadeSeconds, Mathf.Infinity, dt);
            dividerAlphaFrame[a, b] = Time.frameCount;
            return dividerAlphaState[a, b];
        }

        private float DividerTargetAlpha(SplitLayoutSolver.DividerSegment edge)
        {
            if (!(rainworldGameObject?.processManager?.currentMainLoop is RainWorldGame game)) return edge.alpha;
            int ia = ViewportIndex(edge.firstCamera), ib = ViewportIndex(edge.secondCamera);
            if (ia < 0 || ib < 0 || ia >= baseCameraNumbers.Length || ib >= baseCameraNumbers.Length) return edge.alpha;
            var va = dynamicLayout.viewports[ia];
            var vb = dynamicLayout.viewports[ib];
            if (va.ghost || vb.ghost) return edge.alpha;
            RoomCamera ca = CameraByNumber(game, baseCameraNumbers[ia]);
            RoomCamera cb = CameraByNumber(game, baseCameraNumbers[ib]);
            if (ca?.room == null || cb?.room == null || ca.room != cb.room) return 1f;
            if (Mathf.Abs(va.zoom - vb.zoom) > 0.01f) return 1f;
            int a = edge.firstCamera, b = edge.secondCamera;
            if (a < 0 || b < 0 || a >= ownViewShifts.Length || b >= ownViewShifts.Length ||
                !ownViewShiftValid[a] || !ownViewShiftValid[b]) return edge.alpha;
            Vector2 originA = InterpolatedCameraPos(ca, lastTimeStacker) + Vector2.Scale(ownViewShifts[a], ca.sSize);
            Vector2 originB = InterpolatedCameraPos(cb, lastTimeStacker) + Vector2.Scale(ownViewShifts[b], cb.sSize);
            float misalignment = (originA - originB).magnitude;
            float t = Mathf.Clamp01((misalignment - DividerInvisibleBelowPixels) /
                (DividerOpaqueAbovePixels - DividerInvisibleBelowPixels));
            return t * t * (3f - 2f * t);
        }

        // The line must cover the seam for as long as the two images are offset
        // there. A wide 4→300 px ramp left it nearly transparent at 30–80 px of
        // offset, which is exactly the follow slack between two cameras on one
        // screen and the pan difference of a merging pair: the seam then read as the
        // picture shearing. Opaque above 32 px; the damping below still makes the
        // last stretch of the merge a visible fade rather than a cut.
        private const float DividerInvisibleBelowPixels = 3f;
        private const float DividerOpaqueAbovePixels = 32f;
        private const float DividerFadeSeconds = 0.25f;
        private readonly float[,] dividerAlphaState = new float[4, 4];
        private readonly float[,] dividerAlphaVelocity = new float[4, 4];
        private readonly int[,] dividerAlphaFrame = new int[4, 4];

        private static void PolygonBounds(Vector2[] polygon, out Vector2 min, out Vector2 max)
        {
            min = new Vector2(1f, 1f); max = Vector2.zero;
            ExpandBounds(polygon, ref min, ref max);
        }

        /// <summary>
        /// True when every camera in the layout follows a player who is inside the
        /// same shelter. The first camera must actually be showing it, so the
        /// collapsed view is never a camera that is still loading somewhere else.
        /// </summary>
        private bool AllPlayersInOneShelter(RainWorldGame game, List<int> aliveCameras)
        {
            AbstractRoom shelter = null;
            for (int i = 0; i < aliveCameras.Count; i++)
            {
                AbstractCreature player = GetPlayerForCamera(game, aliveCameras[i]);
                AbstractRoom room = player?.realizedCreature?.room?.abstractRoom ?? player?.Room;
                if (room == null || !room.shelter) return false;
                if (shelter == null) shelter = room;
                else if (shelter != room) return false;
            }
            return shelter != null && CameraByNumber(game, aliveCameras[0])?.room?.abstractRoom == shelter;
        }

        private static void ExpandBounds(Vector2[] polygon, ref Vector2 min, ref Vector2 max)
        {
            if (polygon == null) return;
            for (int vertex = 0; vertex < polygon.Length; vertex++)
            {
                Vector2 point = polygon[vertex];
                min.x = Mathf.Min(min.x, point.x); min.y = Mathf.Min(min.y, point.y);
                max.x = Mathf.Max(max.x, point.x); max.y = Mathf.Max(max.y, point.y);
            }
        }

        private static RoomCamera CameraByNumber(RainWorldGame game, int number)
        {
            if (game?.cameras == null) return null;
            for (int i = 0; i < game.cameras.Length; i++)
                if (game.cameras[i]?.cameraNumber == number) return game.cameras[i];
            return null;
        }

        private SplitLayoutSolver.ViewportState DynamicViewportForCamera(int cameraNumber)
        {
            if (dynamicLayout?.viewports == null) return null;
            for (int i = 0; i < dynamicLayout.viewports.Length; i++)
                if (dynamicLayout.viewports[i].cameraNumber == cameraNumber) return dynamicLayout.viewports[i];
            return null;
        }

        private void JollyOffRoom_Update(
            On.JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyOffRoom.orig_Update orig,
            JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyOffRoom self)
        {
            orig(self);
            if (!dynamicStyle || dualDisplays || self == null || dynamicLayout == null) return;
            RoomCamera camera = self.jollyHud.Camera;
            var viewport = DynamicViewportForCamera(camera.cameraNumber);
            if (viewport?.polygon == null) return;
            bool merged = !alwaysSplit;
            foreach (var region in dynamicLayout.viewports)
                if (!region.ghost && region.splitAmount > 0.05f) { merged = false; break; }
            if (merged || (TryProjectJollyPlayer(self, out Vector2 projected) &&
                PointInsideDynamicRegion(camera.cameraNumber, projected)))
            {
                self.hidden = true;
                self.alpha = 0f;
                return;
            }
            if (self.hidden || !TryProjectJollyPlayer(self, out Vector2 target)) return;
            if (!RayToPolygonEdge(viewport.centroid, target - viewport.centroid, viewport.polygon,
                out Vector2 edge)) return;
            Vector2 inward = viewport.centroid - edge;
            if (inward.sqrMagnitude > 0.00001f)
                edge += inward.normalized * (15f / Mathf.Max(1f, camera.sSize.x));
            Vector2 local = edge - viewport.centroid + new Vector2(0.5f, 0.5f);
            self.drawPos = new Vector2(local.x * camera.sSize.x, local.y * camera.sSize.y);
        }

        private bool TryProjectJollyPlayer(
            JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyOffRoom pointer, out Vector2 point)
        {
            point = Vector2.zero;
            RoomCamera camera = pointer?.jollyHud?.Camera;
            var view = camera == null ? null : DynamicViewportForCamera(camera.cameraNumber);
            if (view == null || camera.sSize.x <= 0f || camera.sSize.y <= 0f) return false;
            AbstractCreature followed = camera.followAbstractCreature;
            AbstractCreature target = pointer.jollyHud.RealizedPlayer?.abstractCreature;
            if (followed?.world != null && target?.world == followed.world &&
                followed.realizedCreature?.mainBodyChunk != null && target.realizedCreature?.mainBodyChunk != null &&
                followed.Room != null && target.Room != null &&
                (followed.Room == target.Room ||
                 (followed.Room.mapPos.sqrMagnitude > 0.01f && target.Room.mapPos.sqrMagnitude > 0.01f)))
            {
                Vector2 followedWorld = followed.world.RoomToWorldPos(
                    followed.realizedCreature.mainBodyChunk.pos, followed.Room.index);
                Vector2 targetWorld = target.world.RoomToWorldPos(
                    target.realizedCreature.mainBodyChunk.pos, target.Room.index);
                Vector2 delta = targetWorld - followedWorld;
                point = view.regionAnchor + new Vector2(delta.x / camera.sSize.x,
                    delta.y / camera.sSize.y) * view.zoom;
                return FiniteVector(point);
            }
            int otherCamera = (target?.state as PlayerState)?.playerNumber ?? -1;
            var other = DynamicViewportForCamera(otherCamera);
            if (other == null) return false;
            point = view.regionAnchor + (other.site - view.site);
            return FiniteVector(point);
        }

        private bool PointInsideDynamicRegion(int cameraNumber, Vector2 point)
        {
            var view = DynamicViewportForCamera(cameraNumber);
            if (view?.polygon == null) return false;
            int leader = view.sharesImageWith >= 0 ? view.sharesImageWith : cameraNumber;
            foreach (var member in dynamicLayout.viewports)
                if (member.cameraNumber == leader || member.sharesImageWith == leader)
                    if (PointInsidePolygon(member.polygon, point)) return true;
            return false;
        }

        private static bool PointInsidePolygon(Vector2[] polygon, Vector2 point)
        {
            if (polygon == null) return false;
            for (int i = 0; i < polygon.Length; i++)
                if (Cross(polygon[(i + 1) % polygon.Length] - polygon[i], point - polygon[i]) < -0.0001f)
                    return false;
            return true;
        }

        private static bool RayToPolygonEdge(Vector2 origin, Vector2 direction, Vector2[] polygon,
            out Vector2 hit)
        {
            hit = Vector2.zero;
            if (direction.sqrMagnitude < 0.00001f || polygon == null) return false;
            float closest = float.PositiveInfinity;
            for (int i = 0; i < polygon.Length; i++)
            {
                Vector2 a = polygon[i], edge = polygon[(i + 1) % polygon.Length] - a;
                float denominator = Cross(direction, edge);
                if (Mathf.Abs(denominator) < 0.00001f) continue;
                float rayT = Cross(a - origin, edge) / denominator;
                float edgeT = Cross(a - origin, direction) / denominator;
                if (rayT <= 0f || edgeT < 0f || edgeT > 1f || rayT >= closest) continue;
                closest = rayT;
                hit = origin + direction * rayT;
            }
            return !float.IsInfinity(closest);
        }

        private static float Cross(Vector2 first, Vector2 second)
        {
            return first.x * second.y - first.y * second.x;
        }

        /// <summary>
        /// The pause menu is shared by every player, so it is drawn once on the global
        /// stage. That stage is composited last, at native screen coordinates and
        /// without polygon clipping, so the menu looks exactly like the single-player
        /// one and Futile's pointer hit-testing matches what is on screen.
        /// </summary>
        /// <summary>
        /// The shared meters belong to one camera's HUD, and vanilla only reveals a
        /// HUD for its own owner's map button. Any player holding the map should see
        /// the shared food, karma and rain meters come up.
        /// </summary>
        private void HUD_Update(On.HUD.HUD.orig_Update orig, HUD.HUD self)
        {
            orig(self);
            if (!dynamicStyle || dualDisplays || !dynamicActive || self.showKarmaFoodRain) return;
            if (!(self.rainWorld?.processManager?.currentMainLoop is RainWorldGame game) || game.cameras == null) return;
            RoomCamera source = CameraByNumber(game, globalMeterSource);
            if (source?.hud != self || game.session?.Players == null) return;
            foreach (AbstractCreature player in game.session.Players)
                if (player?.realizedCreature is Player realized && !realized.dead &&
                    (realized.RevealMap || realized.showKarmaFoodRainTime > 0))
                {
                    self.showKarmaFoodRain = true;
                    return;
                }
        }

        private void MovePauseMenuGlobal(Menu.PauseMenu menu)
        {
            if (menu?.container == null || globalHudStage == null) return;
            globalHudStage.AddChild(menu.container);
            menu.container.SetPosition(Vector2.zero);
        }

        private static bool TryGetPlayerRoomPosition(RoomCamera camera, AbstractCreature player, out Vector2 position)
        {
            position = Vector2.zero;
            Creature creature = player?.realizedCreature;
            if (creature == null) return false;
            if (creature.inShortcut)
            {
                if (camera?.room == null) return false;
                Vector2? shortcut = camera.game?.shortcuts?.OnScreenPositionOfInShortCutCreature(camera.room, creature);
                if (shortcut == null) return false;
                position = shortcut.Value;
            }
            else
            {
                if (creature.mainBodyChunk == null) return false;
                position = creature.mainBodyChunk.pos;
            }
            return FiniteVector(position);
        }

        private static bool FiniteVector(Vector2 value)
        {
            return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.x) && !float.IsInfinity(value.y);
        }

        private static Vector2 CameraScreenPosition(RoomCamera camera, Vector2 roomPosition)
        {
            if (camera == null || camera.sSize.x <= 0f || camera.sSize.y <= 0f) return new Vector2(0.5f, 0.5f);
            return new Vector2((roomPosition.x - camera.pos.x) / camera.sSize.x,
                (roomPosition.y - camera.pos.y) / camera.sSize.y);
        }

        private static long ScreenKey(Room room, int cameraPosition)
        {
            uint world = (uint)(room.world?.name?.GetHashCode() ?? 0);
            uint local = unchecked((uint)(room.abstractRoom.index * 31 + cameraPosition));
            return ((long)world << 32) | local;
        }

        private void ApplyDynamicCameraRendering(RainWorldGame game)
        {
            if (dynamicLayout == null) return;
            bool mergedFullScreen = !alwaysSplit;
            for (int i = 0; i < dynamicLayout.viewports.Length; i++)
                if (dynamicLayout.viewports[i].splitAmount > 0f) mergedFullScreen = false;
            int directCamera = baseCameraNumbers.Length > 0 ? baseCameraNumbers[0] :
                dynamicLayout.viewports[0].cameraNumber;
            renderedCameraNumbers.Clear();
            if (mergedFullScreen) renderedCameraNumbers.Add(directCamera);
            else
            {
                // Only base cameras render: a cell always draws the image of the
                // camera whose prebaked screen its players share.
                for (int i = 0; i < dynamicLayout.viewports.Length; i++)
                {
                    if (dynamicLayout.viewports[i].ghost) continue;
                    int baseCamera = baseCameraNumbers[i];
                    if (!renderedCameraNumbers.Contains(baseCamera)) renderedCameraNumbers.Add(baseCamera);
                }
                renderedCameraNumbers.Sort();
            }
            // HUD stages are separate from world stages, so even a full-screen
            // merged image passes through the final compositor in Dynamic style.
            dynamicActive = true;
            FilterMode zoomFilter = Options?.ZoomedFilter.Value == "Point"
                ? FilterMode.Point : FilterMode.Bilinear;
            for (int i = 0; i < fcameras.Length; i++)
            {
                CameraListener listener = cameraListeners[i];
                if (listener == null || fcameras[i] == null) continue;
                if (listener.renderTexture != null)
                {
                    bool zoomed = false;
                    for (int v = 0; v < dynamicLayout.viewports.Length && !zoomed; v++)
                    {
                        var view = dynamicLayout.viewports[v];
                        zoomed = !view.ghost && view.zoom < 0.999f && (view.cameraNumber == i || baseCameraNumbers[v] == i);
                    }
                    listener.renderTexture.filterMode = zoomed ? zoomFilter : FilterMode.Point;
                }
                fcameras[i].cullingMask = 1 << worldLayers[i];
                listener.BindToDisplay(Display.main);
                listener.dynamicCompositing = dynamicActive;
                listener.direct = !dynamicActive;
                if (renderedCameraNumbers.Contains(i)) listener.PrepareForRendering();
                fcameras[i].enabled = renderedCameraNumbers.Contains(i);
                listener.MarkRenderingExpected(fcameras[i].enabled);
            }
            if (dynamicCompositorCamera != null)
            {
                ReinitDynamicCompositorTexture();
                if (dynamicActive && compositorExpectedSinceFrame < 0)
                    compositorExpectedSinceFrame = Time.frameCount;
                dynamicCompositorCamera.enabled = dynamicActive;
            }
            for (int i = 0; i < hudCameras.Length; i++)
                if (hudCameras[i] != null)
                {
                    bool expected = false;
                    for (int v = 0; v < dynamicLayout.viewports.Length && !expected; v++)
                        expected = dynamicLayout.viewports[v].cameraNumber == i && !dynamicLayout.viewports[v].ghost;
                    if (expected && hudExpectedSinceFrames[i] < 0)
                        hudExpectedSinceFrames[i] = Time.frameCount;
                    else if (!expected) hudExpectedSinceFrames[i] = -1;
                    hudCameras[i].enabled = expected;
                }
            if (globalHudCamera != null)
            {
                // Isolating each view's world onto its own layer left Futile's root
                // stage rendered by nobody. Menu.MouseCursor.BumToFront parents the
                // cursor there, so the pause menu had no visible pointer; the same is
                // true of anything else the game or another mod leaves on the root
                // stage. Draw it over the finished composite at native coordinates.
                globalHudCamera.cullingMask = (1 << globalHudLayer) |
                    (1 << (Futile.stage != null ? Futile.stage.layer : 0));
                if (globalHudExpectedSinceFrame < 0) globalHudExpectedSinceFrame = Time.frameCount;
                globalHudCamera.enabled = true;
            }
            // Build the human-readable layout line only when the structure changed;
            // this runs every tick and string work here was pure garbage otherwise.
            long signature = mergedFullScreen ? 1L : 0L;
            for (int i = 0; i < dynamicLayout.viewports.Length; i++)
            {
                var v = dynamicLayout.viewports[i];
                signature = signature * 131 + v.cameraNumber * 7 + v.sharesImageWith + 2;
                signature = signature * 131 + baseCameraNumbers[i] + (v.rendering ? 64 : 0) + (v.ghost ? 128 : 0);
            }
            for (int i = 0; i < renderedCameraNumbers.Count; i++) signature = signature * 131 + renderedCameraNumbers[i];
            if (signature != lastLayoutSignature)
            {
                lastLayoutSignature = signature;
                string key = $"groups=[{string.Join(",", Array.ConvertAll(dynamicLayout.viewports, v => $"{v.cameraNumber}:{v.sharesImageWith}"))}]|sources=[{string.Join(",", baseCameraNumbers)}]|rendering=[{string.Join(",", renderedCameraNumbers)}]|direct={mergedFullScreen}";
                Logger.LogInfo($"[CameraLayout] frame={Time.frameCount} {key}");
                lastDynamicLayoutKey = key;
            }
        }

        private void CompositeDynamicLayout(DynamicCompositor compositor)
        {
            if (!dynamicActive || dynamicLayout == null || compositor.texturedMaterial == null) return;
            HangMarker = "CompositeDynamicLayout";
            RenderTexture destination = Display.main.Extras().renderTexture;
            if (destination == null) return;
            RenderTexture previous = RenderTexture.active;
            Graphics.SetRenderTarget(destination);
            GL.PushMatrix();
            try
            {
                GL.LoadOrtho();
                GL.Clear(true, true, Color.black);
                int liveSource = -1;
                bool hasGhost = false, fullyMerged = !alwaysSplit;
                for (int i = 0; i < dynamicLayout.viewports.Length; i++)
                {
                    var view = dynamicLayout.viewports[i];
                    if (view.ghost) { hasGhost = true; fullyMerged = false; continue; }
                    if (liveSource < 0) liveSource = baseCameraNumbers[i];
                    if (baseCameraNumbers[i] != liveSource || view.splitAmount > 0.001f ||
                        view.imageBlend > 0.001f || view.zoom < 0.999f) fullyMerged = false;
                }
                if (liveSource >= 0 && (hasGhost || fullyMerged))
                    DrawDynamicPolygon(compositor.texturedMaterial,
                        cameraListeners[liveSource]?.renderTexture, FullScreenPolygon,
                        Vector2.zero, 1f);
                // At a complete merge, render one native image. Otherwise every cell
                // draws its base camera's image, opaque, with its own pan. Nothing is
                // ever alpha-blended between two images: when two cells share a screen
                // and their split amount is 0 their pans are identical, so the seam
                // simply is not there.
                if (!fullyMerged)
                {
                    // While cells slide to new rectangles they may overlap or leave
                    // gaps, so the resting layout is drawn underneath first.
                    if (dynamicLayout.sliding)
                        for (int i = 0; i < dynamicLayout.viewports.Length; i++)
                            DrawWorldCell(compositor.texturedMaterial, i, dynamicLayout.viewports[i].targetPolygon);
                    for (int i = 0; i < dynamicLayout.viewports.Length; i++)
                        DrawWorldCell(compositor.texturedMaterial, i, dynamicLayout.viewports[i].polygon);
                }
                // Dividers separate world images, so they belong above the world
                // and below every overlay. Drawing them last painted opaque black
                // over whatever HUD or pause-menu pixels happened to sit under a bar.
                DrawDynamicDividers(compositor.solidMaterial);
                DrawDynamicHud(compositor.texturedMaterial);
                if (dynamicDebugOverlay) DrawDynamicOutlines(compositor.solidMaterial);
                compositor.lastCompositeFrame = Time.frameCount;
                compositorRecoveries = 0;
                for (int i = 0; i < renderedCameraNumbers.Count; i++)
                    cameraListeners[renderedCameraNumbers[i]].lastCompositeFrame = Time.frameCount;
            }
            catch (Exception error)
            {
                Logger.LogError($"[CameraLayout] compositor error: {error}");
                dynamicPipelineFailed = true;
            }
            finally
            {
                GL.PopMatrix();
                RenderTexture.active = previous;
                HangMarker = "idle";
            }
        }

        private void DrawWorldCell(Material material, int index, Vector2[] polygon)
        {
            var viewport = dynamicLayout.viewports[index];
            if (viewport.ghost || polygon == null) return;
            int own = viewport.cameraNumber;
            int source = index < baseCameraNumbers.Length ? baseCameraNumbers[index] : own;
            bool tracked = own >= 0 && own < lastDrawnListeners.Length;
            CameraListener listener = FreshListener(source) ?? FreshListener(own);
            // A base switch to a camera that was disabled until this tick has no
            // fresh image yet; keep last frame's rather than leaving the cell black.
            if (listener == null && tracked && lastDrawnListeners[own]?.renderTexture != null)
                listener = lastDrawnListeners[own];
            if (listener == null) return;
            if (tracked) lastDrawnListeners[own] = listener;
            Vector2 shift = own >= 0 && own < ownViewShifts.Length && ownViewShiftValid[own] ? ownViewShifts[own] : Vector2.zero;
            DrawDynamicPolygon(material, listener.renderTexture, polygon, shift, 1f, viewport.zoom);
        }

        /// <summary>A camera's listener if that camera rendered within the last two frames.</summary>
        private static CameraListener FreshListener(int number)
        {
            if (number < 0 || number >= cameraListeners.Length) return null;
            CameraListener listener = cameraListeners[number];
            if (listener?.renderTexture == null || fcameras[number] == null || !fcameras[number].enabled ||
                listener.lastPostRenderFrame < Time.frameCount - 2) return null;
            return listener;
        }

        private static void DrawDynamicPolygon(Material material, RenderTexture source,
            Vector2[] polygon, Vector2 uvShift, float alpha, float zoom = 1f)
        {
            if (source == null || polygon == null || polygon.Length < 3) return;
            // At native scale the source and the display texture share one pixel
            // grid, so snapping the pan to whole texels keeps the image exact.
            // A fractional shift makes every displayed pixel a blend of two
            // neighbours, which reads as the view softening and shimmering as the
            // followed slugcat's body chunk jitters.
            if (zoom > 0.999f && source.width > 0 && source.height > 0)
                uvShift = new Vector2(Mathf.Round(uvShift.x * source.width) / source.width,
                    Mathf.Round(uvShift.y * source.height) / source.height);
            material.mainTexture = source;
            if (!material.SetPass(0)) return;
            GL.Begin(GL.TRIANGLES);
            GL.Color(new Color(1f, 1f, 1f, alpha));
            for (int i = 1; i < polygon.Length - 1; i++)
            {
                DrawVertex(polygon[0], uvShift, zoom);
                DrawVertex(polygon[i], uvShift, zoom);
                DrawVertex(polygon[i + 1], uvShift, zoom);
            }
            GL.End();
        }

        private static readonly Vector2[] FullScreenPolygon =
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f),
            new Vector2(1f, 1f), new Vector2(0f, 1f)
        };

        private static void DrawVertex(Vector2 position, Vector2 uvShift, float zoom)
        {
            GL.TexCoord2(position.x / zoom + uvShift.x, position.y / zoom + uvShift.y);
            GL.Vertex3(position.x, position.y, 0f);
        }

        private void DrawDynamicHud(Material material)
        {
            for (int i = 0; i < dynamicLayout.viewports.Length; i++)
            {
                var view = dynamicLayout.viewports[i];
                if (view.ghost) continue;
                // The HUD is sampled at its native scale, translated so the map's
                // conventional screen center lands at the owned cell centroid. Each
                // view keeps its own HUD even while paused; the pause menu itself is
                // a single shared one drawn later on the global stage.
                DrawDynamicPolygon(material, hudTextures[view.cameraNumber], view.polygon,
                    DynamicHudShift(view), 1f);
            }
        }

        /// <summary>
        /// The uv translation the compositor draws a view's HUD texture with.
        /// Anything positioned against the composited result has to use this same
        /// value, or it lands somewhere other than where the HUD is drawn.
        /// </summary>
        private Vector2 DynamicHudShift(SplitLayoutSolver.ViewportState view)
        {
            return HudUvShift(view.polygon, new Vector2(0.5f, 0.5f) - view.centroid);
        }

        /// <summary>
        /// The HUD textures clamp at their edges, so a cell that samples outside
        /// [0,1] smears the border row or column of another view's HUD across
        /// itself. Keep the sampled window inside the texture whenever the cell
        /// is small enough to fit; that is every cell up to a full-screen one.
        /// </summary>
        private static Vector2 HudUvShift(Vector2[] polygon, Vector2 shift)
        {
            Vector2 min = new Vector2(1f, 1f), max = Vector2.zero;
            ExpandBounds(polygon, ref min, ref max);
            if (min.x <= max.x && max.x - min.x <= 1f) shift.x = Mathf.Clamp(shift.x, -min.x, 1f - max.x);
            if (min.y <= max.y && max.y - min.y <= 1f) shift.y = Mathf.Clamp(shift.y, -min.y, 1f - max.y);
            return shift;
        }

        private void DrawDynamicDividers(Material material)
        {
            if (material == null || !material.SetPass(0)) return;
            for (int i = 0; i < dynamicLayout.dividers.Length; i++)
            {
                var edge = dynamicLayout.dividers[i];
                Vector2 along = edge.end - edge.start;
                if (along.sqrMagnitude < 0.00001f) continue;
                float alpha = DividerAlpha(edge);
                if (alpha <= 0.002f) continue;
                Vector2 normal = new Vector2(-along.y, along.x).normalized;
                // Pad in pixels along the normal so a tilted line is as thick as a straight one.
                float pixelWidth = Mathf.Max(1f, Futile.screen.pixelWidth), pixelHeight = Mathf.Max(1f, Futile.screen.pixelHeight);
                Vector2 pad = new Vector2(normal.x * edge.width / pixelWidth, normal.y * edge.width / pixelHeight);
                GL.Begin(GL.QUADS);
                GL.Color(new Color(0f, 0f, 0f, alpha));
                GL.Vertex3(edge.start.x - pad.x, edge.start.y - pad.y, 0f);
                GL.Vertex3(edge.start.x + pad.x, edge.start.y + pad.y, 0f);
                GL.Vertex3(edge.end.x + pad.x, edge.end.y + pad.y, 0f);
                GL.Vertex3(edge.end.x - pad.x, edge.end.y - pad.y, 0f);
                GL.End();
            }
        }

        private void DrawDynamicOutlines(Material material)
        {
            if (material == null || !material.SetPass(0)) return;
            for (int i = 0; i < dynamicLayout.viewports.Length; i++)
            {
                var viewport = dynamicLayout.viewports[i];
                GL.Begin(GL.LINES);
                GL.Color(Color.green);
                for (int j = 0; j < viewport.polygon.Length; j++)
                {
                    Vector2 a = viewport.polygon[j], b = viewport.polygon[(j + 1) % viewport.polygon.Length];
                    GL.Vertex3(a.x, a.y, 0f);
                    GL.Vertex3(b.x, b.y, 0f);
                }
                GL.End();
                Vector2 site = viewport.site;
                const float marker = 0.008f;
                GL.Begin(GL.QUADS);
                GL.Color(Color.red);
                GL.Vertex3(site.x - marker, site.y - marker, 0f);
                GL.Vertex3(site.x + marker, site.y - marker, 0f);
                GL.Vertex3(site.x + marker, site.y + marker, 0f);
                GL.Vertex3(site.x - marker, site.y + marker, 0f);
                GL.End();
            }
        }

        private void DrawDynamicDebugLabels()
        {
            if (dynamicLayout == null) return;
            float screenHeight = Screen.height;
            for (int i = 0; i < dynamicLayout.viewports.Length; i++)
            {
                var view = dynamicLayout.viewports[i];
                Vector2 p = view.centroid;
                string source = view.sharesImageWith < 0 ? "self" : view.sharesImageWith.ToString();
                GUI.Label(new Rect(p.x * Screen.width - 105f, (1f - p.y) * screenHeight - 12f, 220f, 24f),
                    $"P{view.cameraNumber + 1} {view.areaFraction:P0} zoom {view.zoom:F2} src {source}");
            }
            float y = 12f;
            for (int i = 0; i < dynamicLayout.viewports.Length; i++)
            for (int j = i + 1; j < dynamicLayout.viewports.Length; j++)
            {
                GUI.Label(new Rect(12f, y, 190f, 22f),
                    $"t{dynamicLayout.viewports[i].cameraNumber},{dynamicLayout.viewports[j].cameraNumber}={dynamicLayout.pairSplitAmounts[i,j]:F2}");
                y += 20f;
            }
        }
    }
}
