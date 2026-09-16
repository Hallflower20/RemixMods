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
        private readonly Vector2[] dynamicCameraTargets = new Vector2[4];
        private readonly bool[] hasDynamicCameraTarget = new bool[4];
        private readonly Vector2[] dynamicFollowVelocities = new Vector2[4];
        private readonly int[] lastPlayerSafetyLogFrames = { -10000, -10000, -10000, -10000 };
        private Camera dynamicCompositorCamera;
        private DynamicCompositor dynamicCompositor;
        private readonly FStage[] hudStages = new FStage[4];
        private readonly FStage[] worldStages = new FStage[4];
        private readonly FContainer[] worldNameContainers = new FContainer[4];
        private readonly int[] worldLayers = new int[4];
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
            RouteFoodMeter(camera.hud.foodMeter, destination);
            // Food plops create short-lived FadeCircles after the permanent
            // meter nodes have been routed. Keep those circles with the pips
            // and backing fade instead of leaving them in the per-view HUD2.
            if (camera.hud.foodMeter != null && camera.hud.fadeCircles != null)
                foreach (HUD.FadeCircle effect in camera.hud.fadeCircles)
                    if (effect?.circle?.sprite != null &&
                        IsFoodMeterEffect(camera.hud.foodMeter, effect.circle.pos))
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
            HUD.TextPrompt prompt = camera.hud.textPrompt;
            if (prompt != null)
            {
                FContainer promptDestination = camera.cameraNumber == globalPromptSource ? globalHudStage : null;
                RouteNode(prompt.label, promptDestination);
                RouteNode(prompt.musicSprite, promptDestination);
                RouteNode(prompt.fullscreenFade, promptDestination);
                if (prompt.sprites != null)
                    foreach (FSprite sprite in prompt.sprites) RouteNode(sprite, promptDestination);
                if (prompt.symbols != null)
                    foreach (IconSymbol symbol in prompt.symbols)
                    {
                        RouteNode(symbol.symbolSprite, promptDestination);
                        RouteNode(symbol.shadowSprite1, promptDestination);
                        RouteNode(symbol.shadowSprite2, promptDestination);
                    }
            }
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
            lastDynamicLayoutKey = null;
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
                hasDynamicCameraTarget[i] = false;
                dynamicFollowVelocities[i] = Vector2.zero;
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
                long screenKey = !cameraRoomReady
                    ? long.MinValue + cameraNumber
                    : ScreenKey(room, camera.currentCameraPosition);
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
                inputs[i] = new SplitLayoutSolver.PlayerInput { playerIndex = cameraNumber, worldPos = worldPos,
                    sameScreenKey = screenKey, validWorldPos = validWorld, mergedScreenPos = new Vector2(0.5f, 0.5f) };
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
            dynamicLayout = dynamicSolver.Solve(inputs, Time.deltaTime, dynamicSettings);
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
            globalMeterSource = aliveCameras[0];
            for (int i = 0; i < aliveCameras.Count; i++)
                if (CameraByNumber(game, aliveCameras[i])?.hud != null)
                { globalMeterSource = aliveCameras[i]; break; }
            globalPromptSource = globalMeterSource;
            foreach (RoomCamera camera in game.cameras)
                if (camera?.hud?.textPrompt is HUD.TextPrompt active &&
                    (active.show > 0f || (active.messages != null && active.messages.Count > 0)))
                { globalPromptSource = camera.cameraNumber; break; }
            RouteGlobalMeters(game);
            ComputeDynamicCameraTargets(allRoomCameras, allRoomPositions, allValid);
            ApplyDynamicCameraRendering(game);
        }

        private void ComputeDynamicCameraTargets(RoomCamera[] cameras, Vector2[] positions, bool[] valid)
        {
            for (int i = 0; i < hasDynamicCameraTarget.Length; i++) hasDynamicCameraTarget[i] = false;
            if (dynamicLayout == null) return;
            for (int i = 0; i < dynamicLayout.viewports.Length; i++)
            {
                var viewport = dynamicLayout.viewports[i];
                int number = viewport.cameraNumber;
                RoomCamera camera = cameras[i];
                if (viewport.ghost || camera == null || !valid[i]) continue;
                SetDynamicCameraTarget(camera, positions[i], viewport);
            }
        }

        private void SetDynamicCameraTarget(RoomCamera camera, Vector2 playerPosition,
            SplitLayoutSolver.ViewportState viewport)
        {
            int number = camera.cameraNumber;
            Vector2 min = new Vector2(1f, 1f), max = Vector2.zero;
            for (int j = 0; j < dynamicLayout.viewports.Length; j++)
                if (dynamicLayout.viewports[j].cameraNumber == number ||
                    dynamicLayout.viewports[j].sharesImageWith == number ||
                    (viewport.sharesImageWith >= 0 &&
                     dynamicLayout.viewports[j].sharesImageWith == viewport.sharesImageWith))
                    ExpandBounds(dynamicLayout.viewports[j].polygon, ref min, ref max);
            Vector2 boundsCenter = (min + max) * 0.5f;
            Vector2 nativeSource = CameraScreenPosition(camera, playerPosition);
            Vector2 wantedSource = SplitLayoutSolver.FollowSourcePosition(nativeSource,
                viewport.groupTargetAnchor, boundsCenter, viewport.zoom, viewport.splitAmount);
            dynamicCameraTargets[number] = playerPosition - new Vector2(wantedSource.x * camera.sSize.x,
                wantedSource.y * camera.sSize.y);
            hasDynamicCameraTarget[number] = true;
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

        private bool ApplyDynamicCameraFollow(RoomCamera camera)
        {
            if (!dynamicStyle || dualDisplays || dynamicLayout == null || camera == null) return false;
            int number = camera.cameraNumber;
            if (number < 0 || number >= hasDynamicCameraTarget.Length || !hasDynamicCameraTarget[number]) return true;
            SplitLayoutSolver.ViewportState viewport = null;
            for (int i = 0; i < dynamicLayout.viewports.Length; i++)
                if (dynamicLayout.viewports[i].cameraNumber == number) { viewport = dynamicLayout.viewports[i]; break; }
            if (viewport == null) return true;
            RoomCamera shared = viewport.sharesImageWith >= 0 ? CameraByNumber(camera.game, viewport.sharesImageWith) : null;
            if (viewport.splitAmount <= 0f && viewport.imageBlend <= 0f)
            {
                if (shared != null && shared.room == camera.room &&
                    shared.currentCameraPosition == camera.currentCameraPosition)
                    camera.pos = shared.pos;
                return true; // Keep native follow for a full-screen merged image.
            }
            AbstractCreature player = GetPlayerForCamera(camera.game, number);
            Creature realized = player?.realizedCreature;
            if (realized == null || (!realized.inShortcut && realized.room != camera.room) ||
                !TryGetPlayerRoomPosition(camera, player, out Vector2 livePosition))
                return true; // The old room must not steer a transitioning camera.
            SetDynamicCameraTarget(camera, livePosition, viewport);
            if (shared != null && viewport.imageBlend <= 0f && shared.room == camera.room &&
                shared.currentCameraPosition == camera.currentCameraPosition)
            {
                camera.pos = shared.pos;
                return true;
            }
            Vector2 target = dynamicCameraTargets[number] + camera.followCreatureInputForward * 2f;
            if (shared != null && shared.room == camera.room && viewport.imageBlend < 1f)
                target = Vector2.Lerp(shared.pos, target, viewport.imageBlend);
            Vector2 velocity = dynamicFollowVelocities[number];
            camera.pos.x = SmoothDynamicCameraAxis(camera.pos.x, target.x, camera.sSize.x, ref velocity.x);
            camera.pos.y = SmoothDynamicCameraAxis(camera.pos.y, target.y, camera.sSize.y, ref velocity.y);
            dynamicFollowVelocities[number] = velocity;
            return true;
        }

        private static float SmoothDynamicCameraAxis(float current, float target, float screenSize, ref float velocity)
        {
            float distance = Mathf.Abs(target - current);
            if (distance > screenSize * 0.35f)
            {
                velocity = 0f; // Discrete screen/warp change, not ordinary follow.
                return target;
            }
            if (distance < 4f && Mathf.Abs(velocity) < 8f) return current;
            return Mathf.SmoothDamp(current, target, ref velocity, 0.14f,
                screenSize * 2f, Mathf.Max(0.001f, Time.deltaTime));
        }

        private float DynamicClampWiden(RoomCamera camera, bool horizontal, bool lower)
        {
            // The level art only covers one prebaked camera screen. Widening
            // Rain World's native displace clamps exposed unrendered black space
            // when a creature was pulled by a vulture or a shortcut. The
            // compositor can pan within that rendered screen, not beyond it.
            return 0f;
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

        private Vector2 DynamicGroupCentroid(int cameraNumber)
        {
            var view = DynamicViewportForCamera(cameraNumber);
            if (view == null) return new Vector2(0.5f, 0.5f);
            int leader = view.sharesImageWith >= 0 ? view.sharesImageWith : cameraNumber;
            Vector2 sum = Vector2.zero;
            float area = 0f;
            foreach (var member in dynamicLayout.viewports)
                if (member.cameraNumber == leader || member.sharesImageWith == leader)
                {
                    sum += member.centroid * member.areaFraction;
                    area += member.areaFraction;
                }
            return area > 0f ? sum / area : view.centroid;
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

        private void MovePauseMenuIntoRegion(Menu.PauseMenu menu, int cameraNumber, Vector2 screenSize)
        {
            if (menu?.container == null || cameraNumber < 0 || cameraNumber >= hudStages.Length ||
                hudStages[cameraNumber] == null) return;
            bool fullyMerged = !alwaysSplit;
            foreach (var view in dynamicLayout.viewports)
                if (!view.ghost && view.splitAmount > 0.05f) { fullyMerged = false; break; }
            if (fullyMerged && globalHudStage != null)
            {
                // Native full-screen pause UI must not be polygon-clipped.
                globalHudStage.AddChild(menu.container);
                menu.container.SetPosition(Vector2.zero);
                return;
            }
            hudStages[cameraNumber].AddChild(menu.container);
            menu.container.SetPosition(camOffsets[cameraNumber]);
            PlacePauseButtonsInRegion(menu, cameraNumber, screenSize);
        }

        private void PlacePauseButtonsInRegion(Menu.PauseMenu menu, int cameraNumber, Vector2 screenSize)
        {
            var view = DynamicViewportForCamera(cameraNumber);
            if (view?.polygon == null || screenSize.x <= 0f || screenSize.y <= 0f) return;
            Vector2 buttonCenter = view.centroid;
            if (RayToPolygonEdge(view.centroid, Vector2.down, view.polygon, out Vector2 bottom))
                buttonCenter = Vector2.Lerp(view.centroid, bottom, 0.3f);
            Vector2 native = new Vector2(screenSize.x * (0.5f + buttonCenter.x - view.centroid.x),
                screenSize.y * (0.5f + buttonCenter.y - view.centroid.y));
            bool narrow = view.areaFraction < 0.32f;
            PlacePauseButton(menu.continueButton ?? menu.confirmYesButton,
                native + (narrow ? new Vector2(0f, 24f) : new Vector2(80f, 0f)));
            PlacePauseButton(menu.exitButton ?? menu.confirmNoButton,
                native + (narrow ? new Vector2(0f, -24f) : new Vector2(-80f, 0f)));
        }

        private static void PlacePauseButton(Menu.SimpleButton button, Vector2 position)
        {
            if (button == null) return;
            button.pos = position;
            button.lastPos = position;
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
                for (int i = 0; i < dynamicLayout.viewports.Length; i++)
                {
                    var viewport = dynamicLayout.viewports[i];
                    if (viewport.rendering && !renderedCameraNumbers.Contains(viewport.cameraNumber))
                        renderedCameraNumbers.Add(viewport.cameraNumber);
                    int baseCamera = baseCameraNumbers[i];
                    if (viewport.imageBlend < 1f && !renderedCameraNumbers.Contains(baseCamera))
                        renderedCameraNumbers.Add(baseCamera);
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
                    bool zoomed = Array.Exists(dynamicLayout.viewports,
                        view => !view.ghost && view.zoom < 0.999f &&
                            (view.cameraNumber == i || baseCameraNumbers[Array.IndexOf(dynamicLayout.viewports, view)] == i));
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
                    bool expected = Array.Exists(dynamicLayout.viewports,
                        v => v.cameraNumber == i && !v.ghost);
                    if (expected && hudExpectedSinceFrames[i] < 0)
                        hudExpectedSinceFrames[i] = Time.frameCount;
                    else if (!expected) hudExpectedSinceFrames[i] = -1;
                    hudCameras[i].enabled = expected;
                }
            if (globalHudCamera != null)
            {
                if (globalHudExpectedSinceFrame < 0) globalHudExpectedSinceFrame = Time.frameCount;
                globalHudCamera.enabled = true;
            }
            string key = $"groups=[{string.Join(",", Array.ConvertAll(dynamicLayout.viewports, v => $"{v.cameraNumber}:{v.sharesImageWith}"))}]|sources=[{string.Join(",", baseCameraNumbers)}]|rendering=[{string.Join(",", renderedCameraNumbers)}]|direct={mergedFullScreen}";
            if (key != lastDynamicLayoutKey)
            {
                Logger.LogInfo($"[CameraLayout] frame={Time.frameCount} {key}");
                lastDynamicLayoutKey = key;
            }
        }

        private void CompositeDynamicLayout(DynamicCompositor compositor)
        {
            if (!dynamicActive || dynamicLayout == null || compositor.texturedMaterial == null) return;
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
                // At a complete merge, render one native image. Re-drawing
                // adjacent cells with a blended material can expose shader seams
                // even though their pairwise split alpha is zero.
                if (!fullyMerged)
                for (int i = 0; i < dynamicLayout.viewports.Length; i++)
                {
                    var viewport = dynamicLayout.viewports[i];
                    if (viewport.ghost) continue;
                    int own = viewport.cameraNumber;
                    int shared = baseCameraNumbers[i];
                    float ownAlpha = viewport.imageBlend;
                    CameraListener ownListener = cameraListeners[own];
                    if (ownListener == null || ownListener.lastPostRenderFrame < Time.frameCount - 2 ||
                        fcameras[own] == null || !fcameras[own].enabled) ownAlpha = 0f;
                    if (rainworldGameObject?.processManager?.currentMainLoop is RainWorldGame activeGame &&
                        CameraByNumber(activeGame, own)?.room == null) ownAlpha = 0f;
                    if (shared == own || ownAlpha >= 1f)
                        DrawDynamicPolygon(compositor.texturedMaterial, ownListener?.renderTexture,
                            viewport.polygon, WorldUvShift(ownScreenPositions[i], viewport, i,
                                shared == own && ownAlpha < 1f), 1f, viewport.zoom);
                    else
                    {
                        DrawDynamicPolygon(compositor.texturedMaterial, cameraListeners[shared]?.renderTexture,
                            viewport.polygon, WorldUvShift(dynamicInputs[i].mergedScreenPos, viewport, i, true), 1f, viewport.zoom);
                        if (ownAlpha > 0f)
                            DrawDynamicPolygon(compositor.texturedMaterial, ownListener?.renderTexture,
                                viewport.polygon, WorldUvShift(ownScreenPositions[i], viewport, i, false), ownAlpha, viewport.zoom);
                    }
                }
                DrawDynamicHud(compositor.texturedMaterial);
                DrawDynamicDividers(compositor.solidMaterial);
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
            }
        }

        private static void DrawDynamicPolygon(Material material, RenderTexture source,
            Vector2[] polygon, Vector2 uvShift, float alpha, float zoom = 1f)
        {
            if (source == null || polygon == null || polygon.Length < 3) return;
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

        private Vector2 WorldUvShift(Vector2 playerSourcePos, SplitLayoutSolver.ViewportState viewport,
            int index, bool sharedSource)
        {
            float zoom = Mathf.Max(0.1f, viewport.zoom);
            Vector2 shift = playerSourcePos - viewport.regionAnchor / zoom;
            Vector2 min = new Vector2(1f, 1f), max = Vector2.zero;
            if (sharedSource)
                for (int j = 0; j < dynamicLayout.viewports.Length; j++)
                    if (baseCameraNumbers[j] == baseCameraNumbers[index])
                        ExpandBounds(dynamicLayout.viewports[j].polygon, ref min, ref max);
            if (!sharedSource) ExpandBounds(viewport.polygon, ref min, ref max);
            float minShiftX = -min.x / zoom, maxShiftX = 1f - max.x / zoom;
            float minShiftY = -min.y / zoom, maxShiftY = 1f - max.y / zoom;
            if (minShiftX <= maxShiftX) shift.x = Mathf.Clamp(shift.x, minShiftX, maxShiftX);
            if (minShiftY <= maxShiftY) shift.y = Mathf.Clamp(shift.y, minShiftY, maxShiftY);
            if (!sharedSource && viewport.splitAmount > 0.05f && viewport.imageBlend > 0.5f &&
                playerSourcePos.x >= 0f && playerSourcePos.x <= 1f &&
                playerSourcePos.y >= 0f && playerSourcePos.y <= 1f)
            {
                Vector2 displayedPlayer = (playerSourcePos - shift) * zoom;
                Vector2 direction = displayedPlayer - viewport.centroid;
                if (RayToPolygonEdge(viewport.centroid, direction, viewport.polygon, out Vector2 edge))
                {
                    bool outside = !PointInsidePolygon(viewport.polygon, displayedPlayer);
                    if (outside || (edge - displayedPlayer).magnitude < 0.045f)
                    {
                        Vector2 inward = viewport.centroid - edge;
                        Vector2 safePoint = edge + inward.normalized * 0.05f;
                        Vector2 safeShift = playerSourcePos - safePoint / zoom;
                        // Allow a narrow clamped-edge strip during discrete
                        // camera-screen entry. It is preferable to hiding the
                        // character under the divider while the native camera
                        // still draws the player near the RT edge.
                        shift.x = Mathf.Clamp(safeShift.x, minShiftX - 0.08f, maxShiftX + 0.08f);
                        shift.y = Mathf.Clamp(safeShift.y, minShiftY - 0.08f, maxShiftY + 0.08f);
                        int number = viewport.cameraNumber;
                        if (outside && number >= 0 && number < lastPlayerSafetyLogFrames.Length &&
                            Time.frameCount - lastPlayerSafetyLogFrames[number] > 120)
                        {
                            Logger.LogWarning($"[CameraLayout] frame={Time.frameCount} player visibility correction cam={number} source={playerSourcePos} display={displayedPlayer} cell={viewport.centroid} zoom={zoom:0.00}");
                            lastPlayerSafetyLogFrames[number] = Time.frameCount;
                        }
                    }
                }
            }
            return shift;
        }

        private static void DrawVertex(Vector2 position, Vector2 uvShift, float zoom)
        {
            GL.TexCoord2(position.x / zoom + uvShift.x, position.y / zoom + uvShift.y);
            GL.Vertex3(position.x, position.y, 0f);
        }

        private void DrawDynamicHud(Material material)
        {
            bool paused = rainworldGameObject?.processManager?.currentMainLoop is RainWorldGame game && game.GamePaused;
            for (int i = 0; i < dynamicLayout.viewports.Length; i++)
            {
                var view = dynamicLayout.viewports[i];
                if (view.ghost) continue;
                // The HUD is sampled at its native scale, translated so the map's
                // conventional screen center lands at the owned cell centroid.
                int sourceCamera = paused && view.sharesImageWith >= 0 ? view.sharesImageWith : view.cameraNumber;
                Vector2 anchor = paused ? DynamicGroupCentroid(view.cameraNumber) : view.centroid;
                DrawDynamicPolygon(material, hudTextures[sourceCamera], view.polygon,
                    new Vector2(0.5f, 0.5f) - anchor, 1f);
            }
        }

        private void DrawDynamicDividers(Material material)
        {
            if (material == null || !material.SetPass(0)) return;
            for (int i = 0; i < dynamicLayout.dividers.Length; i++)
            {
                var edge = dynamicLayout.dividers[i];
                Vector2 along = edge.end - edge.start;
                if (along.sqrMagnitude < 0.00001f) continue;
                Vector2 normal = new Vector2(-along.y, along.x).normalized;
                float width = edge.width / Mathf.Max(1f, Futile.screen.pixelWidth);
                Vector2 pad = normal * width;
                GL.Begin(GL.QUADS);
                GL.Color(new Color(0f, 0f, 0f, edge.alpha));
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
