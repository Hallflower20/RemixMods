using System;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Permissions;
using UnityEngine;
using BepInEx;
using MonoMod.RuntimeDetour;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using BepInEx.Logging;
using MonoMod.RuntimeDetour.HookGen;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[module: UnverifiableCode]
#pragma warning disable CS0618 // Type or member is obsolete
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]

namespace SplitScreenCoop
{
    [BepInPlugin("com.henpemaz.splitscreencoop", "SplitScreen Co-op", "0.2.2")]
    public partial class SplitScreenCoop : BaseUnityPlugin
    {
        public static SplitScreenCoopOptions Options;
        
        public void OnEnable()
        {
            Logger.LogInfo("OnEnable");
            sLogger = Logger;
            On.RainWorld.OnModsInit += OnModsInit;
            On.RainWorld.PostModsInit += RainWorld_PostModsInit;

            try
            {
                // need this early
                On.Futile.Init += Futile_Init; // turn on cam2
                On.Futile.UpdateCameraPosition += Futile_UpdateCameraPosition; // handle custom switcheroos
                On.FScreen.ReinitRenderTexture += FScreen_ReinitRenderTexture; // new tech huh
                On.PersistentData.ctor += PersistentData_ctor; // memory for more cameras
            }
            catch (Exception e)
            {
                Logger.LogError("Failed to initialize");
                Logger.LogError(e);
                throw;
            }
        }

        private void RainWorld_PostModsInit(On.RainWorld.orig_PostModsInit orig, RainWorld self)
        {
            orig(self);
            ReadSettings();
        }

        public enum SplitMode
        {
            NoSplit,
            SplitHorizontal, // top bottom screens
            SplitVertical, // left right screens
            Split4Screen, // 4 players
            Split3Screen // triangle: player 1 above players 2 and 3
        }

        public static SplitMode CurrentSplitMode;
        public static bool alwaysSplit;
        /// <summary>
        /// The "Static" split style: the Dynamic pipeline with cells that never merge.
        /// Every living player owns a fixed region decided by how many are alive (one,
        /// two halves, a half over two quarters, four quarters); a death or a revival
        /// hands the screen out again, nothing else moves it.
        /// </summary>
        public static bool staticStyle;
        /// <summary>
        /// Cells never merge: the "Permanent split" checkbox, or the Static style.
        /// Dual displays keep their own rules (they force alwaysSplit off).
        /// </summary>
        public static bool NeverMerge => alwaysSplit || (staticStyle && !dualDisplays);
        public static bool dualDisplays;
        public static bool stickTogetherEnabled;

        public static Camera[] fcameras = new Camera[4];
        public static CameraListener[] cameraListeners = new CameraListener[4];
        public static List<DisplayExtras> displayExtras = new();
        public static readonly List<int> renderedCameraNumbers = new List<int>();

        public static Camera camera2;
        public static Camera camera3;
        public static Camera camera4;
        public static GameObject cameraHolder2;
        public static GameObject cameraHolder3;
        public static GameObject cameraHolder4;

        public static Vector2[] camOffsets = new Vector2[] { new Vector2(0, 0), new Vector2(32000, 0), new Vector2(0, 32000), new Vector2(32000, 32000) }; // one can dream

        public static int curCamera = -1;
        public static RoomRealizer realizer2;
        public static readonly List<RoomRealizer> additionalRealizers = new();

        public bool init;
        public static ManualLogSource sLogger;
        public static bool selfSufficientCoop;
        public static bool[] cameraZoomed = new bool[] { false, false, false, false };

        public static Rect horizontalSplitScreenPart = new Rect(0f, 0.25f, 1f, 0.5f);
        public static Rect verticalSplitScreenPart = new Rect(0.25f, 0f, 0.5f, 1f);
        public static Rect fourSplitScreenPart = new Rect(0.25f, 0.25f, 0.5f, 0.5f);
        public static Rect[] threeSplitCameraTargetPos = new Rect[] { new Rect(0.25f, 0.5f, 0.5f, 0.5f), new Rect(0f, 0f, 0.5f, 0.5f), new Rect(0.5f, 0f, 0.5f, 0.5f) };
        public static Rect[] fourSplitCameraTargetPos = new Rect[] { new Rect(0f, 0.5f, 0.5f, 0.5f), new Rect(0.5f, 0.5f, 0.5f, 0.5f), new Rect(0f, 0f, 0.5f, 0.5f), new Rect(0.5f, 0f, 0.5f, 0.5f) };
        public static Rect[] horizontalSplitCameraTargetPos = new Rect[] { new Rect(0f, 0.5f, 1f, 0.5f), new Rect(0f, 0f, 1f, 0.5f) };
        public static Rect[] horizontalSplitCameraTargetPosZoomed = new Rect[] { new Rect(0.25f, 0.5f, 0.5f, 0.5f), new Rect(0.25f, 0f, 0.5f, 0.5f) };
        public static Rect[] verticalSplitCameraTargetPos = new Rect[] { new Rect(0f, 0f, 0.5f, 1f), new Rect(0.5f, 0f, 0.5f, 1f) };
        public static Rect[] verticalSplitCameraTargetPosZoomed = new Rect[] { new Rect(0f, 0.25f, 0.5f, 0.5f), new Rect(0.5f, 0.25f, 0.5f, 0.5f) };

        public static RainWorld rainworldGameObject = null;

