using System;
using System.Collections.Generic;
using UnityEngine;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        private const int CameraHealthScanInterval = 15;
        private const int RenderStallFrames = 80;
        private const int RenderRecoveryCooldown = 240;
        private const int RoomMismatchRecoveryFrames = 60;
        private readonly int[] lastRoomCameraUpdateFrames = { -1, -1, -1, -1 };
        private readonly int[] lastRoomCameraDrawFrames = { -1, -1, -1, -1 };
        private readonly int[] roomMismatchSinceFrames = { -1, -1, -1, -1 };
        private readonly bool[] cameraRoomResyncInProgress = new bool[4];
        private readonly string[] lastCameraStateKeys = new string[4];
        private int lastCameraHealthScanFrame = -CameraHealthScanInterval;
        private int lastCameraHeartbeatFrame = -600;

        // Frame-hitch diagnostics. Playtests reported stutter, but the log had no
        // timing at all; now any frame over the threshold is logged together with
        // the expensive things that happened in it.
        private const float HitchThresholdSeconds = 0.05f;
        private const int HitchLogCooldownFrames = 30;
        private static readonly System.Text.StringBuilder frameEvents = new System.Text.StringBuilder(256);
        private static int frameEventCount;
        private static int frameEventFrame = -1;
        private int lastHitchLogFrame = -10000;
        private int suppressedHitches;
        private int lastGcCollectionCount;

        internal static void NoteFrameEvent(string text)
        {
            if (frameEventFrame != Time.frameCount)
            {
                frameEvents.Length = 0;
                frameEventCount = 0;
                frameEventFrame = Time.frameCount;
            }
            if (frameEventCount++ >= 8) return;
            if (frameEvents.Length > 0) frameEvents.Append("; ");
            frameEvents.Append(text);
        }

        private void NoteFrameTime()
        {
            float seconds = Time.unscaledDeltaTime;
            int collections = GC.CollectionCount(0);
            int collectionDelta = collections - lastGcCollectionCount;
            lastGcCollectionCount = collections;
            if (seconds < HitchThresholdSeconds) return;
            if (Time.frameCount - lastHitchLogFrame < HitchLogCooldownFrames)
            {
                suppressedHitches++;
                return;
            }
            string events = frameEventFrame >= Time.frameCount - 1 ? frameEvents.ToString() : "";
            Logger.LogInfo($"[FrameHitch] frame={Time.frameCount} ms={seconds * 1000f:0} gcCollections={collectionDelta} suppressedSinceLast={suppressedHitches} sharedLevelTextures={sharedLevelTextureCopies} events=[{events}]");
            suppressedHitches = 0;
            lastHitchLogFrame = Time.frameCount;
        }

        // ---- Unity log capture and hook error shielding ------------------------
        // The playtest BepInEx config has WriteUnityLog = false, so an exception
        // thrown inside a hook or vanilla draw code never reaches LogOutput.log. A
        // black screen with audio was exactly that: RoomCamera.DrawUpdate threw every
        // frame from one moment on (roomDrawAge climbed while roomUpdateAge stayed 0)
        // and nothing said why. Mirror Unity's errors into this log, rate limited per
        // distinct message, so the next such log names the throw.
        private static readonly Dictionary<string, int> unityErrorLastLogged = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> unityErrorCounts = new Dictionary<string, int>();
        private static string lastUnityError = "none";
        private static bool unityLogHooked;

        private static void HookUnityLog()
        {
            if (unityLogHooked) return;
            unityLogHooked = true;
            Application.logMessageReceived += OnUnityLogMessage;
        }

        private static void OnUnityLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error && type != LogType.Assert) return;
            // Key on the message plus the top two frames. Keyed on the message alone,
            // every "NullReferenceException" from anywhere shared one bucket, printed
            // with the first stack seen, and the throw that mattered (the shutdown
            // one behind the sleep lockout) was never shown.
            string key = condition ?? "";
            if (!string.IsNullOrEmpty(stackTrace))
            {
                int first = stackTrace.IndexOf('\n');
                int second = first < 0 ? -1 : stackTrace.IndexOf('\n', first + 1);
                key += "|" + (second < 0 ? stackTrace : stackTrace.Substring(0, second)).Trim();
            }
            int count;
            unityErrorCounts.TryGetValue(key, out count);
            unityErrorCounts[key] = ++count;
            lastUnityError = key;
            int last;
            bool seen = unityErrorLastLogged.TryGetValue(key, out last);
            if (seen && Time.frameCount - last < 600) return;
            unityErrorLastLogged[key] = Time.frameCount;
            string trace = string.IsNullOrEmpty(stackTrace) ? "" : "\n" + stackTrace.TrimEnd();
            sLogger?.LogError($"[UnityLog] frame={Time.frameCount} {type} (x{count}): {condition}{trace}");
        }

        /// <summary>
        /// The mod's own code that runs after a vanilla call must never take the
        /// game's frame down with it. Log the error, rate limited per call site.
        /// </summary>
        private static readonly Dictionary<string, int> hookErrorLastLogged = new Dictionary<string, int>();

        internal static void LogHookError(string site, Exception error)
        {
            int last;
            if (hookErrorLastLogged.TryGetValue(site, out last) && Time.frameCount - last < 600) return;
            hookErrorLastLogged[site] = Time.frameCount;
            sLogger?.LogError($"[HookError] frame={Time.frameCount} in {site}: {error}");
        }

        /// <summary>
        /// Set when the game's draw loop stopped completing. The mod's code inside
        /// that loop (HUD routing, shader capture, level texture sharing) passes
        /// through while it is set, so if the mod caused the stall the picture comes
        /// back and the log says so; if it persists, the cause is elsewhere.
        /// </summary>
        internal static bool drawPathSafeMode;
        private int drawStallLoggedFrame = -10000;
        private int drawStallSince = -1;

        private void DetectDrawStall(RainWorldGame game)
        {
            if (game?.cameras == null || game.cameras.Length == 0 || game.GamePaused) { drawStallSince = -1; return; }
            int number = game.cameras[0].cameraNumber;
            if (number < 0 || number >= lastRoomCameraDrawFrames.Length) return;
            int drawAge = FrameAge(lastRoomCameraDrawFrames[number]);
            int updateAge = FrameAge(lastRoomCameraUpdateFrames[number]);
            if (drawAge < 30 || updateAge > 2 || lastRoomCameraDrawFrames[number] < 0) { drawStallSince = -1; return; }
            if (drawStallSince < 0) drawStallSince = Time.frameCount;
            if (Time.frameCount - drawStallLoggedFrame < 300) return;
            drawStallLoggedFrame = Time.frameCount;
            if (!drawPathSafeMode)
            {
                drawPathSafeMode = true;
                Logger.LogError($"[CameraHealth] frame={Time.frameCount} draw loop stalled: camera {number} has not completed DrawUpdate for {drawAge} frames while Update still runs (an exception inside the draw loop every frame). Last Unity error: {lastUnityError}. Switching the mod's draw-path code to pass-through.");
            }
            else
                Logger.LogError($"[CameraHealth] frame={Time.frameCount} draw loop still stalled for {Time.frameCount - drawStallSince} frames with the mod's draw-path code in pass-through; the throw is in vanilla or another mod. Last Unity error: {lastUnityError}");
        }

        // ---- Hang watchdog ---------------------------------------------------
        // A playtest ended in a hard freeze with nothing in the log. The main thread
        // cannot report its own hang, so a background thread watches the frame
        // counter and, after four seconds without progress, logs the last marker the
        // main thread set. Markers name the mod code (or the vanilla call) that was
        // running, so the next freeze says where it happened.
        internal static volatile string HangMarker = "startup";
        internal static volatile int WatchdogFrame;
        private static System.Threading.Thread hangWatchdog;

        internal static void StartHangWatchdog()
        {
            if (hangWatchdog != null) return;
            hangWatchdog = new System.Threading.Thread(() =>
            {
                int lastFrame = -1, stalledSeconds = 0;
                while (true)
                {
                    System.Threading.Thread.Sleep(1000);
                    int frame = WatchdogFrame;
                    if (frame == lastFrame)
                    {
                        stalledSeconds++;
                        if (stalledSeconds == 4 || stalledSeconds == 30)
                            sLogger?.LogWarning($"[Hang] main thread has not advanced past frame {frame} for {stalledSeconds} s; last marker={HangMarker}");
                    }
                    else
                    {
                        stalledSeconds = 0;
                        lastFrame = frame;
                    }
                }
            }) { IsBackground = true, Name = "SplitScreen hang watchdog" };
            hangWatchdog.Start();
        }

        // ---- Menu camera watchdog ---------------------------------------------
        // Two playtests ended on a black screen after Exit and after the death
        // screen. Outside a game session exactly one camera may draw: Futile's own
        // camera, into Futile's screen texture, with the root stage in its mask.
        // Enforce that every frame while no game runs, and log what had to be
        // corrected plus a snapshot after every process switch so the log names the
        // culprit if it ever recurs.
        private MainLoopProcess lastObservedProcess;
        private int lastMenuCorrectionLogFrame = -10000;

        internal void EnforceMenuCameraState()
        {
            ProcessManager manager = rainworldGameObject?.processManager;
            MainLoopProcess process = manager?.currentMainLoop;
            if (process == null || process is RainWorldGame || fcameras[0] == null || Futile.screen?.renderTexture == null) return;
            var corrections = RestoreMenuCameras(null);
            bool switched = process != lastObservedProcess;
            lastObservedProcess = process;
            if (switched || (corrections.Count > 0 && Time.frameCount - lastMenuCorrectionLogFrame > 120))
            {
                lastMenuCorrectionLogFrame = Time.frameCount;
                Logger.LogInfo($"[MenuCamera] frame={Time.frameCount} process={process.ID} corrections=[{string.Join("; ", corrections)}] cam0=(enabled={fcameras[0].enabled} mask={fcameras[0].cullingMask} target={fcameras[0].targetTexture?.name ?? "null"} isFutileCamera={Futile.instance?.camera == fcameras[0]}) stageLayer={Futile.stage?.layer} stageChildren={Futile.stage?.GetChildCount()} screenImage={(Futile.instance?._cameraImage?.texture == Futile.screen.renderTexture)} fadeToBlack={manager.fadeToBlack:0.00} blackDelay={manager.blackDelay:0.00}");
                if (switched) LogStageChildren();
            }
        }

        /// <summary>
        /// The root stage held ~500 children after one session where a menu has 3.
        /// Whatever is left there is drawn over every menu by camera 0. Name the
        /// leftovers by type (and sprite element) so the next log says what leaks.
        /// </summary>
        private void LogStageChildren()
        {
            FStage stage = Futile.stage;
            if (stage == null || stage.GetChildCount() <= 40) return;
            var histogram = new Dictionary<string, int>();
            for (int i = 0; i < stage.GetChildCount(); i++)
            {
                FNode child = stage.GetChildAt(i);
                string name = child == null ? "null" : child.GetType().Name;
                if (child is FSprite sprite) name += ":" + (sprite.element?.name ?? "?");
                else if (child is FContainer container) name += "(" + container.GetChildCount() + ")";
                int seen;
                histogram.TryGetValue(name, out seen);
                histogram[name] = seen + 1;
            }
            var lines = new List<string>(histogram.Count);
            foreach (var entry in histogram) lines.Add(entry.Key + "x" + entry.Value);
            lines.Sort();
            Logger.LogInfo($"[MenuCamera] frame={Time.frameCount} stage children by type: {string.Join(", ", lines)}");
        }

        /// <summary>
        /// Put the Unity cameras into the one configuration a menu can draw with:
        /// camera 0 enabled, rendering Futile's screen texture with the root stage in
        /// its mask, everything else off. Returns what had to change.
        /// </summary>
        internal List<string> RestoreMenuCameras(string reason)
        {
            var corrections = new List<string>(4);
            if (fcameras[0] == null || Futile.screen?.renderTexture == null) return corrections;
            if (!fcameras[0].enabled) { fcameras[0].enabled = true; corrections.Add("camera 0 was disabled"); }
            if (fcameras[0].targetTexture != Futile.screen.renderTexture)
            {
                corrections.Add($"camera 0 target was {fcameras[0].targetTexture?.name ?? "null"}");
                fcameras[0].targetTexture = Futile.screen.renderTexture;
            }
            int stageBit = Futile.stage != null ? 1 << Futile.stage.layer : 0;
            if ((fcameras[0].cullingMask & stageBit) == 0 || fcameras[0].cullingMask == 0)
            {
                corrections.Add($"camera 0 mask was {fcameras[0].cullingMask}");
                fcameras[0].cullingMask = initialWorldCullingMasks[0] | stageBit;
                if (fcameras[0].cullingMask == 0) fcameras[0].cullingMask = -1;
            }
            for (int i = 1; i < fcameras.Length; i++)
                if (fcameras[i] != null && fcameras[i].enabled) { fcameras[i].enabled = false; corrections.Add($"camera {i} was enabled"); }
            for (int i = 0; i < hudCameras.Length; i++)
                if (hudCameras[i] != null && hudCameras[i].enabled) { hudCameras[i].enabled = false; corrections.Add($"HUD camera {i} was enabled"); }
            if (globalHudCamera != null && globalHudCamera.enabled) { globalHudCamera.enabled = false; corrections.Add("global HUD camera was enabled"); }
            if (dynamicCompositorCamera != null && dynamicCompositorCamera.enabled) { dynamicCompositorCamera.enabled = false; corrections.Add("compositor camera was enabled"); }
            if (reason != null && corrections.Count > 0)
                Logger.LogInfo($"[MenuCamera] frame={Time.frameCount} restored for {reason}: [{string.Join("; ", corrections)}]");
            return corrections;
        }

        private void ProcessManager_PostSwitchMainProcess(On.ProcessManager.orig_PostSwitchMainProcess orig, ProcessManager self, ProcessManager.ProcessID ID)
        {
            orig(self, ID);
            EnforceMenuCameraState();
        }

        private void AbstractRoom_RealizeRoom(On.AbstractRoom.orig_RealizeRoom orig, AbstractRoom self, World world, RainWorldGame game)
        {
            bool fresh = self.realizedRoom == null;
            orig(self, world, game);
            if (fresh && self.realizedRoom != null) NoteFrameEvent("realize " + self.name);
        }

        private void AbstractRoom_Abstractize(On.AbstractRoom.orig_Abstractize orig, AbstractRoom self)
        {
            if (self.realizedRoom != null) NoteFrameEvent("abstractize " + self.name);
            orig(self);
        }

        private void ResetCameraDiagnostics()
        {
            lastCameraHealthScanFrame = -CameraHealthScanInterval;
            lastCameraHeartbeatFrame = -600;
            renderedCameraNumbers.Clear();
            for (int i = 0; i < lastRoomCameraUpdateFrames.Length; i++)
            {
                lastRoomCameraUpdateFrames[i] = -1;
                lastRoomCameraDrawFrames[i] = -1;
                roomMismatchSinceFrames[i] = -1;
                cameraRoomResyncInProgress[i] = false;
                lastCameraStateKeys[i] = null;
                cameraListeners[i]?.MarkRenderingExpected(false);
            }
        }

        private void NoteRoomCameraUpdated(RoomCamera camera)
        {
            if (ValidCameraNumber(camera)) lastRoomCameraUpdateFrames[camera.cameraNumber] = Time.frameCount;
        }

        private void NoteRoomCameraDrawn(RoomCamera camera)
        {
            if (ValidCameraNumber(camera)) lastRoomCameraDrawFrames[camera.cameraNumber] = Time.frameCount;
        }

        private void NoteRoomCameraMoved(RoomCamera camera, string source)
        {
            if (!ValidCameraNumber(camera)) return;
            Logger.LogInfo($"[CameraMove] frame={Time.frameCount} source={source} cam={camera.cameraNumber} room={RoomName(camera.room)} loading={RoomName(camera.loadingRoom)} position={camera.currentCameraPosition} follow={PlayerNumber(camera.followAbstractCreature)}");
            NoteFrameEvent(source + " cam=" + camera.cameraNumber);
            lastCameraStateKeys[camera.cameraNumber] = null;
        }

        private static bool ValidCameraNumber(RoomCamera camera)
        {
            return camera != null && camera.cameraNumber >= 0 && camera.cameraNumber < 4;
        }

        private static int PlayerNumber(AbstractCreature creature)
        {
            return (creature?.state as PlayerState)?.playerNumber ?? -1;
        }

        private static string RoomName(Room room)
        {
            return room == null ? "null" : $"{room.world?.name ?? "?"}/{room.abstractRoom?.name ?? "?"}";
        }

        private static int FrameAge(int frame)
        {
            return frame < 0 ? -1 : Time.frameCount - frame;
        }

        private void RefreshActiveCameraRendering(RainWorldGame game, string reason)
        {
            if (game?.cameras == null) return;
            for (int i = 0; i < cameraListeners.Length; i++)
            {
                CameraListener listener = cameraListeners[i];
                if (listener == null) continue;
                bool expected = renderedCameraNumbers.Contains(i) && i < fcameras.Length && fcameras[i] != null;
                listener.MarkRenderingExpected(expected);
                if (!expected) continue;
                listener.PrepareForRendering();
                fcameras[i].enabled = true;
            }
            Logger.LogInfo($"[CameraRenderTarget] frame={Time.frameCount} refreshed; reason={reason}; expectedUnityCameras=[{string.Join(",", renderedCameraNumbers)}]");
        }

        private void MonitorCameraHealth(RainWorldGame game)
        {
            if (game?.cameras == null || Time.frameCount - lastCameraHealthScanFrame < CameraHealthScanInterval) return;
            lastCameraHealthScanFrame = Time.frameCount;
            DetectDrawStall(game);
            LogCameraSnapshot(game, "state change", false);
            if (Time.frameCount - lastCameraHeartbeatFrame >= 600)
            {
                lastCameraHeartbeatFrame = Time.frameCount;
                LogCameraSnapshot(game, "periodic heartbeat", true);
            }

            foreach (int i in renderedCameraNumbers.ToArray())
            {
                if (i < 0 || i >= cameraListeners.Length) continue;
                CameraListener listener = cameraListeners[i];
                Camera unityCamera = i < fcameras.Length ? fcameras[i] : null;
                if (listener == null || unityCamera == null) continue;
                listener.MarkRenderingExpected(true);
                int lastCompletedFrame = listener.dynamicCompositing
                    ? Math.Min(listener.lastPostRenderFrame, listener.lastCompositeFrame)
                    : listener.direct ? listener.lastPostRenderFrame : listener.lastCompositeFrame;
                int newestRenderFrame = Math.Max(listener.renderingExpectedSinceFrame, lastCompletedFrame);
                int renderAge = Time.frameCount - newestRenderFrame;
                if (renderAge <= RenderStallFrames || Time.frameCount - listener.lastRecoveryFrame <= RenderRecoveryCooldown) continue;

                Logger.LogWarning($"[CameraHealth] frame={Time.frameCount} cam={i} stopped rendering/compositing for {renderAge} frames; enabled={unityCamera.enabled}; active={unityCamera.gameObject.activeInHierarchy}; direct={listener.direct}; target={RenderTargetState(listener)}; roomUpdateAge={FrameAge(lastRoomCameraUpdateFrames[i])}; roomDrawAge={FrameAge(lastRoomCameraDrawFrames[i])}; preRenderAge={FrameAge(listener.lastPreRenderFrame)}; postRenderAge={FrameAge(listener.lastPostRenderFrame)}; compositeAge={FrameAge(listener.lastCompositeFrame)}; attempting render-target recovery");
                listener.RecoverRendering();
                unityCamera.enabled = true;
                lastCameraStateKeys[i] = null;
            }

            if (dynamicActive && dynamicCompositorCamera != null && dynamicCompositorCamera.enabled &&
                dynamicCompositor != null && compositorExpectedSinceFrame >= 0 &&
                Time.frameCount - Math.Max(compositorExpectedSinceFrame,
                    dynamicCompositor.lastCompositeFrame) > RenderStallFrames)
            {
                Logger.LogWarning($"[CameraHealth] frame={Time.frameCount} final polygon compositor has not completed for {Time.frameCount - dynamicCompositor.lastCompositeFrame} frames; enabled={dynamicCompositorCamera.enabled}; target={dynamicCompositorCamera.targetTexture?.name ?? "null"}; resetting compositor camera");
                dynamicCompositorCamera.enabled = false;
                ReinitDynamicCompositorTexture();
                dynamicCompositorCamera.enabled = true;
                dynamicCompositor.lastCompositeFrame = Time.frameCount;
                if (++compositorRecoveries >= 2)
                {
                    Logger.LogError("[CameraHealth] polygon compositor stalled after recovery; scheduling Classic fallback");
                    dynamicPipelineFailed = true;
                }
            }
            if (dynamicActive)
            {
                for (int i = 0; i < hudCameras.Length; i++)
                {
                    if (hudCameras[i] == null || !hudCameras[i].enabled || hudExpectedSinceFrames[i] < 0) continue;
                    int age = Time.frameCount - Math.Max(hudExpectedSinceFrames[i], lastHudPostFrames[i]);
                    if (age <= RenderStallFrames || Time.frameCount - lastHudRecoveryFrames[i] <= RenderRecoveryCooldown) continue;
                    Logger.LogWarning($"[CameraHealth] frame={Time.frameCount} unzoomed HUD cam={i} stalled for {age} frames; rebuilding its mod-owned render texture");
                    hudCameras[i].enabled = false;
                    ReinitHudTexture(i);
                    hudCameras[i].enabled = true;
                    lastHudRecoveryFrames[i] = Time.frameCount;
                    hudExpectedSinceFrames[i] = Time.frameCount;
                }
                if (globalHudCamera != null && globalHudCamera.enabled && globalHudExpectedSinceFrame >= 0)
                {
                    int age = Time.frameCount - Math.Max(globalHudExpectedSinceFrame, lastGlobalHudPostFrame);
                    if (age > RenderStallFrames && Time.frameCount - lastGlobalHudRecoveryFrame > RenderRecoveryCooldown)
                    {
                        Logger.LogWarning($"[CameraHealth] frame={Time.frameCount} global HUD camera stalled for {age} frames; rebuilding its mod-owned render texture");
                        globalHudCamera.enabled = false;
                        ReinitGlobalHudTexture();
                        globalHudCamera.enabled = true;
                        lastGlobalHudRecoveryFrame = Time.frameCount;
                        globalHudExpectedSinceFrame = Time.frameCount;
                    }
                }
            }
        }

        private void LogCameraSnapshot(RainWorldGame game, string reason, bool force)
        {
            if (game?.cameras == null) return;
            for (int i = 0; i < game.cameras.Length && i < lastCameraStateKeys.Length; i++)
            {
                RoomCamera roomCamera = game.cameras[i];
                Camera unityCamera = i < fcameras.Length ? fcameras[i] : null;
                CameraListener listener = i < cameraListeners.Length ? cameraListeners[i] : null;
                Creature followedCreature = roomCamera?.followAbstractCreature?.realizedCreature;
                string realizedRoom = RoomName(followedCreature?.room);
                string key = $"mode={CurrentSplitMode}|world={game.world?.name}|room={RoomName(roomCamera?.room)}|loading={RoomName(roomCamera?.loadingRoom)}|position={roomCamera?.currentCameraPosition}|follow={PlayerNumber(roomCamera?.followAbstractCreature)}|realizedRoom={realizedRoom}|enabled={unityCamera?.enabled}|direct={listener?.direct}|target={RenderTargetState(listener)}|zoom={cameraZoomed[i]}";
                if (!force && lastCameraStateKeys[i] == key) continue;
                lastCameraStateKeys[i] = key;
                // Palette state rides along on the heartbeat so two views that render
                // the same room with different colours can be compared in the log.
                string palette = "";
                if (force && roomCamera != null)
                {
                    float fog = -1f;
                    if (listener != null && !listener.ShaderFloats.TryGetValue(RainWorld.ShadPropFogAmount, out fog)) fog = -1f;
                    palette = $"; palA={roomCamera.paletteA} palB={roomCamera.paletteB} blend={roomCamera.paletteBlend:0.00} ghost={roomCamera.ghostMode:0.00} dark={roomCamera.currentPalette.darkness:0.00} fog={fog:0.00} dayNight={roomCamera.effect_dayNight:0.00} darkEff={roomCamera.effect_darkness:0.00} palTexOwn={(roomCamera.paletteTexture != null && listener != null && listener.ShaderTextures.TryGetValue(RainWorld.ShadPropPalTex, out Texture bound) && bound == roomCamera.paletteTexture)}";
                }
                Logger.LogInfo($"[CameraState] frame={Time.frameCount} reason={reason} cam={i} {key}; cameraPos={roomCamera?.pos}; playerPos={followedCreature?.mainBodyChunk?.pos}; inShortcut={followedCreature?.inShortcut}; mapVisible={roomCamera?.hud?.map?.visible}; roomUpdateAge={FrameAge(lastRoomCameraUpdateFrames[i])}; roomDrawAge={FrameAge(lastRoomCameraDrawFrames[i])}; preRenderAge={FrameAge(listener?.lastPreRenderFrame ?? -1)}; postRenderAge={FrameAge(listener?.lastPostRenderFrame ?? -1)}; compositeAge={FrameAge(listener?.lastCompositeFrame ?? -1)}{palette}");
            }
        }

        private static string RenderTargetState(CameraListener listener)
        {
            RenderTexture texture = listener?.fcamera?.targetTexture;
            if (texture == null) return "null";
            return $"{texture.name}:{texture.width}x{texture.height}:created={texture.IsCreated()}";
        }

        private void ReconcileCameraRoom(RoomCamera camera, AbstractCreature player, bool immediate, string reason)
        {
            if (!ValidCameraNumber(camera) || player == null) return;
            int cameraNumber = camera.cameraNumber;
            if (cameraRoomResyncInProgress[cameraNumber]) return;
            Room desiredRoom = player.realizedCreature?.room ?? player.Room?.realizedRoom;
            if (desiredRoom == null || camera.room == desiredRoom || camera.loadingRoom == desiredRoom)
            {
                roomMismatchSinceFrames[cameraNumber] = -1;
                return;
            }

            if (roomMismatchSinceFrames[cameraNumber] < 0)
            {
                roomMismatchSinceFrames[cameraNumber] = Time.frameCount;
                Logger.LogInfo($"[CameraRoomMismatch] frame={Time.frameCount} cam={cameraNumber} cameraRoom={RoomName(camera.room)} desiredRoom={RoomName(desiredRoom)} player={PlayerNumber(player)} aboutToSwitch={camera.AboutToSwitchRoom}; reason={reason}");
            }

            int mismatchAge = Time.frameCount - roomMismatchSinceFrames[cameraNumber];
            bool wrongWorld = camera.room != null &&
                (camera.room.world != desiredRoom.world || camera.room.world != camera.game.world);
            bool playerInShortcut = player.realizedCreature is Creature creature && creature.inShortcut;
            if (playerInShortcut && !wrongWorld) return;
            if (!immediate && !wrongWorld && mismatchAge < RoomMismatchRecoveryFrames) return;
            if (camera.AboutToSwitchRoom && !wrongWorld && mismatchAge < RoomMismatchRecoveryFrames * 4) return;

            int node = player.pos.abstractNode;
            int viewingNode = desiredRoom.CameraViewingNode(node >= 0 ? node : 0);
            Logger.LogWarning($"[CameraHealth] frame={Time.frameCount} cam={cameraNumber} forcing room resync after {mismatchAge} frames; from={RoomName(camera.room)} to={RoomName(desiredRoom)} loading={RoomName(camera.loadingRoom)} player={PlayerNumber(player)} node={node}; reason={reason}");
            try
            {
                cameraRoomResyncInProgress[cameraNumber] = true;
                camera.MoveCamera(desiredRoom, viewingNode);
                cameraListeners[cameraNumber]?.PrepareForRendering();
                roomMismatchSinceFrames[cameraNumber] = -1;
                lastCameraStateKeys[cameraNumber] = null;
            }
            catch (Exception exception)
            {
                Logger.LogError($"[CameraHealth] room resync failed for cam={cameraNumber}: {exception}");
                roomMismatchSinceFrames[cameraNumber] = Time.frameCount;
            }
            finally
            {
                cameraRoomResyncInProgress[cameraNumber] = false;
            }
        }
    }
}