        public void OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
        {
            try
            {
                rainworldGameObject = GameObject.FindObjectOfType<RainWorld>();

                // Register OptionsInterface
                Options ??= new SplitScreenCoopOptions();
                MachineConnector.SetRegisteredOI("henpemaz_splitscreencoop", Options);

                //CHECK IF SPECIFIC MODS ARE ENABLED
                for (int i = 0; i < ModManager.ActiveMods.Count; i++)
                {
                    if (ModManager.ActiveMods[i].id == "WillowWisp.CoopLeash")
                        stickTogetherEnabled = true;
                }
                

                if (init) return;
                init = true;
                Logger.LogInfo("OnModsInit");
                HookUnityLog(); // mirror Unity exceptions into this log; WriteUnityLog is off in the playtest config

                // splitscreen functionality
                IL.RainWorldGame.ctor += RainWorldGame_ctor1;
                On.RainWorldGame.ctor += RainWorldGame_ctor; // make roomcam2, follow fix

                On.RoomCamera.ctor += RoomCamera_ctor1; // bind cam to camlistener
                On.RainWorldGame.Update += RainWorldGame_Update; // split unsplit
                On.RainWorldGame.GrafUpdate += RainWorldGame_GrafUpdate; // per-frame view panning, hitch logging
                On.RainWorldGame.ShutDownProcess += RainWorldGame_ShutDownProcess; // unbind camlistener

                // fixes in fixes file
                On.RoomCamera.FireUpSinglePlayerHUD += RoomCamera_FireUpSinglePlayerHUD;// displace cam2 map
                On.Menu.PauseMenu.ctor += PauseMenu_ctor;// displace pause menu
                On.Menu.PauseMenu.GrafUpdate += PauseMenu_GrafUpdate;
                On.Menu.PauseMenu.ShutDownProcess += PauseMenu_ShutDownProcess;// kill dupe pause menu
                On.Water.InitiateSprites += Water_InitiateSprites; // move water somewhere near final position
                On.VirtualMicrophone.DrawUpdate += VirtualMicrophone_DrawUpdate; // mic from 2nd cam should not pic up while on same cam
                On.HUD.DialogBox.DrawPos += DialogBox_DrawPos; // center dialog in half screen

                IL.RoomCamera.ctor += RoomCamera_ctor; // create sprite with the right name
                IL.RoomCamera.Update += RoomCamera_Update1; // follow critter, clamp to proper values
                IL.RoomCamera.DrawUpdate += RoomCamera_DrawUpdate1; // clamp to proper values
                IL.ShortcutHandler.Update += ShortcutHandler_Update; // activate room if followed, move cam1 if p moves
                IL.ShortcutHandler.SuckInCreature += ShortcutHandler_SuckInCreature; // the vanilla room loading system is fragile af
                IL.PoleMimicGraphics.InitiateSprites += PoleMimicGraphics_InitiateSprites; // dont resize on re-init

                // creature culling has to take into account cam2
                new Hook(typeof(GraphicsModule).GetMethod("get_ShouldBeCulled", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic),
                    typeof(SplitScreenCoop).GetMethod("get_ShouldBeCulled"), this);

                // co-op in co-op file
                On.OverWorld.WorldLoaded += OverWorld_WorldLoaded; // roomrealizer 2 
                On.RoomRealizer.Update += RoomRealizer_Update; // preserve each realizer's own follow target
                On.RoomRealizer.CanAbstractizeRoom += RoomRealizer_CanAbstractizeRoom; // two checks
                On.RoomRealizer.KillRoom += RoomRealizer_KillRoom; // RemoveNotVisitedRooms skips the checks
                On.RoomRealizer.CurrentPerformanceEstimation += RoomRealizer_CurrentPerformanceEstimation; // one shared budget
                On.AbstractRoom.RealizeRoom += AbstractRoom_RealizeRoom; // hitch diagnostics
                On.AbstractRoom.Abstractize += AbstractRoom_Abstractize; // hitch diagnostics
                On.ShelterDoor.Close += ShelterDoor_Close; // custom close logic
                IL.Player.Update += Player_Update; // custom sleep update
                On.ShelterDoor.DoorClosed += ShelterDoor_DoorClosed; // custom win condition
                IL.ShelterDoor.Update += ShelterDoor_Update; // custom win/starve detection
                new Hook(typeof(RainWorldGame).GetMethod("get_FirstAlivePlayer", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic),
                    typeof(SplitScreenCoop).GetMethod("get_FirstAlivePlayer"), this);
                IL.SaveState.SessionEnded += SaveState_SessionEnded; // food math
                On.SaveState.SessionEnded += SaveState_SessionEndedFriends; // count friends saved by any player
                IL.RainWorldGame.ctor += RainWorldGame_ctor2; // food math
                IL.RainWorldGame.GameOver += RainWorldGame_GameOver; // custom gameover detection
                On.RegionGate.PlayersInZone += RegionGate_PlayersInZone; // joar please TEST your own code
                On.RegionGate.PlayersStandingStill += RegionGate_PlayersStandingStill; // and these two asked corpses too
                On.RegionGate.AllPlayersThroughToOtherSide += RegionGate_AllPlayersThroughToOtherSide;
                On.Creature.FlyAwayFromRoom += Creature_FlyAwayFromRoom; // Player taken by vulture? die quicker please
                HookEndpointManager.Modify(typeof(RegionGate).GetProperty("MeetRequirement").GetGetMethod(), // don't assume player[0].realizedcreature
                    new ILContext.Manipulator(RegionGate_get_MeetRequirement));
                On.ProcessManager.IsGameInMultiplayerContext += ProcessManager_IsGameInMultiplayerContext; // :pensive:

                // jolly why
                On.Menu.SlugcatSelectMenu.StartGame += SlugcatSelectMenu_StartGame;
                On.RoomCamera.ChangeCameraToPlayer += RoomCamera_ChangeCameraToPlayer;
                IL.Player.TriggerCameraSwitch += Player_TriggerCameraSwitch;
                On.Player.TriggerCameraSwitch += Player_TriggerCameraSwitch1;
                On.Player.JollyInputUpdate += Player_JollyInputUpdate;
                On.Player.Die += Player_Die;
                On.StoryGameSession.PlaceKarmaFlowerOnDeathSpot += StoryGameSession_PlaceKarmaFlowerOnDeathSpot;
                On.MoreSlugcats.HypothermiaMeter.Draw += HypothermiaMeter_Draw;
                On.MoreSlugcats.GourmandMeter.Draw += GourmandMeter_Draw;

                On.Player.ctor += Player_ctor;
                IL.HUD.HUD.InitSinglePlayerHud += InitSinglePlayerHud;
                HookEndpointManager.Modify(typeof(JollyCoop.JollyHUD.JollyPlayerSpecificHud).GetProperty("Camera").GetGetMethod(),
                    new ILContext.Manipulator(JollyPlayerSpecificHud_get_Camera));
                IL.JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyOffRoom.Update += JollyOffRoom_Update1;
                On.JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyOffRoom.Update += JollyOffRoom_Update;
                IL.HUD.Map.Draw += HudMap_Draw;
                On.HUD.Map.ctor += HudMap_ctor; // every region change builds a new map on the root stage
                On.HUD.KarmaMeter.Draw += KarmaMeter_Draw;
                On.HUD.FoodMeter.Draw += FoodMeter_Draw;
                On.RoomPreparer.Update += RoomPreparer_Update; // name slow room-loading slices in [FrameHitch]
                On.RainWorld.Update += RainWorld_Update; // [FrameHitch]: everything the game's scripts do in a frame
                On.Futile.LateUpdate += Futile_LateUpdate; // [FrameHitch]: Futile's mesh rebuild
                On.Watcher.Frog.ReleaseGrasp += Frog_ReleaseGrasp; // vanilla null dereference that froze a room every tick
                On.HUD.RainMeter.Draw += RainMeter_Draw;
                On.HUD.TextPrompt.Draw += TextPrompt_Draw;

                // CustomDecal
                On.CustomDecal.ctor += CustomDecal_ctor;
                On.CustomDecal.DrawSprites += CustomDecal_DrawSprites;
                On.CustomDecal.InitiateSprites += CustomDecal_InitiateSprites;
                On.CustomDecal.Update += CustomDecal_Update;
                On.CustomDecal.UpdateMesh += CustomDecal_UpdateMesh;
                On.CustomDecal.UpdateAsset += CustomDecal_UpdateAsset;

                // Snow
                On.MoreSlugcats.SnowSource.PackSnowData += SnowSource_PackSnowData;
                On.MoreSlugcats.SnowSource.Update += SnowSource_Update;
                On.RoofTopView.DustpuffSpawner.DustPuff.ApplyPalette += DustPuff_ApplyPalette;

                // Blizzard
                On.MoreSlugcats.BlizzardGraphics.DrawSprites += BlizzardGraphics_DrawSprites;

                // Shader shenanigans
                // wrapped calls to store shader globals
                On.RoomCamera.DrawUpdate += RoomCamera_DrawUpdate;
                On.RoomCamera.SpriteLeaser.Update += SpriteLeaser_Update; // no DrawSprites for cameras that do not render
                On.RoomCamera.PausedDrawUpdate += RoomCamera_PausedDrawUpdate; // paused draws count as draws
                // Globals computed from one camera in room scope, and effects gated on cameras[0]
                On.MoreSlugcats.BlizzardGraphics.Update += BlizzardGraphics_Update;
                On.MoreSlugcats.DustWave.Update += DustWave_Update;
                On.LightSource.Update += LightSource_Update;
                On.LightBeam.Update += LightBeam_Update;
                On.MoreSlugcats.EnergySwirl.Update += EnergySwirl_Update;
                On.GoldFlakes.Update += GoldFlakes_Update;
                On.GoldFlakes.GoldFlake.Update += GoldFlake_Update;
                On.MoreSlugcats.FairyParticle.Update += FairyParticle_Update;
                On.GenericZeroGSpeck.Update += GenericZeroGSpeck_Update;
                On.AboveCloudsView.Update += AboveCloudsView_Update;
                On.RoomCamera.Update += RoomCamera_Update;
                On.RoomCamera.MoveCamera_int += RoomCamera_MoveCamera_int;
                On.RoomCamera.MoveCamera_Room_int += RoomCamera_MoveCamera_Room_int; // can also colapse to single cam if one of the cams is dead
                On.RoomCamera.UpdateSnowLight += RoomCamera_UpdateSnowLight;
                On.MoreSlugcats.Snow.DrawSprites += Snow_DrawSprites; // visibleSnow is one field for every camera
                // Palette changes reach ApplyPalette from room effects, the day/night
                // cycle and palette-changing rooms, none of which run inside a scoped
                // RoomCamera call. Its fog-amount global would then be recorded against
                // no camera at all, so the views keep whichever value was written last
                // and lose their own palette during fades and merges.
                On.Room.Update += Room_Update; // scope a room's own globals to the cameras showing it
                On.RoomCamera.ApplyPalette += RoomCamera_ApplyPalette;
                On.RoomCamera.ApproximateLightmap += RoomCamera_ApproximateLightmap;
                On.RoomCamera.WarpMoveCameraActual += RoomCamera_WarpMoveCameraActual;
                On.RoomCamera.BlankWarpPointHoldFrame += RoomCamera_BlankWarpPointHoldFrame;
                On.RoomCamera.UpdateGhostMode += RoomCamera_UpdateGhostMode; // Room.cs only updates cameras[0]
                On.RoomCamera.UpdateRotMode += RoomCamera_UpdateRotMode; // and rot colours only ever hit cameras[0]
                On.Room.Loaded += Room_Loaded; // load-time globals go to the room's record
                On.Room.NowViewed += Room_NowViewed; // so do the first camera's entry globals
                On.RoomCamera.ChangeRoom += RoomCamera_ChangeRoom; // and reach a camera when it arrives
                On.HUD.HUD.Update += HUD_Update; // any player's map button reveals the shared meters
                On.ProcessManager.PostSwitchMainProcess += ProcessManager_PostSwitchMainProcess; // menu camera watchdog
                On.RainWorldGame.ResumeProcess += RainWorldGame_ResumeProcess; // back from the fast-travel screen
                On.RainWorldGame.GoToDeathScreen += RainWorldGame_GoToDeathScreen; // diagnostics
                On.HUD.TextPrompt.EnterGameOverMode += TextPrompt_EnterGameOverMode; // diagnostics
                // Cameras showing the same screen copy the decoded level image from
                // each other instead of each decoding the PNG on the main thread.
                IL.RoomCamera.ApplyPositionChange += RoomCamera_ApplyPositionChange;

                // Watcher rendering assumes a single Unity camera. Route its command
                // buffers, textures, masks and ripple state to the owning split camera.
                On.SentientRotSpores.InitiateSprites += SentientRotSpores_InitiateSprites; // one renderer per camera
                On.SentientRotSpores.DrawSprites += SentientRotSpores_DrawSprites; // never GetChildAt(0) on an emptied container
                On.SentientRotSpores.Destroy += SentientRotSpores_Destroy;
                On.Watcher.RippleCameraData.ctor += RippleCameraData_ctor;
                On.Watcher.RippleCameraData.AddCommandBuffer += RippleCameraData_AddCommandBuffer;
                On.Watcher.RippleCameraData.RemoveCommandBuffer += RippleCameraData_RemoveCommandBuffer;
                On.Watcher.RippleCameraData.SetGlobals += RippleCameraData_SetGlobals;
                On.Watcher.LevelTexCombiner.Initialize += LevelTexCombiner_Initialize;
                On.Watcher.LevelTexCombiner.CreateBuffer += LevelTexCombiner_CreateBuffer;
                On.Watcher.LevelTexCombiner.RemovePass += LevelTexCombiner_RemovePass;
                On.Watcher.LevelTexCombiner.RemoveAllBuffers += LevelTexCombiner_RemoveAllBuffers;
                On.Watcher.LevelTexCombiner.SetGlobals += LevelTexCombiner_SetGlobals;
                On.Watcher.LevelTexCombiner.UnSetGlobals += LevelTexCombiner_UnSetGlobals;
                On.Watcher.DynamicLevelElement.AddLevelCombiner += DynamicLevelElement_AddLevelCombiner;
                On.Watcher.DynamicLevelElement.DrawSprites += DynamicLevelElement_DrawSprites;
                On.Watcher.MaskSource.DrawUpdate += MaskSource_DrawUpdate;
                On.Watcher.MaskSource.CreateGameObject += MaskSource_CreateGameObject;
                On.Watcher.FloatingDebris.RippleFlow.Update += RippleFlow_Update;
                On.Watcher.RippleSpider.RippleSpiderSpawner.SpawnRippleTear += RippleSpiderSpawner_SpawnRippleTear;
                On.RoomCamera.ClearMaskMaker += RoomCamera_ClearMaskMaker;

                // unity hooks
                // set shader variables into a dict so it can be set per-camera
                new Hook(typeof(Shader).GetMethod("SetGlobalColor", new Type[] { typeof(int), typeof(Color) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalColor"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalColor", new Type[] { typeof(string), typeof(Color) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalColorString"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalVector", new Type[] { typeof(int), typeof(Vector4) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalVector"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalVector", new Type[] { typeof(string), typeof(Vector4) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalVectorString"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalVectorArray", new Type[] { typeof(int), typeof(Vector4[]) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalVectorArrayArray"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalVectorArray", new Type[] { typeof(string), typeof(Vector4[]) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalVectorArrayArrayString"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalVectorArray", new Type[] { typeof(int), typeof(List<Vector4>) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalVectorArrayList"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalVectorArray", new Type[] { typeof(string), typeof(List<Vector4>) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalVectorArrayListString"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalFloat", new Type[] { typeof(int), typeof(float) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalFloat"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalFloat", new Type[] { typeof(string), typeof(float) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalFloatString"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalInt", new Type[] { typeof(int), typeof(int) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalInt"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalInt", new Type[] { typeof(string), typeof(int) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalIntString"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalTexture", new Type[] { typeof(int), typeof(Texture) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalTexture"), this);
                new Hook(typeof(Shader).GetMethod("SetGlobalTexture", new Type[] { typeof(string), typeof(Texture) }),
                    typeof(SplitScreenCoop).GetMethod("Shader_SetGlobalTextureString"), this);
                new Hook(typeof(Watcher.MaskSource).GetProperty("isVisible", BindingFlags.Public | BindingFlags.Instance).GetGetMethod(),
                    typeof(SplitScreenCoop).GetMethod("MaskSource_get_IsVisible"), this);
                // Every way vanilla moves a mask mesh ends in these three setters (see MaskPlacement).
                foreach (string setter in new[] { "GameObjectPos", "GameObjectRotation", "GameObjectScale" })
                    new Hook(typeof(Watcher.MaskSource).GetProperty("set" + setter, BindingFlags.Public | BindingFlags.Instance).GetSetMethod(),
                        typeof(SplitScreenCoop).GetMethod("MaskSource_set_" + setter), this);

                Logger.LogInfo("OnModsInit done");

            }
            catch (Exception e)
            {
                Logger.LogError(e);
                throw;
            }
            finally
            {
                orig(self);
                selfSufficientCoop = !ModManager.JollyCoop;
                if (selfSufficientCoop)
                {
                    try
                    {
                        self.RequestPlayerSignIn(1, null);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e);
                    }
                }
            }
        }

        public void JollyPlayerSpecificHud_get_Camera(ILContext il)
        {
            try
            {
                var c = new ILCursor(il);
                c.GotoNext(MoveType.Before,
                    i => i.MatchRet()
                    );

                c.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                c.EmitDelegate<Func<RoomCamera, JollyCoop.JollyHUD.JollyPlayerSpecificHud, RoomCamera>>((returnValue, self) =>
                {
                    return self.GetSplitScreenCamera();
                });
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                throw;
            }
        }

        public void ReadSettings()
        {
            dualDisplays = Options.DualDisplays.Value;
            alwaysSplit = Options.AlwaysSplit.Value;
            staticStyle = Options.SplitStyle.Value == "Static";
            adaptiveStyle = Options.SplitStyle.Value == "Adaptive";
            spareQuarterMode = Options.SpareQuarter.Value;
            dynamicStyle = Options.SplitStyle.Value != "Classic" && dynamicPipelineAvailable &&
                !dynamicPipelineFailed;
            // Existing installs may still hold the old eager default in their
            // Remix config. Treat only that exact default as the new baseline.
            dynamicSettings.mergeDistance = Mathf.Approximately(Options.MergeDistance.Value, 280f) ||
                Mathf.Approximately(Options.MergeDistance.Value, 850f) ? 600f : Options.MergeDistance.Value;
            dynamicSettings.blendWidth = Mathf.Approximately(Options.BlendWidth.Value, 200f) ||
                Mathf.Approximately(Options.BlendWidth.Value, 300f) ? 250f : Options.BlendWidth.Value;
            dynamicSettings.minZoom = Options.MinZoom.Value;
            dynamicSettings.zoomExponent = Options.ZoomExponent.Value;
            dynamicSettings.dividerWidth = Options.DividerWidth.Value;
            dynamicSettings.smoothingTime = Options.SmoothingTime.Value;
            cameraRenderingMode = Options.CameraRendering.Value;
            dynamicDebugOverlay = Options.DebugOverlay.Value;
            if (debugLabels != null) debugLabels.enabled = dynamicDebugOverlay;
            hudTakesTurns = Options.HudTakesTurns.Value;
            hudOntoPicture = Options.HudOntoPicture.Value;
            FilterMode zoomFilter = Options.ZoomedFilter.Value == "Point"
                ? FilterMode.Point : FilterMode.Bilinear;
            foreach (CameraListener listener in cameraListeners)
                if (listener?.renderTexture != null)
                    listener.renderTexture.filterMode = dynamicStyle ? zoomFilter :
                        Futile.screen?.renderTexture?.filterMode ?? FilterMode.Point;

            if (dualDisplays && DualDisplaySupported())
            {
                Screen.fullScreen = true;
                InitSecondDisplay();
                alwaysSplit = false;
            }
            else
            {
                // A second display that was active earlier keeps its last texture for
                // ever unless it is pointed back at the main screen.
                MirrorSecondaryDisplays();
                foreach (CameraListener listener in cameraListeners)
                    if (listener?.display != null && listener.display != Display.main)
                        listener.BindToDisplay(Display.main);
                dualDisplays = false;
            }
        }

        /// <summary>
        /// Every secondary display shows Futile's main screen, and any camera bound
        /// to one stops rendering, until SetSplitMode binds a camera to it again:
        /// menus, shutdown, one survivor. Works on the displays themselves, so it
        /// does not matter which camera last owned display 2.
        /// </summary>
        public static void MirrorSecondaryDisplays()
        {
            if (Futile.screen?.renderTexture == null) return;
            foreach (CameraListener listener in cameraListeners)
                if (listener?.display != null && listener.display != Display.main) listener.mirrorMain = true;
            foreach (DisplayExtras extras in displayExtras)
                if (extras != null && extras.display != Display.main) extras.MapToTexture(Futile.screen.renderTexture);
        }

        public static bool DualDisplaySupported()
        {
            return Display.displays.Length >= 2;
        }

        public static void InitSecondDisplay()
        {
            if (!DualDisplaySupported() || cameraListeners[1] == null) return;
            if (!Display.displays[1].active)
                Display.displays[1].Activate();
            cameraListeners[1].BindToDisplay(Display.displays[1]);
            cameraListeners[1].mirrorMain = true;
        }
        
        /// <summary>
        /// Allocate memory for 4 cameras instead of default 2
        /// </summary>
        private void PersistentData_ctor(On.PersistentData.orig_ctor orig, PersistentData self, RainWorld rainWorld)
        {
            Logger.LogInfo("Allocation");
            orig(self, rainWorld);
            int ntex = Mathf.Max(4, self.cameraTextures.GetLength(0));
            Texture2D[,] originalTextures = self.cameraTextures;
            self.cameraTextures = new Texture2D[ntex, 2];
            for (int i = 0; i < ntex; i++)
            {
                for (int j = 0; j < 2; j++)
                {
                    if (i < originalTextures.GetLength(0) && j < originalTextures.GetLength(1))
                    {
                        self.cameraTextures[i, j] = originalTextures[i, j];
                        continue;
                    }
                    self.cameraTextures[i, j] = new Texture2D(1400, 800, TextureFormat.ARGB32, false);
                    self.cameraTextures[i, j].anisoLevel = 0;
                    self.cameraTextures[i, j].filterMode = FilterMode.Point;
                    self.cameraTextures[i, j].wrapMode = TextureWrapMode.Clamp;
                    if (j == 0)
                    {
                        Futile.atlasManager.UnloadAtlas("LevelTexture" + ((i != 0) ? i.ToString() : string.Empty));
                        Futile.atlasManager.LoadAtlasFromTexture("LevelTexture" + ((i != 0) ? i.ToString() : string.Empty), self.cameraTextures[i, j], false);
                    }
                    else
                    {
                        Futile.atlasManager.UnloadAtlas("BackgroundTexture" + ((i != 0) ? i.ToString() : string.Empty));
                        Futile.atlasManager.LoadAtlasFromTexture("BackgroundTexture" + ((i != 0) ? i.ToString() : string.Empty), self.cameraTextures[i, j], false);
                    }
                }
            }
        }
        
        /// <summary>
        /// Init unity camera 2
        /// </summary>
        public void Futile_Init(On.Futile.orig_Init orig, Futile self, FutileParams futileParams)
        {
            orig(self, futileParams);

            Logger.LogInfo("Futile_Init creating camera2");

            cameraHolder2 = new GameObject();
            cameraHolder2.transform.parent = self.gameObject.transform;
            camera2 = cameraHolder2.AddComponent<Camera>();
            self.InitCamera(camera2, 2);

            cameraHolder3 = new GameObject();
            cameraHolder3.transform.parent = self.gameObject.transform;
            camera3 = cameraHolder3.AddComponent<Camera>();
            self.InitCamera(camera3, 3);

            cameraHolder4 = new GameObject();
            cameraHolder4.transform.parent = self.gameObject.transform;
            camera4 = cameraHolder4.AddComponent<Camera>();
            self.InitCamera(camera4, 4);

            fcameras = new Camera[] { self.camera, camera2, camera3, camera4 };

            for (int i = 0; i < fcameras.Length; i++)
            {
                fcameras[i].depth = 100f + i;
                // Watcher depth effects use Camera.main in their constructors. Give
                // every split camera the required depth texture up front instead.
                fcameras[i].depthTextureMode |= DepthTextureMode.Depth;
                var listener = fcameras[i].gameObject.AddComponent<CameraListener>();
                cameraListeners[i] = listener;
                listener.AttachTo(fcameras[i], Display.main);
            }

            camera2.enabled = false;
            camera3.enabled = false;
            camera4.enabled = false;
            InitDynamicCompositor(self);
            self.UpdateCameraPosition();
            Logger.LogInfo("Futile_Init camera2 success");
        }

        /// <summary>
        /// CameraListeners need to keep up
        /// </summary>
        public void FScreen_ReinitRenderTexture(On.FScreen.orig_ReinitRenderTexture orig, FScreen self, int displayWidth)
        {
            orig(self, displayWidth);

            var refreshedDisplays = new HashSet<Display>();
            foreach (var listener in cameraListeners)
                if (listener?.display != null && refreshedDisplays.Add(listener.display))
                    listener.display.Extras().ReinitRenderTexture();
            foreach (var l in cameraListeners)
            {
                l?.ReinitRenderTexture(false);
            }
            ReinitDynamicCompositorTexture();
            ReinitHudTextures();
            Logger.LogInfo($"[CameraRenderTarget] frame={Time.frameCount} rebuilt after FScreen resize; displayWidth={displayWidth}");
        }

        /// <summary>
        /// Apply better offsets in multicam mode, vanilla ones weren't good
        /// </summary>
        public void Futile_UpdateCameraPosition(On.Futile.orig_UpdateCameraPosition orig, Futile self)
        {
            orig(self);

            Logger.LogInfo("Futile_UpdateCameraPosition");
            for (int i = 0; i < fcameras.Length; i++)
            {
                if (fcameras[i] == null) continue;
                var offset = camOffsets[i];
                var x = (Futile.screen.originX - 0.5f) * -Futile.screen.pixelWidth * Futile.displayScaleInverse + Futile.screenPixelOffset.x + offset.x;
                var y = (Futile.screen.originY - 0.5f) * -Futile.screen.pixelHeight * Futile.displayScaleInverse - Futile.screenPixelOffset.y + offset.y;
                fcameras[i].transform.position = new Vector3(x, y, -10f);
                if (hudCameras[i] != null) hudCameras[i].transform.position = new Vector3(x, y, -10f);
            }
            if (globalHudCamera != null) globalHudCamera.transform.position = fcameras[0].transform.position;
            foreach (DisplayExtras extras in displayExtras) extras?.SyncImageRect();
        }


        /// <summary>
        /// Hookpoint for loading in more cameras, right before first room force-loads
        /// </summary>
        public void RainWorldGame_ctor1(ILContext il)
        {
            var c = new ILCursor(il);
            if (c.TryGotoNext(MoveType.Before,
                i => i.MatchLdarg(0),
                i => i.MatchCallOrCallvirt<RainWorldGame>("get_world"),
                i => i.MatchLdloc(0),
                i => i.MatchCallOrCallvirt<World>("ActivateRoom")
                ))
            {
                c.MoveAfterLabels();
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Action<RainWorldGame>>((self) =>
                {
                    Logger.LogInfo("RainWorldGame_ctor1 hookpoint");
                    if (self.IsStorySession && self.session.Players.Count > 1)
                    {
                        Logger.LogInfo("RainWorldGame_ctor1 creating roomcamera2");
                        var cams = self.cameras;
                        int ncams = Mathf.Clamp(self.session.Players.Count, 2, 4);
                        Array.Resize(ref cams, ncams);
                        self.cameras = cams;
                        for(int i = 1; i < ncams; i++)
                        {
                            cams[i] = new RoomCamera(self, i);
                            if (i < self.session.Players.Count)
                                cams[i].followAbstractCreature = self.session.Players[i];
                            else
                                cams[i].followAbstractCreature = self.session.Players[0];
                        }
                        cams[0].followAbstractCreature = self.session.Players[0];
                    }
                    if (selfSufficientCoop && self.IsStorySession) RestorePlayerShelters(self);
                    Logger.LogInfo("RainWorldGame_ctor1 hookpoint done");
                });
            }
            else Logger.LogError(new Exception("Couldn't IL-hook RainWorldGame_ctor1 from SplitScreenMod")); // deffendisve progrmanig
        }

        /// <summary>
        /// Setups, cleanups, move cam2, realizer2, set initial split mode
        /// </summary>
        public void RainWorldGame_ctor(On.RainWorldGame.orig_ctor orig, RainWorldGame self, ProcessManager manager)
        {
            Logger.LogInfo("RainWorldGame_ctor");
            ReadSettings();
            if (selfSufficientCoop)
            {
                Logger.LogInfo("enabling player 2");
                manager.rainWorld.setup.player2 = true;
            }

            realizer2 = null;
            additionalRealizers.Clear();
            realizerBudgetRooms = 1;
            realizerBudgetShrinkTicks = 0;
            ForgetLevelTextures();
            drawPathSafeMode = false;
            pendingKarmaFlowerPosition = null;
            ResetSharedFood();
            CurrentSplitMode = SplitMode.NoSplit;
            ResetCameraDiagnostics();
            inputLoggedAfterStart = false;
            trackedWarpTimer = null;
            ResetDynamicLayout();
            // Before orig: the new cameras record their globals while they are built.
            foreach (CameraListener listener in cameraListeners) listener?.ClearRecordedShaderState();

            orig(self, manager);

            if (self.cameras.Length > 1)
            {
                Logger.LogInfo("camera2 detected");
                for (int i = 0; i < self.cameras.Length; i++)
                {
                    //Logger.LogInfo($"RainWorldGame_ctor cam {self.cameras[i].cameraNumber} to p {(self.session.Players[i].state as PlayerState).playerNumber}");
                    AbstractCreature player = GetPlayerForCamera(self, i);
                    if (player != null)
                        self.cameras[i].followAbstractCreature = player;
                    else
                        self.cameras[i].followAbstractCreature = self.session.Players[0];
                    MoveCameraWorldToStage(self.cameras[i]);
                    MoveCameraHudToOverlay(self.cameras[i]);
                    MovePlayerNamesToWorld(self.cameras[i]);
                }
                SetSplitMode(dynamicStyle && !dualDisplays ? SplitMode.NoSplit :
                    NeverMerge ? ResolveSplitMode(self.session.Players.Count) : SplitMode.NoSplit, self, "game start");
                // SetSplitMode leaves camera 0 rendering straight to the display with
                // the HUD cameras still off. Without a layout solved here the first
                // frames after spawn-in show one un-composited world and no HUD,
                // which reads as a flash before the real layout appears.
                if (dynamicStyle && !dualDisplays)
                    UpdateDynamicLayout(self, GetAliveCameraNumbers(self));
            }
            else
            {
                Logger.LogInfo("no camera2");
                SetSplitMode(SplitMode.NoSplit, self, "single camera game start");
            }
            EnsurePlayerControllers(self);
            LogInputSetups(self, "game start");
            Logger.LogInfo($"[CameraLayout] frame={Time.frameCount} split style={Options?.SplitStyle.Value}; dynamicPipeline={dynamicStyle}; adaptive={adaptiveStyle}; spareQuarter={spareQuarterMode}; static={staticStyle}; neverMerge={NeverMerge}; dualDisplays={dualDisplays}");
            Logger.LogInfo("RainWorldGame_ctor done");
        }

        /// <summary>
        /// adds a listener for render events so shader globals can be set
        /// </summary>
        public void RoomCamera_ctor1(On.RoomCamera.orig_ctor orig, RoomCamera self, RainWorldGame game, int cameraNumber)
        {
            Logger.LogInfo("RoomCamera_ctor1 for camera #" + cameraNumber);
            int previous = curCamera;
            try
            {
                curCamera = cameraNumber;
                orig(self, game, cameraNumber);
            }
            finally
            {
                curCamera = previous;
            }
            RegisterWatcherCamera(self);
            self.splitScreenMode = false; // don't, mine is better
            self.offset = Vector2.zero;
            foreach (var c in self.SpriteLayers) c.SetPosition(camOffsets[self.cameraNumber]);
            MoveCameraWorldToStage(self);
            MoveCameraHudToOverlay(self);
            MovePlayerNamesToWorld(self);
        }

        /// <summary>
        /// Cleanup cameralisteners and realizers
        /// </summary>
        public void RainWorldGame_ShutDownProcess(On.RainWorldGame.orig_ShutDownProcess orig, RainWorldGame self)
        {
            Logger.LogInfo("RainWorldGame_ShutDownProcess cleanups");
            // Nothing in here may throw past this method. ProcessManager calls
            // ShutDownProcess from PreSwitchMainProcess and only clears
            // currentMainLoop afterwards; an exception leaves the half-shut-down
            // game as the current process, updated every frame with its HUD and
            // rooms gone (JollyMeter.Update throws each frame), and the sleep or
            // death screen never arrives. That was the seventh playtest's lockout.
            try
            {
                // Dual displays isolate each camera's world and HUD on its own stage
                // too, so they need the same hand-back to Futile's root stage.
                if ((dynamicActive || dynamicStyle || dualDisplays) && self.cameras?.Length > 1)
                {
                    RestoreClassicWorld(self);
                    RestoreClassicHud(self);
                }
                ResetDynamicLayout();
                frameRenderCamera = -1;
                rotationHoldsCameras = false;
                ApplyRotationFrameCap(1); // menus run at the player's own frame limit
                SetSplitMode(SplitMode.NoSplit, self, "game shutdown");
                if (DualDisplaySupported()) MirrorSecondaryDisplays();
                realizer2 = null;
                additionalRealizers.Clear();
                ForgetLevelTextures();
                ResetSharedFood();
                foreach (CameraListener listener in cameraListeners) listener?.ClearRecordedShaderState();
            }
            catch (Exception error) { LogHookError("RainWorldGame_ShutDownProcess.pre", error); }
            try { orig(self); }
            catch (Exception error) { LogHookError("RainWorldGame_ShutDownProcess.orig", error); }
            try
            {
                DisposeWatcherMasks();
                // Vanilla never removes AbstractSpaceVisualizer's ~500 hidden community
                // labels from Futile.stage (PreSwitchMainProcess only hides them), so
                // the root stage grew by 500 nodes per session (2983 after six) that
                // Futile walked every frame.
                if (self.abstractSpaceVisualizer?.communityLabels != null)
                    foreach (FLabel label in self.abstractSpaceVisualizer.communityLabels) label?.RemoveFromContainer();
                // Leave the cameras the way a menu needs them instead of relying on the
                // watchdog to notice one frame later: SetSplitMode above just enabled
                // whichever camera still had a living player and pointed camera 0 at its
                // split texture.
                RestoreMenuCameras("game shutdown");
            }
            catch (Exception error) { LogHookError("RainWorldGame_ShutDownProcess.post", error); }
        }

        struct RoomTarget : IEquatable<RoomTarget>
        {
            public int room;
            public int camNumber;

            public RoomTarget(int room, int camNumber)
            {
                this.room = room;
                this.camNumber = camNumber;
            }

            public bool Equals(RoomTarget other)
            {
                return room == other.room && camNumber == other.camNumber;
            }

            public override bool Equals(object obj)
            {
                return obj is RoomTarget other && Equals(other);
            }

            public override int GetHashCode()
            {
                return 1000 * room + camNumber;
            }
        }

        /// <summary>
        /// Check for needed split mode changes, update realizers
        /// </summary>
        public void RainWorldGame_Update(On.RainWorldGame.orig_Update orig, RainWorldGame self)
        {
            StartHangWatchdog();
            HangMarker = "EnsureStableCameraAssignments";
            // Before orig: a throw here skipped the whole game tick, every tick it threw.
            if (self.IsStorySession && self.cameras?.Length > 1)
            {
                try { EnsureStableCameraAssignments(self); }
                catch (Exception error) { LogHookError("EnsureStableCameraAssignments", error); }
            }
            HangMarker = "RainWorldGame.Update(orig)";
            long tickStart = phaseWatch.ElapsedTicks;
            orig(self);
            long tickEnd = phaseWatch.ElapsedTicks;
            frameUpdateMs += (tickEnd - tickStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            frameTicks++;
            HangMarker = "RainWorldGame_Update.post";
            // An exception here would leave RawUpdate before GrafUpdate: game logic
            // and audio would continue while nothing is ever drawn again.
            try { RainWorldGame_UpdatePost(self); }
            catch (Exception error) { LogHookError("RainWorldGame_Update.post", error); }
            frameModTickMs += (phaseWatch.ElapsedTicks - tickEnd) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            HangMarker = "idle";
        }

        private void RainWorldGame_UpdatePost(RainWorldGame self)
        {
            if (!self.IsStorySession) return;
            try { WatchWarpTimer(self); }
            catch (Exception error) { LogHookError("WatchWarpTimer", error); }
            if (!inputLoggedAfterStart)
            {
                // The constructor's [Input] line runs before the game is the current
                // process, so its multiplayerContext is always False. Log once more
                // from the first tick, where it means something.
                inputLoggedAfterStart = true;
                LogInputSetups(self, "first tick");
            }
            if (self.cameras.Length > 1 && dynamicStyle && dynamicPipelineFailed)
            {
                Logger.LogWarning($"[CameraLayout] frame={Time.frameCount} restoring Classic after compositor failure");
                RestoreClassicWorld(self);
                RestoreClassicHud(self);
                RestoreClassicPauseMenus(self);
                dynamicStyle = false;
                ResetDynamicLayout();
                SetSplitMode(ResolveSplitMode(GetAliveCameraNumbers(self).Count), self,
                    "compositor failure fallback");
            }
            if (self.GamePaused) return;

            if (self.cameras.Length > 1)
            {
                // Refilled every tick. SetSplitMode below builds a list of its own.
                List<int> aliveCameras = FillAliveCameraNumbers(self, tickAliveCameras);
                if (dynamicStyle && !dualDisplays)
                {
                    UpdateDynamicLayout(self, aliveCameras);
                }
                else
                {
                // SetSplitMode falls back to camera 0 when nobody is alive. Ask for the
                // same here, or the comparison below fails on every tick of a game over
                // and SetSplitMode, with its display rebinding and two log lines, runs
                // forty times a second (84 times in the log of 2026-09-19).
                if (aliveCameras.Count == 0 && self.cameras.Length > 0) aliveCameras.Add(self.cameras[0].cameraNumber);
                // Every tick, dual displays included: loops, where a LINQ chain allocated
                // closures, iterators and two lists per tick.
                bool splitTargets = false, haveTarget = false;
                RoomTarget firstTarget = default;
                foreach (int cameraNumber in aliveCameras)
                {
                    RoomCamera camera = CameraByNumber(self, cameraNumber);
                    if (camera == null) continue;
                    RoomTarget target = camera.room != null
                        ? new RoomTarget(camera.room.abstractRoom.index, camera.currentCameraPosition)
                        : new RoomTarget();
                    if (!haveTarget) { firstTarget = target; haveTarget = true; }
                    else if (!target.Equals(firstTarget)) { splitTargets = true; break; }
                }
                SplitMode desiredMode = !dualDisplays && aliveCameras.Count > 1 && (NeverMerge || splitTargets)
                    ? ResolveSplitMode(aliveCameras.Count)
                    : SplitMode.NoSplit;
                // SetSplitMode renders the first two living cameras on dual displays, the
                // first one unsplit, all of them split.
                int wanted = dualDisplays ? Math.Min(2, aliveCameras.Count)
                    : desiredMode == SplitMode.NoSplit ? Math.Min(1, aliveCameras.Count) : aliveCameras.Count;
                bool sameCameras = renderedCameraNumbers.Count == wanted;
                for (int i = 0; sameCameras && i < wanted; i++) sameCameras = renderedCameraNumbers[i] == aliveCameras[i];
                if (desiredMode != CurrentSplitMode || !sameCameras)
                {
                    string reason = desiredMode != CurrentSplitMode
                        ? (desiredMode == SplitMode.NoSplit ? "camera targets converged or one survivor" : "camera targets diverged or survivor count changed")
                        : "active survivor cameras changed";
                    SetSplitMode(desiredMode, self, reason);
                }
                }

                if (CurrentSplitMode != SplitMode.NoSplit && self.cameras[0].room != null && self.cameras[0].room.abstractRoom.name == "SB_L01") // honestly jolly
                {
                    ConsiderColapsing(self, false);
                }
            }

            if(self.Players.Count > 1)
            {
                UpdateRealizerBudget(self);
                if (additionalRealizers.Count > 0)
                {
                    HangMarker = "additional RoomRealizer.Update";
                    // By index, not a copy per tick; the list only changes on a world load.
                    for (int r = 0; r < additionalRealizers.Count; r++)
                    {
                        RoomRealizer realizer = additionalRealizers[r];
                        if (realizer?.world == self.world) realizer.Update();
                    }
                }
                else
                {
                    if (self.roomRealizer?.followCreature != null) MakeRealizer2(self);
                }
            }

            if (selfSufficientCoop)
            {
                HangMarker = "CoopUpdate";
                CoopUpdate(self);
            }

            HangMarker = "MonitorCameraHealth";
            MonitorCameraHealth(self);
        }

        /// <summary>
        /// Switches between split modes, only call from outside of camera-related code? untested if that actually breaks anything
        /// </summary>
        public void SetSplitMode(SplitMode split, RainWorldGame game, string reason = null)
        {
            dynamicActive = false;
            if (dynamicCompositorCamera != null) dynamicCompositorCamera.enabled = false;
            foreach (var listener in cameraListeners)
                if (listener != null) listener.dynamicCompositing = false;
            SplitMode previousMode = CurrentSplitMode;
            List<int> aliveCameras = GetAliveCameraNumbers(game);
            if (aliveCameras.Count == 0 && game?.cameras?.Length > 0) aliveCameras.Add(game.cameras[0].cameraNumber);
            if (game.cameras.Length > 1)
            {
                CurrentSplitMode = split == SplitMode.NoSplit || aliveCameras.Count <= 1
                    ? SplitMode.NoSplit
                    : ResolveSplitMode(aliveCameras.Count);
                renderedCameraNumbers.Clear();
                renderedCameraNumbers.AddRange(dualDisplays
                    ? aliveCameras.Take(2)
                    : CurrentSplitMode == SplitMode.NoSplit ? aliveCameras.Take(1) : aliveCameras);
                Logger.LogInfo($"[CameraMode] frame={Time.frameCount} {previousMode} -> {CurrentSplitMode}; reason={reason ?? "unspecified"}; players={game.session.Players.Count}; aliveCameras=[{string.Join(",", aliveCameras)}]; renderedCameras=[{string.Join(",", renderedCameraNumbers)}]; cameras={game.cameras.Length}; dualDisplay={dualDisplays}");

                for (int i = 0; i < fcameras.Length; i++)
                {
                    if (fcameras[i] != null) fcameras[i].enabled = false;
                    cameraZoomed[i] = false;
                }

                if (dualDisplays)
                {
                    // With one survivor display 2 has no camera; show the main screen
                    // there instead of the last frame its previous camera drew.
                    if (renderedCameraNumbers.Count < 2) MirrorSecondaryDisplays();
                    for (int slot = 0; slot < renderedCameraNumbers.Count; slot++)
                    {
                        int cameraNumber = renderedCameraNumbers[slot];
                        Display targetDisplay = slot == 0 ? Display.main : Display.displays[1];
                        cameraListeners[cameraNumber].BindToDisplay(targetDisplay);
                        cameraListeners[cameraNumber].direct = true;
                        cameraListeners[cameraNumber].mirrorMain = false;
                        fcameras[cameraNumber].enabled = true;
                    }
                }
                else
                {
                    switch (CurrentSplitMode)
                    {
                        case SplitMode.NoSplit:
                            Logger.LogInfo("NoSplit");
                            if (renderedCameraNumbers.Count > 0)
                            {
                                int cameraNumber = renderedCameraNumbers[0];
                                cameraListeners[cameraNumber].BindToDisplay(Display.main);
                                cameraListeners[cameraNumber].direct = true;
                                fcameras[cameraNumber].enabled = true;
                            }
                            break;
                        case SplitMode.SplitHorizontal:
                            Logger.LogInfo("SplitHorizontal");
                            for (int slot = 0; slot < renderedCameraNumbers.Count; slot++)
                            {
                                int cameraNumber = renderedCameraNumbers[slot];
                                cameraListeners[cameraNumber].BindToDisplay(Display.main);
                                cameraListeners[cameraNumber].direct = false;
                                cameraListeners[cameraNumber].SetMap(InsetForSeparator(horizontalSplitScreenPart), InsetForSeparator(horizontalSplitCameraTargetPos[slot]));
                                fcameras[cameraNumber].enabled = true;
                            }
                            break;
                        case SplitMode.SplitVertical:
                            Logger.LogInfo("SplitVertical");
                            for (int slot = 0; slot < renderedCameraNumbers.Count; slot++)
                            {
                                int cameraNumber = renderedCameraNumbers[slot];
                                cameraListeners[cameraNumber].BindToDisplay(Display.main);
                                cameraListeners[cameraNumber].direct = false;
                                cameraListeners[cameraNumber].SetMap(InsetForSeparator(verticalSplitScreenPart), InsetForSeparator(verticalSplitCameraTargetPos[slot]));
                                fcameras[cameraNumber].enabled = true;
                            }
                            break;
                        case SplitMode.Split3Screen:
                            Logger.LogInfo("Split3Screen");
                            for (int slot = 0; slot < renderedCameraNumbers.Count; slot++)
                            {
                                int cameraNumber = renderedCameraNumbers[slot];
                                cameraListeners[cameraNumber].BindToDisplay(Display.main);
                                cameraListeners[cameraNumber].direct = false;
                                cameraListeners[cameraNumber].SetMap(InsetForSeparator(fourSplitScreenPart), InsetForSeparator(threeSplitCameraTargetPos[slot]));
                                fcameras[cameraNumber].enabled = true;
                            }
                            break;
                        case SplitMode.Split4Screen:
                            Logger.LogInfo("Split4Screen");
                            for (int slot = 0; slot < renderedCameraNumbers.Count; slot++)
                            {
                                int cameraNumber = renderedCameraNumbers[slot];
                                cameraListeners[cameraNumber].BindToDisplay(Display.main);
                                cameraListeners[cameraNumber].direct = false;
                                cameraListeners[cameraNumber].SetMap(InsetForSeparator(fourSplitScreenPart), InsetForSeparator(fourSplitCameraTargetPos[slot]));
                                fcameras[cameraNumber].enabled = true;
                            }
                            break;
                        default:
                            break;
                    }
                }
            }
            else
            {
                renderedCameraNumbers.Clear();
                renderedCameraNumbers.Add(0);
                Logger.LogInfo("single cam NoSplit");
                for (int i = 1; i < fcameras.Length; i++)
                {
                    fcameras[i].enabled = false;
                }
                cameraListeners[0].direct = true;
            }
            // World-layer isolation belongs to ApplyDynamicCameraRendering, which runs
            // every tick while a session is live. Doing it here too re-isolated camera
            // 0 during shutdown, right after ResetDynamicLayout had restored its mask,
            // and the main menu then rendered as a black screen.
            RefreshActiveCameraRendering(game, reason ?? "split mode update");
        }

        private List<int> GetAliveCameraNumbers(RainWorldGame game)
        {
            return FillAliveCameraNumbers(game, new List<int>(4));
        }

        /// <summary>The list the tick hook fills every tick, instead of a new one per tick.</summary>
        private readonly List<int> tickAliveCameras = new List<int>(4);

        /// <summary>
        /// Living players that own a camera, by player number, each once, into
        /// <paramref name="alive"/> (cleared first). This runs every tick and more; it was
        /// a seven-stage LINQ chain with a closure per player.
        /// </summary>
        private static List<int> FillAliveCameraNumbers(RainWorldGame game, List<int> alive)
        {
            alive.Clear();
            if (game?.session?.Players == null || game.cameras == null) return alive;
            for (int i = 0; i < game.session.Players.Count; i++)
            {
                AbstractCreature player = game.session.Players[i];
                if (CreatureIsDead(player)) continue;
                int number = (player.state as PlayerState)?.playerNumber ?? i;
                if (alive.Contains(number)) continue;
                foreach (RoomCamera camera in game.cameras)
                    if (camera != null && camera.cameraNumber == number) { alive.Add(number); break; }
            }
            alive.Sort();
            return alive;
        }

        /// <summary>
        /// null or dead or deleted creature
        /// </summary>
        public bool IsCreatureDead(AbstractCreature critter) => CreatureIsDead(critter);

        /// <summary>The player camera <paramref name="cameraNumber"/> belongs to is dead (with a camera per player).</summary>
        internal static bool CameraOwnerDead(RainWorldGame game, int cameraNumber)
        {
            AbstractCreature owner = GetPlayerForCamera(game, cameraNumber);
            return owner != null && CreatureIsDead(owner);
        }

        /// <summary>IsCreatureDead for static code.</summary>
        internal static bool CreatureIsDead(AbstractCreature critter)
        {
            if (critter?.state == null || critter.state.dead) return true;
            if (critter.state is PlayerState playerState && playerState.permaDead) return true;
            // A living player travelling through a pipe into an unrealized room is
            // abstracted: its realized body is slated for deletion and rebuilt when
            // the room loads. Counting that as death dropped the player's camera and,
            // with the other player held or dead, ended the game with someone alive.
            // Death is the creature state, nothing else.
            return false;
        }

        /// <summary>
        /// consider changing camera targets if someones dead or deleted
        /// </summary>
        public void ConsiderColapsing(RainWorldGame game, bool regionSwitch)
        {
            if (game.cameras.Length > 1)
            {
                if (game.cameras.Length >= game.Players.Count)
                {
                    EnsureStableCameraAssignments(game);
                    return;
                }
                if (!regionSwitch && (game.Players.Count == game.cameras.Length && alwaysSplit)) return; // I guess
                foreach (var cam in game.cameras)
                {
                    // if following dead critter, switch!
                    if (IsCreatureDead(cam.followAbstractCreature))
                    {
                        if (cam.game.Players.ToArray().Reverse().FirstOrDefault(cr => !IsCreatureDead(cr))?.realizedCreature is Player p)
                            AssignCameraToPlayer(cam, p);
                        else if (cam.game.cameras.FirstOrDefault(c => !IsCreatureDead(c.followAbstractCreature))?.followAbstractCreature?.realizedCreature is Player pp)
                            AssignCameraToPlayer(cam, pp);
                    }
                }
            }
        }

        private static SplitMode ResolveSplitMode(int playerCount)
        {
            if (playerCount >= 4) return SplitMode.Split4Screen;
            if (playerCount == 3) return SplitMode.Split3Screen;
            if (playerCount == 2) return SplitMode.SplitVertical;
            return SplitMode.NoSplit;
        }

        private static bool IsGridSplit(SplitMode mode)
        {
            return mode == SplitMode.Split3Screen || mode == SplitMode.Split4Screen;
        }

        private static float SmoothCameraAxis(float current, float target, float screenSize)
        {
            if (Mathf.Abs(target - current) > screenSize * 0.5f) return target;
            return Mathf.Lerp(current, target, 0.55f);
        }

        /// <summary>Each player has a camera of their own: the case in which vanilla's "make cameras[0] follow player X" is wrong.</summary>
        internal static bool EachPlayerHasOwnCamera(RainWorldGame game)
        {
            return game?.cameras != null && game.Players != null &&
                game.cameras.Length > 1 && game.cameras.Length >= game.Players.Count;
        }

        private static AbstractCreature GetPlayerForCamera(RainWorldGame game, int cameraNumber)
        {
            // Called per player per frame (Adaptive framing and map) and per camera per
            // tick: a plain loop, the LINQ version allocated a closure every call.
            List<AbstractCreature> players = game?.session?.Players;
            if (players == null) return null;
            for (int i = 0; i < players.Count; i++)
                if ((players[i]?.state as PlayerState)?.playerNumber == cameraNumber) return players[i];
            return cameraNumber >= 0 && cameraNumber < players.Count ? players[cameraNumber] : null;
        }

        private void EnsureStableCameraAssignments(RainWorldGame game)
        {
            if (game?.cameras == null || game.cameras.Length < game.Players.Count) return;
            foreach (RoomCamera camera in game.cameras)
            {
                AbstractCreature player = GetPlayerForCamera(game, camera.cameraNumber);
                if (player == null) continue;
                // A dead player's view fades out of the layout within a second, but
                // their corpse keeps travelling as it is dragged or carried through
                // shortcuts. Chasing it re-enters RoomCamera.MoveCamera, which loads
                // the next room image synchronously and stalls every other view.
                if (IsCreatureDead(player))
                {
                    if (camera.cameraNumber >= 0 && camera.cameraNumber < roomMismatchSinceFrames.Length)
                        roomMismatchSinceFrames[camera.cameraNumber] = -1;
                    continue;
                }
                // HunterStart and HideHudAndFollowNoone cutscenes set the camera to follow
                // nobody every tick; reassigning it every tick only fought vanilla (and
                // logged each time). The camera stays put; the cutscene ends by itself.
                if (camera.followAbstractCreature == null && camera.InCutscene &&
                    (camera.cutsceneType == RoomCamera.CameraCutsceneType.HunterStart ||
                     camera.cutsceneType == RoomCamera.CameraCutsceneType.HideHudAndFollowNoone))
                    continue;
                if (player.realizedCreature is Player realized && camera.followAbstractCreature != player)
                    AssignCameraToPlayer(camera, realized);
                else
                {
                    camera.followAbstractCreature = player;
                    ReconcileCameraRoom(camera, player, false, "stable assignment check");
                }
            }
        }

        private static Rect InsetForSeparator(Rect rect)
        {
            float x = Futile.screen == null ? 0.001f : 1f / Mathf.Max(1f, Futile.screen.pixelWidth);
            float y = Futile.screen == null ? 0.001f : 1f / Mathf.Max(1f, Futile.screen.pixelHeight);
            return new Rect(rect.x + x, rect.y + y, Mathf.Max(0f, rect.width - 2f * x), Mathf.Max(0f, rect.height - 2f * y));
        }

        /// <summary>
        /// Update camera.follow but also properly switch current room and hud owner
        /// </summary>
        public void AssignCameraToPlayer(RoomCamera camera, Player player)
        {
            Logger.LogInfo($"AssignCameraToPlayer cam {camera.cameraNumber} to p {player.playerState.playerNumber}");
            //Logger.LogInfo(Environment.StackTrace);
            camera.followAbstractCreature = player.abstractCreature;
            ReconcileCameraRoom(camera, player.abstractCreature, true, "player assignment");
            if (camera.hud != null) camera.hud.owner = player;
        }

        /// <summary>
        /// Move HUD onscreen for current split mode
        /// </summary>
        public void OffsetHud(RoomCamera self)
        {
            self.hud?.map?.inFrontContainer?.SetPosition(camOffsets[self.cameraNumber]); // map icons
            self.hud?.warpMap?.inFrontContainer?.SetPosition(camOffsets[self.cameraNumber]); // Watcher warp map icons
        }

        /// <summary>
        /// Store screen bound camera to an extended field when constructor is called
        /// </summary>
        public void InitSinglePlayerHud(ILContext il)
        {
            try
            {
                var c = new ILCursor(il);
                int loc = 0;
                c.GotoNext(MoveType.After,
                    i => i.MatchNewobj<JollyCoop.JollyHUD.JollyPlayerSpecificHud>(),
                    i => i.MatchStloc(out loc)
                    );

                c.Emit(OpCodes.Ldarg_1);
                c.Emit(OpCodes.Ldloc, loc);
                c.EmitDelegate<Action<RoomCamera, JollyCoop.JollyHUD.JollyPlayerSpecificHud>>((cam, self) =>
                {
                    self.SetSplitScreenCamera(cam);
                    return;
                });
                
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                throw;
            }
        }

        public void FoodMeter_Draw(On.HUD.FoodMeter.orig_Draw orig, HUD.FoodMeter self, float timeStacker)
        {
            if (dynamicStyle && !dualDisplays) { orig(self, timeStacker); return; }
            var oldPos = self.pos;
            var oldLastPos = self.lastPos;
            RoomCamera cam = GetHUDPartCurrentCamera(self);
            if (cam != null)
            {
                var offset = GetGlobalHudOffset(cam);
                self.pos += offset;
                self.lastPos += offset;
            }
            orig(self, timeStacker);
            if (cam != null)
            {
                self.pos = oldPos;
                self.lastPos = oldLastPos;
            }
        }

        public void KarmaMeter_Draw(On.HUD.KarmaMeter.orig_Draw orig, HUD.KarmaMeter self, float timeStacker)
        {
            if (dynamicStyle && !dualDisplays) { orig(self, timeStacker); return; }
            var oldPos = self.pos;
            var oldLastPos = self.lastPos;
            RoomCamera cam = GetHUDPartCurrentCamera(self);
            if (cam != null)
            {
                var offset = GetGlobalHudOffset(cam);
                self.pos += offset;
                self.lastPos += offset;
            }
            orig(self, timeStacker);
            if (cam != null)
            {
                self.pos = oldPos;
                self.lastPos = oldLastPos;
            }
        }

        // Reused by the meter hooks below: they ran with two new lists per meter per camera per frame.
        private readonly List<Vector2> hudScratchA = new List<Vector2>(), hudScratchB = new List<Vector2>();

        public void RainMeter_Draw(On.HUD.RainMeter.orig_Draw orig, HUD.RainMeter self, float timeStacker)
        {
            if (dynamicStyle && !dualDisplays) { orig(self, timeStacker); return; }
            RoomCamera cam = GetHUDPartCurrentCamera(self);
            Vector2 offset = cam != null ? GetGlobalHudOffset(cam) : Vector2.zero;
            // Dual displays and a single camera on screen have nothing to move.
            if (offset == Vector2.zero) { orig(self, timeStacker); return; }
            hudScratchA.Clear(); hudScratchB.Clear();
            for (int i = 0; i < self.circles.Length; i++)
            {
                hudScratchA.Add(self.circles[i].pos);
                hudScratchB.Add(self.circles[i].lastPos);
                self.circles[i].pos += offset;
                self.circles[i].lastPos += offset;
            }
            try { orig(self, timeStacker); }
            finally
            {
                for (int j = 0; j < self.circles.Length && j < hudScratchA.Count; j++)
                {
                    self.circles[j].pos = hudScratchA[j];
                    self.circles[j].lastPos = hudScratchB[j];
                }
            }
        }

        public void TextPrompt_Draw(On.HUD.TextPrompt.orig_Draw orig, HUD.TextPrompt self, float timeStacker)
        {
            orig(self, timeStacker);
            if (dynamicStyle && !dualDisplays) return;
            RoomCamera cam = GetHUDPartCurrentCamera(self);
            if (cam != null)
            {
                var offset = GetGlobalHudOffset(cam);
                // text
                if (self.label != null)
                {
                    self.label.x += offset.x;
                    self.label.y += offset.y;
                }
                if (self.musicSprite != null)
                {
                    self.musicSprite.x += offset.x;
                    self.musicSprite.y += offset.y;
                }
                // background overlay that appears from top and bottom of the screen
                if (self.sprites != null)
                {
                    if (self.sprites[0] != null)
                        self.sprites[0].y -= offset.y;
                    if (self.sprites.Length > 1 && self.sprites[1] != null)
                        self.sprites[1].y += offset.y;
                }
                for (int j = 0; j < self.symbols.Count; j++)
                {
                    var symbol = self.symbols[j];
                    if (symbol.symbolSprite != null)
                    {
                        self.symbols[j].symbolSprite.x += offset.x;
                        self.symbols[j].symbolSprite.y += offset.y;
                    }
                    if (symbol.shadowSprite1 != null)
                    {
                        self.symbols[j].shadowSprite1.x += offset.x;
                        self.symbols[j].shadowSprite1.y += offset.y;
                    }
                    if (symbol.shadowSprite2 != null)
                    {
                        self.symbols[j].shadowSprite2.x += offset.x;
                        self.symbols[j].shadowSprite2.y += offset.y;
                    }
                }
            }
        }

        /// <summary>
        /// The warmth meter. Vanilla places its ten circles INSIDE Draw
        /// (circles[i].pos = pos + ...), unlike RainMeter, which places them in
        /// Update. This hook used to shift the circles, let Draw overwrite pos, then
        /// "restore" every circle's pos to what it held before Draw. HUDCircle.Update
        /// copies pos into lastPos on the next tick, so lastPos never left the
        /// constructor's (-100, -100), and a circle is drawn at
        /// Lerp(lastPos, pos, timeStacker): every circle swept from beyond the bottom
        /// left corner of the screen to its place, forty times a second, whenever the
        /// hook took that path, i.e. in Classic split and on dual displays, even with
        /// an offset of zero ("the warmth indicator is bugging around the bottom
        /// left", 2026-09-19). Move the meter, which is what Draw reads, and leave
        /// the circles alone.
        /// </summary>
        public void HypothermiaMeter_Draw(On.MoreSlugcats.HypothermiaMeter.orig_Draw orig, MoreSlugcats.HypothermiaMeter self, float timeStacker)
        {
            Vector2 offset = HypothermiaMeterOffset(self);
            if (offset == Vector2.zero) { orig(self, timeStacker); return; }
            Vector2 pos = self.pos, lastPos = self.lastPos;
            self.pos += offset;
            self.lastPos += offset;
            try { orig(self, timeStacker); }
            finally
            {
                self.pos = pos;
                self.lastPos = lastPos;
            }
        }

        private Vector2 HypothermiaMeterOffset(MoreSlugcats.HypothermiaMeter self)
        {
            RoomCamera cam = CameraOfHud(self?.hud);
            if (cam == null) return Vector2.zero;
            if (!(dynamicStyle && !dualDisplays)) return GetGlobalHudOffset(cam);
            if (adaptiveStyle) return AdaptiveHudOffset(cam);
            // Dynamic and Static: warmth is a reading per player, so unlike food, karma
            // and rain it stays in its own view's HUD. That HUD texture is drawn into
            // the view's cell shifted by DynamicHudShift (a point h of the texture lands
            // at h - shift on screen), which leaves the HUD's bottom left corner, where
            // this meter lives, outside every cell smaller than the screen: the meter
            // was simply not visible. Put it in the corner of its own cell.
            if (!dynamicActive) return Vector2.zero;
            var view = DynamicViewportForCamera(cam.cameraNumber);
            if (view?.polygon == null || view.ghost) return Vector2.zero;
            Vector2 min = new Vector2(1f, 1f), max = Vector2.zero;
            ExpandBounds(view.polygon, ref min, ref max);
            if (min.x > max.x || min.y > max.y) return Vector2.zero;
            Vector2 shift = DynamicHudShift(view);
            return new Vector2((min.x + shift.x) * cam.sSize.x, (min.y + shift.y) * cam.sSize.y);
        }

        /// <summary>The camera a HUD belongs to, whoever is drawing it.</summary>
        private static RoomCamera CameraOfHud(HUD.HUD hud)
        {
            RainWorldGame game = hud?.rainWorld?.processManager?.currentMainLoop as RainWorldGame;
            if (game?.cameras == null) return null;
            foreach (RoomCamera camera in game.cameras)
                if (camera != null && camera.hud == hud) return camera;
            return null;
        }

        public void GourmandMeter_Draw(On.MoreSlugcats.GourmandMeter.orig_Draw orig, MoreSlugcats.GourmandMeter self, float timeStacker)
        {
            if (dynamicStyle && !dualDisplays) { orig(self, timeStacker); return; }
            RoomCamera cam = GetHUDPartCurrentCamera(self);
            Vector2 offset = cam != null ? GetGlobalHudOffset(cam) : Vector2.zero;
            if (offset == Vector2.zero) { orig(self, timeStacker); return; }
            hudScratchA.Clear(); hudScratchB.Clear();
            for (int i = 0; i < self.CollectedSymbols.Count; i++)
            {
                hudScratchA.Add(self.CollectedSymbols[i].Pos);
                hudScratchB.Add(self.CollectedSymbols[i].GoalPos);
                self.CollectedSymbols[i].Pos += offset;
                self.CollectedSymbols[i].GoalPos += offset;
            }
            try { orig(self, timeStacker); }
            finally
            {
                for (int j = 0; j < self.CollectedSymbols.Count && j < hudScratchA.Count; j++)
                {
                    self.CollectedSymbols[j].Pos = hudScratchA[j];
                    self.CollectedSymbols[j].GoalPos = hudScratchB[j];
                }
            }
        }

        public RoomCamera GetHUDPartCurrentCamera(HUD.HudPart hudPart)
        {
            // While paused RainWorldGame.GrafUpdate draws each HUD itself, outside any
            // camera's DrawUpdate; the meters lost their split offset for the length of
            // the pause. A HUD knows its camera whoever draws it.
            if (curCamera == -1)
                return CameraOfHud(hudPart?.hud);
            var loop = hudPart.hud.rainWorld.processManager.currentMainLoop;
            if (loop is RainWorldGame)
                return ((RainWorldGame)loop).cameras[curCamera];
            return null;
        }

        public void JollyOffRoom_Update1(ILContext il)
        {
            try
            {
                var c = new ILCursor(il);
                c.GotoNext(MoveType.After,
                    i => i.MatchLdfld<JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyPointer>("screenEdge")
                    );
                c.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                c.EmitDelegate<Func<int, JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyOffRoom, int>>((returnValue, self) =>
                {
                    if (!cameraZoomed[self.jollyHud.Camera.cameraNumber])
                        return returnValue + (int)GetRelativeSplitScreenOffset(self.jollyHud.Camera).x;
                    else
                        return returnValue;
                });
                c.Index++;

                c.GotoNext(MoveType.After,
                    i => i.MatchLdfld<JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyPointer>("screenEdge")
                    );
                c.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                c.EmitDelegate<Func<int, JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyOffRoom, int>>((returnValue, self) =>
                {
                    if (!cameraZoomed[self.jollyHud.Camera.cameraNumber])
                        return returnValue + (int)GetRelativeSplitScreenOffset(self.jollyHud.Camera).x;
                    else
                        return returnValue;
                });
                c.Index++;

                c.GotoNext(MoveType.After,
                    i => i.MatchLdfld<JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyPointer>("screenEdge")
                    );
                c.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                c.EmitDelegate<Func<int, JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyOffRoom, int>>((returnValue, self) =>
                {
                    if (!cameraZoomed[self.jollyHud.Camera.cameraNumber])
                        return returnValue + (int)GetRelativeSplitScreenOffset(self.jollyHud.Camera).y;
                    else
                        return returnValue;
                });
                c.Index++;

                c.GotoNext(MoveType.After,
                    i => i.MatchLdfld<JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyPointer>("screenEdge")
                    );
                c.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                c.EmitDelegate<Func<int, JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyOffRoom, int>>((returnValue, self) =>
                {
                    if (!cameraZoomed[self.jollyHud.Camera.cameraNumber])
                        return returnValue + (int)GetRelativeSplitScreenOffset(self.jollyHud.Camera).y;
                    else
                        return returnValue;
                });

                // Allow icons to appear when other slugcats are in the same room, but not on screen
                c.GotoNext(MoveType.After,
                    i => i.MatchCallvirt<JollyCoop.JollyHUD.JollyPlayerSpecificHud>("get_PlayerRoomBeingViewed")
                    );
                c.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_0);
                c.EmitDelegate<Func<bool, JollyCoop.JollyHUD.JollyPlayerSpecificHud.JollyOffRoom, bool>>((returnValue, self) =>
                {
                    if (self.jollyHud.Camera == null || self.jollyHud.RealizedPlayer == null || self.jollyHud.RealizedPlayer.abstractCreature == null)
                    {
                        return true;
                    }
                    // if result == true - hide slugcat icon
                    var followedCreature = self.jollyHud.Camera.followAbstractCreature;
                    if (followedCreature == self.jollyHud.RealizedPlayer.abstractCreature)
                        return returnValue;
                    if (returnValue)
                    {
                        if (dynamicStyle && !dualDisplays && !adaptiveStyle && TryProjectJollyPlayer(self, out Vector2 projected))
                            return PointInsideDynamicRegion(self.jollyHud.Camera.cameraNumber, projected);
                        if (followedCreature == null || followedCreature.realizedCreature == null || followedCreature.Room == null)
                        {
                            return true;
                        }
                        var followedPos = followedCreature.world.RoomToWorldPos(followedCreature.realizedCreature.mainBodyChunk.pos, followedCreature.Room.index);
                        var distanceX = Math.Abs(self.playerPos.x - followedPos.x);
                        var distanceY = Math.Abs(self.playerPos.y - followedPos.y);

                        var magicNumber = 2.6f; // otherwise slugcat icons are sometimes placed weirdly
                        if (cameraZoomed[self.jollyHud.Camera.cameraNumber])
                        {
                            if (distanceX > (self.jollyHud.Camera.sSize.x) || distanceY > (self.jollyHud.Camera.sSize.y))
                            {
                                returnValue = false;
                            }
                        }
                        else if (distanceX > Math.Abs(self.jollyHud.Camera.sSize.x - magicNumber * GetRelativeSplitScreenOffset(self.jollyHud.Camera).x) ||
                                distanceY > Math.Abs(self.jollyHud.Camera.sSize.y - magicNumber * GetRelativeSplitScreenOffset(self.jollyHud.Camera).y))
                        {
                            returnValue = false;
                        }
                    }
                    return returnValue;
                });
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                throw;
            }
        }

        /// <summary>
        /// Show other slugcat icons on the map even when they are in different rooms.
        /// Map.Draw's creature-sense block (map open, creature sense on) loops over
        /// (hud.owner as Creature).room.abstractRoom.creatures and reads that list three
        /// times: the loop bound, the element, and, for box worms and fire sprites, the
        /// element again for its colour. Every one of those reads gets one list of the
        /// creatures in every player's room. The old version redirected only the bound
        /// and the first read, so the colour read indexed the owner's own room with an
        /// index from the longer list: ArgumentOutOfRangeException inside hud.Draw, the
        /// first call of RoomCamera.DrawUpdate, which skipped that camera's draw and every
        /// later camera's. It also rebuilt the list (with LINQ) every frame for every
        /// camera, map open or not; now it is built only when the block runs.
        /// </summary>
        public void HudMap_Draw(ILContext il)
        {
            try
            {
                var c = new ILCursor(il);
                int redirected = 0;
                while (c.TryGotoNext(MoveType.After,
                    i => i.MatchCallOrCallvirt<Room>("get_abstractRoom"),
                    i => i.MatchLdfld<AbstractRoom>("creatures")))
                {
                    c.Emit(OpCodes.Ldarg_0);
                    c.EmitDelegate<Func<List<AbstractCreature>, HUD.Map, List<AbstractCreature>>>(MapCreatures);
                    redirected++;
                }
                if (redirected != 3)
                    Logger.LogWarning($"HudMap_Draw: redirected {redirected} reads of the owner's creature list, expected 3 (loop bound, element, box worm colour)");

                // disable vanishing of slugcat icons because of distance
                c.Index = 0;
                c.GotoNext(MoveType.Before,
                   i => i.MatchCallOrCallvirt<AbstractWorldEntity>("get_Room"),
                   i => i.MatchLdfld<AbstractRoom>("index"),
                   i => i.MatchLdarg(1)
                   );
                c.EmitDelegate<Func<AbstractWorldEntity, AbstractWorldEntity>>(entity =>
                {
                    mapIconCreature = entity as AbstractCreature;
                    return entity;
                });
                c.GotoNext(MoveType.After,
                   i => i.MatchCallOrCallvirt<UnityEngine.Mathf>("InverseLerp")
                   );
                c.EmitDelegate<Func<float, float>>(fade =>
                    mapIconCreature?.creatureTemplate?.type == CreatureTemplate.Type.Slugcat ? 1f : fade);
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                throw;
            }
        }

        // The creature whose map icon Map.Draw is fading by distance (see HudMap_Draw).
        private static AbstractCreature mapIconCreature;

        // Every player's room's creatures for one Map.Draw, built once per map per frame.
        private static readonly List<AbstractCreature> mapCreatures = new List<AbstractCreature>();
        private static readonly HashSet<AbstractCreature> mapCreaturesSeen = new HashSet<AbstractCreature>();
        private static HUD.Map mapCreaturesFor;
        private static int mapCreaturesFrame = -1;

        private static List<AbstractCreature> MapCreatures(List<AbstractCreature> ownerRoom, HUD.Map map)
        {
            RainWorldGame game = map?.hud?.rainWorld?.processManager?.currentMainLoop as RainWorldGame;
            List<AbstractCreature> players = game?.session?.Players;
            if (players == null || players.Count < 2) return ownerRoom;
            if (map == mapCreaturesFor && mapCreaturesFrame == Time.frameCount) return mapCreatures;
            mapCreaturesFor = map;
            mapCreaturesFrame = Time.frameCount;
            mapCreatures.Clear();
            mapCreaturesSeen.Clear();
            for (int p = 0; p < players.Count; p++)
            {
                List<AbstractCreature> creatures = players[p]?.realizedCreature?.room?.abstractRoom?.creatures;
                if (creatures == null) continue;
                for (int n = 0; n < creatures.Count; n++)
                    if (mapCreaturesSeen.Add(creatures[n])) mapCreatures.Add(creatures[n]);
            }
            return mapCreatures;
        }

        public void ToggleCameraZoom(RoomCamera cam)
        {
            if (dynamicStyle && !dualDisplays) return; // automatic region zoom replaces the manual toggle
            SetCameraZoom(cam, !cameraZoomed[cam.cameraNumber]);
            Logger.LogInfo($"[CameraZoom] frame={Time.frameCount} cam={cam.cameraNumber} zoomed={cameraZoomed[cam.cameraNumber]} room={cam.room?.abstractRoom?.name ?? "null"}");
        }

        public void SetCameraZoom(RoomCamera cam, bool enabled)
        {
            if (dynamicStyle && !dualDisplays) return;
            var camNum = cam.cameraNumber;
            int layoutSlot = Mathf.Max(0, renderedCameraNumbers.IndexOf(camNum));
            cameraZoomed[camNum] = enabled;
            if (enabled)
            {
                Rect wholeScreen = new Rect(0f, 0f, 1f, 1f);
                switch (CurrentSplitMode)
                {
                    case SplitMode.SplitHorizontal:
                        cameraListeners[camNum].SetMap(wholeScreen, InsetForSeparator(horizontalSplitCameraTargetPosZoomed[layoutSlot]));
                        break;
                    case SplitMode.SplitVertical:
                        cameraListeners[camNum].SetMap(wholeScreen, InsetForSeparator(verticalSplitCameraTargetPosZoomed[layoutSlot]));
                        break;
                    case SplitMode.Split4Screen:
                        cameraListeners[camNum].SetMap(wholeScreen, InsetForSeparator(fourSplitCameraTargetPos[layoutSlot]));
                        break;
                    case SplitMode.Split3Screen:
                        cameraListeners[camNum].SetMap(wholeScreen, InsetForSeparator(threeSplitCameraTargetPos[layoutSlot]));
                        break;
                }
                
            }
            else
            {
                switch (CurrentSplitMode)
                {
                    case SplitMode.SplitHorizontal:
                        cameraListeners[camNum].SetMap(InsetForSeparator(horizontalSplitScreenPart), InsetForSeparator(horizontalSplitCameraTargetPos[layoutSlot]));
                        break;
                    case SplitMode.SplitVertical:
                        cameraListeners[camNum].SetMap(InsetForSeparator(verticalSplitScreenPart), InsetForSeparator(verticalSplitCameraTargetPos[layoutSlot]));
                        break;
                    case SplitMode.Split4Screen:
                        cameraListeners[camNum].SetMap(InsetForSeparator(fourSplitScreenPart), InsetForSeparator(fourSplitCameraTargetPos[layoutSlot]));
                        break;
                    case SplitMode.Split3Screen:
                        cameraListeners[camNum].SetMap(InsetForSeparator(fourSplitScreenPart), InsetForSeparator(threeSplitCameraTargetPos[layoutSlot]));
                        break;
                }
            }
        }

        public Vector2 GetGlobalHudOffset(RoomCamera camera)
        {
            if (dynamicStyle && !dualDisplays) return Vector2.zero;
            if (!cameraZoomed[camera.cameraNumber])
                return GetRelativeSplitScreenOffset(camera);
            return new Vector2(0, 0);
        }

        public Vector2 GetSplitScreenHudOffset(RoomCamera camera, int cameraNumber)
        {
            if (dynamicStyle && !dualDisplays) return camOffsets[cameraNumber];
            Vector2 offset = camOffsets[cameraNumber];
            if (!cameraZoomed[camera.cameraNumber])
                offset += GetRelativeSplitScreenOffset(camera);
            return offset;
        }

        public Vector2 GetRelativeSplitScreenOffset(RoomCamera camera)
        {
            if (dynamicStyle && !dualDisplays) return Vector2.zero;
            Vector2 offset = new Vector2();
            if (CurrentSplitMode == SplitMode.SplitHorizontal)
            {
                offset = new Vector2(0, camera.sSize.y / 4f);
            }
            else if (CurrentSplitMode == SplitMode.SplitVertical)
            {
                offset = new Vector2(camera.sSize.x / 4f, 0f);
            }
            else if (IsGridSplit(CurrentSplitMode))
            {
                offset = new Vector2(camera.sSize.x / 4f, camera.sSize.y / 4f);
            }
            return offset;
        }

        public static void CustomDecal_ctor(On.CustomDecal.orig_ctor orig, CustomDecal self, PlacedObject placedObject)
        {
            orig(self, placedObject);
            for (int i = 0; i < 4; i++)
            {
                self.SetMeshDirty(i, self.meshDirty);
                self.SetElementDirty(i, self.elementDirty);
            }
        }
		
        public static void CustomDecal_DrawSprites(On.CustomDecal.orig_DrawSprites orig, CustomDecal self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            int cameraNumber = rCam.cameraNumber;
            self.meshDirty = self.IsMeshDirty(cameraNumber);
            self.elementDirty = self.IsElementDirty(cameraNumber);

            orig(self, sLeaser, rCam, timeStacker, camPos);

            self.SetMeshDirty(cameraNumber, self.meshDirty);
            self.SetElementDirty(cameraNumber, self.elementDirty);
        }

        public static void CustomDecal_InitiateSprites(On.CustomDecal.orig_InitiateSprites orig, CustomDecal self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam)
        {
            orig(self, sLeaser, rCam);
            self.SetMeshDirty(rCam.cameraNumber, self.meshDirty);
        }

        // Vanilla's dirty flags are one per decal; these give every camera its own. They
        // are only ever SET for all cameras here, never cleared: copying the last-drawn
        // camera's cleared flag to all four every tick meant a camera that was not
        // drawing then (the merged Adaptive view draws one) never built its mesh, and its
        // decals stayed missing after the split. Each camera clears its own flag when it
        // draws (CustomDecal_DrawSprites).
        public static void CustomDecal_Update(On.CustomDecal.orig_Update orig, CustomDecal self, bool eu)
        {
            orig(self, eu);
            if (self.meshDirty)
                for (int i = 0; i < 4; i++)
                    self.SetMeshDirty(i, true);
        }

        public static void CustomDecal_UpdateMesh(On.CustomDecal.orig_UpdateMesh orig, CustomDecal self)
        {
            orig(self);
            for (int i = 0; i < 4; i++)
                self.SetMeshDirty(i, true);
        }

        public static void CustomDecal_UpdateAsset(On.CustomDecal.orig_UpdateAsset orig, CustomDecal self)
        {
            orig(self);
            for (int i = 0; i < 4; i++)
                self.SetElementDirty(i, true);
        }

        public Vector4[] SnowSource_PackSnowData(On.MoreSlugcats.SnowSource.orig_PackSnowData orig, MoreSlugcats.SnowSource self)
        {
            Vector2 vector = self.room.cameraPositions[self.room.game.cameras[curCamera].currentCameraPosition];
            Vector4[] array = new Vector4[3];
            Vector2 vector2 = RWCustom.Custom.EncodeFloatRG((self.pos.x - vector.x) / 1400f * 0.3f + 0.3f);
            Vector2 vector3 = RWCustom.Custom.EncodeFloatRG((self.pos.y - vector.y) / 800f * 0.3f + 0.3f);
            Vector2 vector4 = RWCustom.Custom.EncodeFloatRG(self.rad / 1600f);
            array[0] = new Vector4(vector2.x, vector2.y, vector3.x, vector3.y);
            array[1] = new Vector4(vector4.x, vector4.y, self.intensity, self.noisiness);
            array[2] = new Vector4(0f, 0f, 0f, (float)((int)self.shape) / 5f);
            return array;
        }

        public void SnowSource_Update(On.MoreSlugcats.SnowSource.orig_Update orig, MoreSlugcats.SnowSource self, bool eu)
        {
            bool camChanged = false;
            for (int i = 0; i < self.room.game.cameras.Length; i++)
            {
                RoomCamera cam = self.room.game.cameras[i];
                if (cam.room == self.room)
                {
                    if (self.GetLastCamPos(i) != cam.currentCameraPosition)
                    {
                        camChanged = true;
                        break;
                    }
                }
            }

            bool flag = false;
            if (camChanged || self.shape != self.lastShape || self.noisiness != self.lastNoisiness || self.intensity != self.lastIntensity || self.pos != self.lastPos || self.rad != self.lastRad || (self.room.BeingViewed && self.visibility == 2))
            {
                flag = true;
            }
            if (flag && self.room.snow && self.room.BeingViewed)
            {
                // Not visible unless a camera says so. This started from self.visibility,
                // so a source outside every camera's screen stayed at 2 ("never checked"),
                // which is itself a reason to run this block: every camera in the room
                // re-blitted its full-screen snow map on every tick, for ever.
                int finalVisibility = 0;
                for (int j = 0; j < self.room.game.cameras.Length; j++)
                {
                    RoomCamera cam = self.room.game.cameras[j];
                    if (cam.room != self.room)
                        continue;
                    int vis = self.CheckVisibility(cam.currentCameraPosition);
                    if (vis != 0)
                        finalVisibility = vis;
                    cam.snowChange = true;
                }
                self.visibility = finalVisibility;
            }

            for (int i = 0; i < self.room.game.cameras.Length; i++)
            {
                RoomCamera cam = self.room.game.cameras[i];
                self.SetLastCamPos(cam.cameraNumber, cam.currentCameraPosition);
            }

            self.lastPos = self.pos;
            self.lastRad = self.rad;
            self.lastIntensity = self.intensity;
            self.lastNoisiness = self.noisiness;
            self.lastShape = self.shape;
        }

        public void DustPuff_ApplyPalette(On.RoofTopView.DustpuffSpawner.DustPuff.orig_ApplyPalette orig, RoofTopView.DustpuffSpawner.DustPuff self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
        {
            if (rCam.room.snow)
            {
                int camNum = curCamera;
                if (camNum == -1)
                {
                    for (int i = 0; i < rCam.room.game.cameras.Length; i++)
                    {
                        RoomCamera cam = rCam.room.game.cameras[i];
                        if (rCam.room == cam.room)
                        {
                            camNum = i;
                            break;
                        }
                    }
                }
                Vector2 vector = self.pos - rCam.room.cameraPositions[rCam.room.game.cameras[camNum].currentCameraPosition];
                sLeaser.sprites[0].color = new Color(vector.x / 1400f, vector.y / 800f, 0f);
            }
            else
                orig(self, sLeaser, rCam, palette);
        }

        public static void BlizzardGraphics_DrawSprites(On.MoreSlugcats.BlizzardGraphics.orig_DrawSprites orig, MoreSlugcats.BlizzardGraphics self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            RoomCamera oldCam = self.rCam;
            Vector2 vector = rCam.pos;
            if (rCam.room == self.room)
                self.rCam = rCam;
            orig(self, sLeaser, rCam, timeStacker, camPos);
            self.rCam = oldCam;
        }
    }

    public static class JollyHUDExtension
    {
        public class SplitScreenCamera
        {
            public RoomCamera cam;
        }

        private static readonly ConditionalWeakTable<JollyCoop.JollyHUD.JollyPlayerSpecificHud, SplitScreenCamera> _cwt = new();
        public static RoomCamera GetSplitScreenCamera(this JollyCoop.JollyHUD.JollyPlayerSpecificHud hud)
        {
            return _cwt.GetValue(hud, _cwt => new()).cam;
        }
        public static RoomCamera SetSplitScreenCamera(this JollyCoop.JollyHUD.JollyPlayerSpecificHud hud, RoomCamera cam)
        {
            return _cwt.GetValue(hud, _cwt => new()).cam = cam;
        }
    }

    public static class CustomDecalExtension
    {
        public class SplitScreenCustomDecal
        {
            public bool[] meshDirty = new bool[4];
            public bool[] elementDirty = new bool[4];
        }

        private static readonly ConditionalWeakTable<CustomDecal, SplitScreenCustomDecal> _cwt = new();
        public static bool IsMeshDirty(this CustomDecal decal, int cameraNumber)
        {
            return _cwt.GetValue(decal, _cwt => new()).meshDirty[cameraNumber];
        }
        public static bool SetMeshDirty(this CustomDecal decal, int cameraNumber, bool isDirty)
        {
            return _cwt.GetValue(decal, _cwt => new()).meshDirty[cameraNumber] = isDirty;
        }
        public static bool IsElementDirty(this CustomDecal decal, int cameraNumber)
        {
            return _cwt.GetValue(decal, _cwt => new()).elementDirty[cameraNumber];
        }
        public static bool SetElementDirty(this CustomDecal decal, int cameraNumber, bool isDirty)
        {
            return _cwt.GetValue(decal, _cwt => new()).elementDirty[cameraNumber] = isDirty;
        }
    }

    public static class SnowSourceExtension
    {
        public class SplitScreenSnowSource
        {
            public int[] lastCam = new int[4];
        }

        private static readonly ConditionalWeakTable<MoreSlugcats.SnowSource, SplitScreenSnowSource> _cwt = new();
        public static int SetLastCamPos(this MoreSlugcats.SnowSource ss, int cameraNumber, int val)
        {
            return _cwt.GetValue(ss, _cwt => new()).lastCam[cameraNumber] = val;
        }
        public static int GetLastCamPos(this MoreSlugcats.SnowSource ss, int cameraNumber)
        {
            return _cwt.GetValue(ss, _cwt => new()).lastCam[cameraNumber];
        }
    }
}
