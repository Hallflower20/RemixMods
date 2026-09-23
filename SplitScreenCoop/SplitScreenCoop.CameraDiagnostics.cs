using System;
using System.Collections.Generic;
using UnityEngine;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        // Durations are seconds, turned into frames at the current frame rate and never
        // fewer than at 60 fps. Written as frame counts they shrank with the frame rate:
        // at 240 fps (the rotation's raised limit) a room mismatch forced a synchronous
        // resync after 0.25 s, a stuck camera was rebuilt every second, and the health
        // scan ran 16 times a second.
        private static int FramesFor(float seconds)
        {
            int atSixty = Mathf.CeilToInt(seconds * 60f);
            int atCurrentRate = Mathf.CeilToInt(seconds / Mathf.Max(0.001f, typicalFrameSeconds));
            return Math.Max(atSixty, atCurrentRate);
        }

        private static int CameraHealthScanInterval => FramesFor(0.25f);
        private static int RenderStallFrames => FramesFor(80f / 60f);
        private static int RenderRecoveryCooldown => FramesFor(4f);
        private static int RoomMismatchRecoveryFrames => FramesFor(1f);
        private readonly int[] lastRoomCameraUpdateFrames = { -1, -1, -1, -1 };
        private readonly int[] lastRoomCameraDrawFrames = { -1, -1, -1, -1 };
        private readonly int[] roomMismatchSinceFrames = { -1, -1, -1, -1 };
        private readonly bool[] cameraRoomResyncInProgress = new bool[4];
        /// <summary>Per camera: a hash of what the [CameraState] line says; long.MinValue means "log it next scan".</summary>
        private readonly long[] lastCameraStateSignatures = { long.MinValue, long.MinValue, long.MinValue, long.MinValue };
        private int lastCameraHealthScanFrame = -CameraHealthScanInterval;
        private float lastCameraHeartbeatTime = -1000f;
        private int heartbeatsSinceTextureCensus = 1000;

        // Frame-hitch diagnostics. Playtests reported stutter, but the log had no
        // timing at all; now any frame over the threshold is logged together with
        // the expensive things that happened in it.
        private const int HitchLogCooldownFrames = 30;
        private static System.Text.StringBuilder frameEvents = new System.Text.StringBuilder(256);
        private static System.Text.StringBuilder previousFrameEvents = new System.Text.StringBuilder(256);
        private static int frameEventCount;
        private static int frameEventFrame = -1;
        private static int previousFrameEventFrame = -1;
        private int lastHitchLogFrame = -10000;
        private int suppressedHitches;

        internal static void NoteFrameEvent(string text)
        {
            RollFrameEvents();
            if (frameEventCount++ >= 8) return;
            if (frameEvents.Length > 0) frameEvents.Append("; ");
            frameEvents.Append(text);
        }

        /// <summary>
        /// Keeps the events of the frame before this one. A hitch is logged at the end of
        /// the frame after the slow one (Time.unscaledDeltaTime measures the previous
        /// frame), and that frame's first event used to wipe the buffer: in Dynamic every
        /// tick notes "solve layout", so the real cause of a hitch was nearly always gone.
        /// </summary>
        private static void RollFrameEvents()
        {
            int now = Time.frameCount;
            if (frameEventFrame == now) return;
            System.Text.StringBuilder older = previousFrameEvents;
            previousFrameEvents = frameEvents;
            previousFrameEventFrame = frameEventFrame;
            frameEvents = older;
            frameEvents.Length = 0;
            frameEventCount = 0;
            frameEventFrame = now;
        }

        // Steady per-frame cost, as opposed to hitches: every unpaused frame since the
        // last heartbeat feeds the [Perf] line, which is what "it lags with three
        // players" needs to become a number and a suspect. A time window: the old ring
        // of 600 frames held a quarter of the 10 s at 240 fps.
        private readonly FrameTimeWindow perfWindow = new FrameTimeWindow(8192);
        private int perfPausedFrames;
        private float perfPausedMaxSeconds;
        private float perfWindowStart;
        /// <summary>Median frame of the last [Perf] window: what a hitch is measured against, and what FramesFor converts with.</summary>
        private static float typicalFrameSeconds = 1f / 60f;

        // Collections and heap sampled at the start of every frame (RainWorld.Update), so
        // a hitch line carries the collections of the frame it measures. Counted from the
        // end of one frame's drawing to the next they fell on the frame after, and the
        // first line of a session showed every collection since startup (190).
        private int gcAtFrameStart = -1;
        private int gcDuringLastFrame;
        private int perfGcCollections;
        private readonly AllocationMeter allocationMeter = new AllocationMeter();

        private void NoteFrameStart()
        {
            int collections = GC.CollectionCount(0);
            gcDuringLastFrame = gcAtFrameStart < 0 ? 0 : collections - gcAtFrameStart;
            gcAtFrameStart = collections;
            perfGcCollections += gcDuringLastFrame;
            allocationMeter.Note(GC.GetTotalMemory(false), gcDuringLastFrame > 0);
        }

        private void ResetPerfWindow()
        {
            perfWindow.Clear();
            perfPausedFrames = 0;
            perfPausedMaxSeconds = 0f;
            perfGcCollections = 0;
            allocationMeter.Reset();
            perfWindowStart = Time.realtimeSinceStartup;
            perfUpdateMsSum = 0; perfModTickMsSum = 0; perfGrafMsSum = 0; perfTickSum = 0; perfFrameSum = 0;
            perfRainWorldMsSum = 0; perfFutileMsSum = 0; perfRenderMsSum = 0;
            perfReplayMsSum = 0; perfReplaySum = 0;
        }

        // Where a frame's CPU time went. Update ticks and GrafUpdate are timed around
        // vanilla's own methods; what is left of the frame is rendering, the GPU and
        // the wait for vsync. Time.unscaledDeltaTime measures the frame *before* the
        // one that is ending, so the hitch line reports the previous frame's phases.
        internal static readonly System.Diagnostics.Stopwatch phaseWatch = System.Diagnostics.Stopwatch.StartNew();
        internal static double frameUpdateMs, frameModTickMs, frameGrafMs;
        // The rest of the main thread: all of RainWorld.Update (ticks, drawing, menus, side
        // processes such as pause menus), Futile's mesh rebuild in LateUpdate, and each world
        // camera from cull to OnPostRender. What is still missing from a slow frame after
        // these is the GPU, the present and other plugins. These three are read one frame
        // late by construction, which is the frame Time.unscaledDeltaTime describes.
        internal static double frameRainWorldMs, frameFutileMs, frameRenderMs;
        private double perfRainWorldMsSum, perfFutileMsSum, perfRenderMsSum;
        // Shader-state replays (CameraListener.OnPreRender): world cameras, HUD cameras,
        // the overlay camera, snow blits. Timed to decide whether skipping unchanged
        // values would pay (audit of 2026-09-22 estimated 0.1-0.35 ms a frame).
        internal static double frameReplayMs;
        internal static int frameReplays;
        private double perfReplayMsSum;
        private int perfReplaySum;
        internal static int frameTicks;
        private double lastFrameUpdateMs, lastFrameModTickMs, lastFrameGrafMs;
        private int lastFrameTicks;
        private double perfUpdateMsSum, perfModTickMsSum, perfGrafMsSum;
        private int perfTickSum, perfFrameSum;
        private bool inputLoggedAfterStart;

        private void NoteFrameTime()
        {
            double hitchUpdateMs = lastFrameUpdateMs, hitchModTickMs = lastFrameModTickMs, hitchGrafMs = lastFrameGrafMs;
            int hitchTicks = lastFrameTicks;
            lastFrameUpdateMs = frameUpdateMs; lastFrameModTickMs = frameModTickMs; lastFrameGrafMs = frameGrafMs;
            lastFrameTicks = frameTicks;
            perfUpdateMsSum += frameUpdateMs; perfModTickMsSum += frameModTickMs; perfGrafMsSum += frameGrafMs;
            perfTickSum += frameTicks; perfFrameSum++;
            frameUpdateMs = 0; frameModTickMs = 0; frameGrafMs = 0; frameTicks = 0;
            double hitchRainWorldMs = frameRainWorldMs, hitchFutileMs = frameFutileMs, hitchRenderMs = frameRenderMs;
            perfRainWorldMsSum += frameRainWorldMs; perfFutileMsSum += frameFutileMs; perfRenderMsSum += frameRenderMs;
            frameFutileMs = 0; frameRenderMs = 0;
            perfReplayMsSum += frameReplayMs; perfReplaySum += frameReplays;
            frameReplayMs = 0; frameReplays = 0;
            float seconds = Time.unscaledDeltaTime;
            NoteRotationFrame(seconds);
            bool paused = (rainworldGameObject?.processManager?.currentMainLoop as RainWorldGame)?.GamePaused == true;
            if (paused)
            {
                perfPausedFrames++;
                if (seconds > perfPausedMaxSeconds) perfPausedMaxSeconds = seconds;
            }
            else perfWindow.Add(seconds);
            if (!FrameTimeWindow.IsHitch(seconds, typicalFrameSeconds)) return;
            if (Time.frameCount - lastHitchLogFrame < HitchLogCooldownFrames)
            {
                suppressedHitches++;
                return;
            }
            RollFrameEvents();
            string events = previousFrameEventFrame == Time.frameCount - 1 ? previousFrameEvents.ToString() : "";
            Logger.LogInfo($"[FrameHitch] frame={Time.frameCount} ms={seconds * 1000f:0} typicalMs={typicalFrameSeconds * 1000f:0.0} ticks={hitchTicks} updateMs={hitchUpdateMs:0} modTickMs={hitchModTickMs:0} grafMs={hitchGrafMs:0} rainWorldMs={hitchRainWorldMs:0} futileMs={hitchFutileMs:0} renderMs={hitchRenderMs:0} paused={paused} gcCollections={gcDuringLastFrame} suppressedSinceLast={suppressedHitches} sharedLevelTextures={sharedLevelTextureCopies} events=[{events}]");
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
            // one behind the sleep lockout) was never shown. Numbers in the message
            // (an index, a size, an instance id) are not a new error: each value was a
            // bucket of its own, logged at once and kept for the whole session.
            string key = WithoutNumbers(condition);
            if (!string.IsNullOrEmpty(stackTrace))
            {
                int first = stackTrace.IndexOf('\n');
                int second = first < 0 ? -1 : stackTrace.IndexOf('\n', first + 1);
                key += "|" + (second < 0 ? stackTrace : stackTrace.Substring(0, second)).Trim();
            }
            // Past the cap, new kinds share one bucket per type, still rate limited.
            if (unityErrorCounts.Count >= UnityErrorKeyLimit && !unityErrorCounts.ContainsKey(key))
                key = "(further distinct errors)|" + type;
            int count;
            unityErrorCounts.TryGetValue(key, out count);
            unityErrorCounts[key] = ++count;
            lastUnityError = key;
            int last;
            bool seen = unityErrorLastLogged.TryGetValue(key, out last);
            if (seen && Time.frameCount - last < FramesFor(10f)) return;
            unityErrorLastLogged[key] = Time.frameCount;
            string trace = string.IsNullOrEmpty(stackTrace) ? "" : "\n" + stackTrace.TrimEnd();
            sLogger?.LogError($"[UnityLog] frame={Time.frameCount} {type} (x{count}): {condition}{trace}");
        }

        private const int UnityErrorKeyLimit = 256;
        private static readonly System.Text.StringBuilder unityErrorKey = new System.Text.StringBuilder(256);

        /// <summary>The text with every run of digits replaced by one '#'. Main thread only (logMessageReceived).</summary>
        private static string WithoutNumbers(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            unityErrorKey.Length = 0;
            bool inNumber = false;
            foreach (char c in text)
            {
                bool digit = c >= '0' && c <= '9';
                if (!digit) unityErrorKey.Append(c);
                else if (!inNumber) unityErrorKey.Append('#');
                inNumber = digit;
            }
            return unityErrorKey.ToString();
        }

        /// <summary>
        /// The mod's own code that runs after a vanilla call must never take the
        /// game's frame down with it. Log the error, rate limited per call site.
        /// </summary>
        private static readonly Dictionary<string, int> hookErrorLastLogged = new Dictionary<string, int>();

        internal static void LogHookError(string site, Exception error)
        {
            int last;
            if (hookErrorLastLogged.TryGetValue(site, out last) && Time.frameCount - last < FramesFor(10f)) return;
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

        private int drawLoopHealthySince = -1;

        private void DetectDrawStall(RainWorldGame game)
        {
            if (game?.cameras == null || game.cameras.Length == 0 || game.GamePaused) { drawStallSince = -1; return; }
            // The camera longest without a completed draw while its Update still runs. Only
            // camera 0 was watched: on dual displays camera 1's draw threw every frame for
            // three minutes (a Watcher RippleTree, log of 2026-09-22) and nothing said so.
            int number = -1, drawAge = -1;
            for (int i = 0; i < game.cameras.Length; i++)
            {
                int n = game.cameras[i]?.cameraNumber ?? -1;
                if (n < 0 || n >= lastRoomCameraDrawFrames.Length || lastRoomCameraDrawFrames[n] < 0 ||
                    FrameAge(lastRoomCameraUpdateFrames[n]) > 2) continue;
                int age = FrameAge(lastRoomCameraDrawFrames[n]);
                if (age > drawAge) { drawAge = age; number = n; }
            }
            if (number < 0 || drawAge < FramesFor(0.5f))
            {
                drawStallSince = -1;
                // Pass-through is a diagnosis mode, not a state to stay in: once the
                // draw loop has completed for 600 frames, put the mod's draw path
                // back. A false alarm (a long pause, before paused draws counted)
                // used to leave shader capture and HUD routing off all session.
                if (drawPathSafeMode)
                {
                    if (drawLoopHealthySince < 0) drawLoopHealthySince = Time.frameCount;
                    else if (Time.frameCount - drawLoopHealthySince > FramesFor(10f))
                    {
                        drawPathSafeMode = false;
                        drawLoopHealthySince = -1;
                        Logger.LogInfo($"[CameraHealth] frame={Time.frameCount} draw loop completed for 10 s; the mod's draw-path code is active again");
                    }
                }
                return;
            }
            drawLoopHealthySince = -1;
            if (drawStallSince < 0) drawStallSince = Time.frameCount;
            if (Time.frameCount - drawStallLoggedFrame < FramesFor(5f)) return;
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
                long lastTick = DateTime.UtcNow.Ticks;
                while (true)
                {
                    System.Threading.Thread.Sleep(1000);
                    // This thread sleeps one second. If that took several, every
                    // thread stood still: the process was paged out, the GPU driver
                    // was resetting, or the OS suspended the game. That is not a
                    // main-thread hang and the game loop's marker says nothing about
                    // it; the twelfth log had 26 s, 34 s and 69 s frames of this kind
                    // with no [Hang] line, and p95 frame time still at 4 ms.
                    long now = DateTime.UtcNow.Ticks;
                    double slept = (now - lastTick) / 10000000.0;
                    lastTick = now;
                    if (slept >= 3.0)
                        sLogger?.LogWarning($"[Hang] the whole process stood still for {slept:0.0} s (every thread, not the game loop): system paging, a GPU driver reset or the window being suspended; last marker={HangMarker}");
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
        private readonly List<string> menuCorrections = new List<string>(4);

        internal void EnforceMenuCameraState()
        {
            ProcessManager manager = rainworldGameObject?.processManager;
            MainLoopProcess process = manager?.currentMainLoop;
            if (process == null || process is RainWorldGame || fcameras[0] == null || Futile.screen?.renderTexture == null) return;
            var corrections = RestoreMenuCameras(null);
            // A menu over a paused game (the Watcher's fast-travel screen) arrives without
            // a shutdown, so nothing had pointed the second display at the main screen:
            // it kept the game's last frame while the menu was on display 1.
            if (corrections.Count > 0 && dualDisplays && DualDisplaySupported()) MirrorSecondaryDisplays();
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
        /// its mask, everything else off. Returns what had to change, in a list that is
        /// reused (this runs every frame in every menu): read it before the next call.
        /// </summary>
        internal List<string> RestoreMenuCameras(string reason)
        {
            List<string> corrections = menuCorrections;
            corrections.Clear();
            if (fcameras[0] == null || Futile.screen?.renderTexture == null) return corrections;
            // SetSplitMode(NoSplit) at shutdown makes only the camera that was
            // rendering direct. When that was not camera 0 (its player dead, another
            // the sole survivor) camera 0's listener stayed in split mode and its
            // OnPostRender copied the stale split texture over Futile's screen every
            // frame: the sleep screen ran underneath a frozen last frame of the game.
            CameraListener primary = cameraListeners.Length > 0 ? cameraListeners[0] : null;
            if (primary != null && (!primary.direct || primary.dynamicCompositing))
            {
                primary.dynamicCompositing = false;
                primary.direct = true;
                corrections.Add("camera 0 listener was still split/compositing");
            }
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
            // The decoded level images (about 54 MB) help the next cycle, which usually
            // starts in the same shelter; back at the main menu nothing is coming.
            if (ID == ProcessManager.ProcessID.MainMenu) ClearLevelTextureCache();
        }

        /// <summary>
        /// A paused game is the main process again. The Watcher's ripple-egg warp runs
        /// the fast-travel screen as the main process while the game waits behind it
        /// (RainWorldGame.PauseProcess, ProcessManager.pendingProcess). That menu drew
        /// with camera 0 alone, as every menu must (EnforceMenuCameraState), and nothing
        /// of the game drew, rendered or composited for the whole visit, so every "last
        /// done" frame the health checks measure from was from before it. The first scan
        /// after the menu found every camera, HUD camera and the compositor stalled at
        /// once and rebuilt all their textures (a hitch, a screen of false warnings and a
        /// strike towards the Classic fallback), the draw check switched the mod's draw
        /// path to pass-through, and Classic and dual displays kept the menu's one camera
        /// until those recoveries enabled theirs. Measure from now, and put the split
        /// back before the first frame renders.
        /// </summary>
        private void RainWorldGame_ResumeProcess(On.RainWorldGame.orig_ResumeProcess orig, RainWorldGame self)
        {
            orig(self);
            try
            {
                for (int i = 0; i < lastRoomCameraDrawFrames.Length; i++)
                {
                    lastRoomCameraDrawFrames[i] = -1;
                    roomMismatchSinceFrames[i] = -1;
                    lastCameraStateSignatures[i] = long.MinValue;
                }
                drawStallSince = -1;
                foreach (CameraListener listener in cameraListeners) listener?.MarkRenderingExpected(false);
                compositorExpectedSinceFrame = -1;
                for (int i = 0; i < hudExpectedSinceFrames.Length; i++) hudExpectedSinceFrames[i] = -1;
                globalHudExpectedSinceFrame = -1;
                if (self.cameras == null || self.cameras.Length < 2) return;
                bool dynamicPipeline = dynamicStyle && !dualDisplays;
                Logger.LogInfo($"[CameraMode] frame={Time.frameCount} game resumed after {self.manager?.oldProcess?.ID?.ToString() ?? "a menu"}; health checks restart and the {(dynamicPipeline ? "dynamic" : "classic")} split is re-applied");
                if (dynamicPipeline)
                {
                    // The layout is the one from before the menu; only the cameras went.
                    if (dynamicLayout != null) ApplyDynamicCameraRendering(self);
                }
                else SetSplitMode(CurrentSplitMode, self, "game resumed after a menu");
            }
            catch (Exception error) { LogHookError("RainWorldGame_ResumeProcess", error); }
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
            lastCameraHeartbeatTime = -1000f;
            heartbeatsSinceTextureCensus = 1000;
            ResetFrameRenderingDecision();
            ResetPerfWindow();
            typicalFrameSeconds = 1f / 60f;
            renderedCameraNumbers.Clear();
            for (int i = 0; i < lastRoomCameraUpdateFrames.Length; i++)
            {
                lastRoomCameraUpdateFrames[i] = -1;
                lastRoomCameraDrawFrames[i] = -1;
                roomMismatchSinceFrames[i] = -1;
                cameraRoomResyncInProgress[i] = false;
                lastCameraStateSignatures[i] = long.MinValue;
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

        /// <summary>
        /// While the game is paused vanilla draws with PausedDrawUpdate instead of
        /// DrawUpdate. Counting only DrawUpdate made any pause longer than 30 frames
        /// read as "camera 0 has not drawn while Update runs" on the first tick after
        /// unpausing (Update precedes GrafUpdate in RawUpdate), which switched the
        /// mod's draw path to pass-through for the rest of the session (frame 123019
        /// of the tenth log, after a four-second pause).
        /// </summary>
        public void RoomCamera_PausedDrawUpdate(On.RoomCamera.orig_PausedDrawUpdate orig, RoomCamera self,
            float timeStacker, float timeSpeed)
        {
            orig(self, timeStacker, timeSpeed);
            NoteRoomCameraDrawn(self);
        }

        private void NoteRoomCameraMoved(RoomCamera camera, string source)
        {
            if (!ValidCameraNumber(camera)) return;
            Logger.LogInfo($"[CameraMove] frame={Time.frameCount} source={source} cam={camera.cameraNumber} room={RoomName(camera.room)} loading={RoomName(camera.loadingRoom)} position={camera.currentCameraPosition} follow={PlayerNumber(camera.followAbstractCreature)}");
            NoteFrameEvent(source + " cam=" + camera.cameraNumber);
            lastCameraStateSignatures[camera.cameraNumber] = long.MinValue;
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
            DecideFrameRendering(game);
            LogCameraSnapshot(game, "state change", false);
            if (Time.realtimeSinceStartup - lastCameraHeartbeatTime >= 10f)
            {
                lastCameraHeartbeatTime = Time.realtimeSinceStartup;
                LogCameraSnapshot(game, "periodic heartbeat", true);
                LogPerformance(game);
            }

            // A copy: recovery below re-enables cameras, which must not disturb the loop.
            int monitored = 0;
            for (int n = 0; n < renderedCameraNumbers.Count && monitored < healthScanCameras.Length; n++)
                healthScanCameras[monitored++] = renderedCameraNumbers[n];
            for (int n = 0; n < monitored; n++)
            {
                int i = healthScanCameras[n];
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
                lastCameraStateSignatures[i] = long.MinValue;
            }

            if (dynamicActive && dynamicCompositorCamera != null && dynamicCompositorCamera.enabled &&
                dynamicCompositor != null && compositorExpectedSinceFrame >= 0 &&
                Time.frameCount - Math.Max(compositorExpectedSinceFrame,
                    dynamicCompositor.lastCompositeFrame) > RenderStallFrames &&
                OtherCamerasRenderedSince(Math.Max(compositorExpectedSinceFrame, dynamicCompositor.lastCompositeFrame)))
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

        private readonly int[] healthScanCameras = new int[4];

        /// <summary>
        /// Whether a world camera finished a render after <paramref name="frame"/>. The
        /// compositor draws after them, so it has stalled on its own only while they
        /// still render; with nothing rendering at all (a minimized window, a driver
        /// reset) there was nothing to composite, and two such stretches in a row
        /// switched the session to Classic for good.
        /// </summary>
        private bool OtherCamerasRenderedSince(int frame)
        {
            for (int n = 0; n < renderedCameraNumbers.Count; n++)
            {
                int i = renderedCameraNumbers[n];
                if (i >= 0 && i < cameraListeners.Length && cameraListeners[i] != null &&
                    cameraListeners[i].lastPostRenderFrame > frame) return true;
            }
            return false;
        }

        private void LogCameraSnapshot(RainWorldGame game, string reason, bool force)
        {
            if (game?.cameras == null) return;
            for (int i = 0; i < game.cameras.Length && i < lastCameraStateSignatures.Length; i++)
            {
                RoomCamera roomCamera = game.cameras[i];
                Camera unityCamera = i < fcameras.Length ? fcameras[i] : null;
                CameraListener listener = i < cameraListeners.Length ? cameraListeners[i] : null;
                Creature followedCreature = roomCamera?.followAbstractCreature?.realizedCreature;
                bool turns = frameRenderCamera >= 0 && renderedCameraNumbers.Contains(i);
                // Compared every scan, so a number: the text below is built only when
                // something changed (it was ~1 KB of garbage per camera per scan).
                long signature = CameraStateSignature(game, roomCamera, followedCreature, unityCamera, listener, turns, cameraZoomed[i]);
                if (!force && lastCameraStateSignatures[i] == signature) continue;
                lastCameraStateSignatures[i] = signature;
                string realizedRoom = RoomName(followedCreature?.room);
                string key = $"mode={CurrentSplitMode}|world={game.world?.name}|room={RoomName(roomCamera?.room)}|loading={RoomName(roomCamera?.loadingRoom)}|position={roomCamera?.currentCameraPosition}|follow={PlayerNumber(roomCamera?.followAbstractCreature)}|realizedRoom={realizedRoom}|enabled={(turns ? "turns" : unityCamera?.enabled.ToString())}|direct={listener?.direct}|target={RenderTargetState(listener)}|zoom={cameraZoomed[i]}";
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

        // ---- Auto camera rendering ---------------------------------------------
        // Frame times measured only while the views take turns, since that is the one
        // thing Auto has to judge: what each view really gets. The median, because a
        // region load in the window is not a frame rate (the log of 2026-09-19 left
        // the rotation 120 frames into the game on "fps=28", which was one 1.8 s
        // load; and again after a pause).
        private const float RotationMinPerViewFps = 38f; // the game ticks at 40
        private readonly float[] rotationSamples = new float[240];
        private readonly float[] rotationSorted = new float[240];
        private int rotationSampleCount, rotationSampleIndex, rotationSampleViews;
        private float nextRenderingDecision, nextRotationAttempt, rotationBackoff = 30f, rotationSince;
        private string lastRenderingWhy;

        // What vsync really held the game to in the last failed rotation attempt, and under
        // which setup. The refresh rate Unity reports is not always the pace: on dual displays
        // (log of 2026-09-22) the game ran at 60 fps under vsync and Auto still tried the
        // rotation four times in four minutes, each try two seconds at half speed per view,
        // while the same machine on one display read 60 Hz and never tried. Static: the
        // display setup outlives a session.
        private static float vsyncMeasuredFps;
        private static int vsyncMeasuredCount = -1, vsyncMeasuredRefresh = -1;
        private static bool vsyncMeasuredDual;

        private static bool VsyncMeasurementApplies()
        {
            return vsyncMeasuredFps > 0f && vsyncMeasuredCount == QualitySettings.vSyncCount &&
                vsyncMeasuredRefresh == Screen.currentResolution.refreshRate && vsyncMeasuredDual == dualDisplays;
        }

        private void ResetFrameRenderingDecision()
        {
            rotationSampleCount = 0; rotationSampleIndex = 0; rotationSampleViews = 0;
            nextRenderingDecision = 0f; nextRotationAttempt = 0f; rotationBackoff = 30f;
            lastRenderingWhy = null;
        }

        private void NoteRotationFrame(float seconds)
        {
            // Not while paused: the log of 2026-09-19 has a pause whose every frame took
            // 67 ms for reasons that had nothing to do with taking turns, and Auto left
            // the rotation on the strength of it the moment the game resumed.
            if ((rainworldGameObject?.processManager?.currentMainLoop as RainWorldGame)?.GamePaused == true) return;
            int views = frameRenderCamera >= 0 ? renderedCameraNumbers.Count : 0;
            if (views != rotationSampleViews)
            {
                rotationSampleViews = views;
                rotationSampleCount = 0;
                rotationSampleIndex = 0;
            }
            if (views <= 1) return;
            rotationSamples[rotationSampleIndex] = seconds;
            rotationSampleIndex = (rotationSampleIndex + 1) % rotationSamples.Length;
            if (rotationSampleCount < rotationSamples.Length) rotationSampleCount++;
        }

        private float RotationMedianFps()
        {
            if (rotationSampleCount == 0) return 0f;
            Array.Copy(rotationSamples, rotationSorted, rotationSampleCount);
            Array.Sort(rotationSorted, 0, rotationSampleCount);
            return 1f / Mathf.Max(0.0001f, rotationSorted[rotationSampleCount / 2]);
        }

        /// <summary>
        /// Whether the views take turns (see SelectFrameCamera). Taking turns makes
        /// every named grab right on every view and costs each view its share of the
        /// frames; ApplyRotationFrameCap gives the frames back by raising the limit.
        /// Auto keeps the rotation while each view really gets 38 frames a second,
        /// judged only from frames in which the rotation ran. When it does not (a
        /// slow machine, or vsync pacing the game at 60) every camera renders every
        /// frame, and the rotation is tried again later, 30 s doubling to 5 min. It
        /// cannot be judged from outside: under the normal limit the frame rate says
        /// nothing about what the machine could do with the limit raised.
        /// </summary>
        private void DecideFrameRendering(RainWorldGame game)
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextRenderingDecision) return;
            nextRenderingDecision = now + 1f;
            bool before = alternateFrames;
            bool canRotate = dynamicActive || dualDisplays;
            bool wanted;
            string why;
            if (cameraRenderingMode == "Alternate frames") { wanted = true; why = cameraRenderingMode; }
            else if (cameraRenderingMode == "Every frame") { wanted = false; why = cameraRenderingMode; }
            else if (alternateFrames)
            {
                // Merged, one survivor, or too few rotating frames yet: nothing to judge.
                if (frameRenderCamera < 0 || rotationSampleViews <= 1 || rotationSampleCount < 90) return;
                float fps = RotationMedianFps();
                float perView = fps / rotationSampleViews;
                if (perView >= RotationMinPerViewFps)
                {
                    if (now - rotationSince > 60f) rotationBackoff = 30f;
                    return;
                }
                wanted = false;
                bool vsync = QualitySettings.vSyncCount > 0;
                if (vsync)
                {
                    vsyncMeasuredFps = fps;
                    vsyncMeasuredCount = QualitySettings.vSyncCount;
                    vsyncMeasuredRefresh = Screen.currentResolution.refreshRate;
                    vsyncMeasuredDual = dualDisplays;
                }
                why = $"Auto: {fps:0} fps while {rotationSampleViews} views took turns is {perView:0} each, under {RotationMinPerViewFps:0}" +
                    (vsync ? $"; vsync holds the game at {fps:0} fps, so Auto stops trying while vsync is on (turn it off to let the frame limit rise)"
                        : $"; next try in {rotationBackoff:0} s");
                nextRotationAttempt = now + rotationBackoff;
                rotationBackoff = Mathf.Min(rotationBackoff * 2f, 300f);
            }
            else
            {
                if (!canRotate || renderedCameraNumbers.Count <= 1 || now < nextRotationAttempt) return;
                int views = renderedCameraNumbers.Count;
                if (QualitySettings.vSyncCount > 0)
                {
                    // Under vsync the outcome is known without trying. A failed attempt under the
                    // same setup measured the real pace, which the reported refresh rate is not always.
                    float paced = Screen.currentResolution.refreshRate / (float)QualitySettings.vSyncCount;
                    if (VsyncMeasurementApplies()) paced = paced > 0f ? Mathf.Min(paced, vsyncMeasuredFps) : vsyncMeasuredFps;
                    if (paced > 0f && paced / views < RotationMinPerViewFps)
                    {
                        string vsyncWhy = $"Auto: vsync paces the game at {paced:0} fps, {views} views taking turns would get {paced / views:0} each; every camera renders every frame. " +
                            "Turn vsync off in the game's options to get correct grab effects on every view at full speed";
                        if (vsyncWhy != lastRenderingWhy) Logger.LogInfo($"[CameraLayout] frame={Time.frameCount} camera rendering: {vsyncWhy}");
                        lastRenderingWhy = vsyncWhy;
                        nextRotationAttempt = now + 30f;
                        return;
                    }
                }
                wanted = true;
                why = "Auto: trying one camera per frame with the frame limit raised to match";
            }
            alternateFrames = wanted && canRotate;
            if (alternateFrames == before) return;
            if (alternateFrames) rotationSince = now;
            rotationSampleCount = 0; rotationSampleIndex = 0;
            // The dynamic-level-element pass decides at build time whether to emulate
            // its grab per camera; rebuild it when the answer changes.
            RebuildDynamicElementPasses(game);
            lastRenderingWhy = why;
            Logger.LogInfo($"[CameraLayout] frame={Time.frameCount} camera rendering: {(alternateFrames ? "one camera per frame" : "every camera every frame")} ({why}); frame limit per view={FrameCapPerView} vsync={QualitySettings.vSyncCount}");
        }

        private void RainWorld_Update(On.RainWorld.orig_Update orig, RainWorld self)
        {
            NoteFrameStart();
            long start = phaseWatch.ElapsedTicks;
            try { orig(self); }
            finally { frameRainWorldMs = (phaseWatch.ElapsedTicks - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        }

        private void Futile_LateUpdate(On.Futile.orig_LateUpdate orig, Futile self)
        {
            long start = phaseWatch.ElapsedTicks;
            try { orig(self); }
            finally { frameFutileMs += (phaseWatch.ElapsedTicks - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        }

        /// <summary>
        /// Vanilla prepares a room on the main thread in slices (RoomPreparer.Update)
        /// while its first visitor waits in the pipe. In single player the screen is
        /// still then; with a second player still playing, every slow slice is a
        /// visible hitch. Name them so [FrameHitch] can tell room loading from drawing.
        /// </summary>
        private void RoomPreparer_Update(On.RoomPreparer.orig_Update orig, RoomPreparer self)
        {
            long start = phaseWatch.ElapsedTicks;
            orig(self);
            double ms = (phaseWatch.ElapsedTicks - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms >= 8.0) NoteFrameEvent($"prepare {self.room?.abstractRoom?.name ?? "?"} {ms:0}ms");
        }

        /// <summary>
        /// The pause-menu pointer is reported missing in dual display mode only, and
        /// nothing in the code explains it. Record what decides it: whether the game
        /// is in mouse mode, where Unity and Futile think the pointer is (Unity
        /// reports it relative to the display under it once two are active), where the
        /// cursor container hangs, and what each display's camera looks at.
        /// </summary>
        internal static void LogPauseDiagnostics(Menu.PauseMenu menu, string reason)
        {
            try
            {
                var cameras = new List<string>();
                for (int i = 0; i < fcameras.Length; i++)
                    if (fcameras[i] != null && fcameras[i].enabled)
                        cameras.Add($"cam{i}(mask={fcameras[i].cullingMask} pos=({fcameras[i].transform.position.x:0},{fcameras[i].transform.position.y:0}) display={(cameraListeners[i]?.display == Display.main ? 0 : 1)})");
                FContainer cursor = menu?.cursorContainer;
                Vector3 relative = Display.RelativeMouseAt(Input.mousePosition);
                sLogger?.LogInfo($"[Pause] frame={Time.frameCount} {reason}; dualDisplays={dualDisplays}; mouseMode={menu?.manager?.menuesMouseMode}; " +
                    $"showCursor={menu?.ShowCursor}; unityMouse=({Input.mousePosition.x:0},{Input.mousePosition.y:0}); relativeMouse=({relative.x:0},{relative.y:0},display {relative.z:0}); " +
                    $"futileMouse=({Futile.mousePosition.x:0},{Futile.mousePosition.y:0}); screen={Screen.width}x{Screen.height}; futileScreen={Futile.screen?.pixelWidth}x{Futile.screen?.pixelHeight}; " +
                    $"cursorParent={(cursor?.container == null ? "none" : cursor.container == Futile.stage ? "root stage" : "other")} cursorChildren={cursor?.GetChildCount()}; " +
                    $"menuPos=({menu?.container?.x:0},{menu?.container?.y:0}); pauseMenus={menu?.manager?.sideProcesses?.FindAll(p => p is Menu.PauseMenu).Count}; rendered=[{string.Join(",", renderedCameraNumbers)}]; {string.Join(" ", cameras)}; stages=[{StageOrderForLog()}]");
            }
            catch (Exception error) { LogHookError("LogPauseDiagnostics", error); }
        }

        // ---- Pause overlay self check ---------------------------------------------
        // Whether the pause menu is drawn over the world is a purely visual fact that
        // no game state records, and in dual display mode it has been reported wrong
        // twice. It can still be measured. Vanilla's pause menu lays a black sprite
        // over the whole screen that fades in to 25% within half a second, and the
        // world stands still while paused: a view the menu is drawn over gets a
        // quarter darker, a view whose menu is missing or behind the world does not
        // change. Classic and dual only; Dynamic draws its one menu over the finished
        // composite, not into a camera's frame.
        private static readonly float[] pauseLuminanceBefore = { -1f, -1f, -1f, -1f };
        private static readonly float[] pauseCheckAt = { -1f, -1f, -1f, -1f };

        private const int SmallReadWidth = 64, SmallReadHeight = 36;
        private static RenderTexture smallReadTarget;
        private static Texture2D smallReadPixels;

        /// <summary>Nearest-neighbour 64x36 read-back of a texture. A GPU stall; only for one-off diagnostics.</summary>
        private static Color32[] ReadSmall(Texture source)
        {
            if (smallReadTarget == null)
            {
                smallReadTarget = new RenderTexture(SmallReadWidth, SmallReadHeight, 0, RenderTextureFormat.ARGB32)
                {
                    name = "SplitScreen diagnostic read-back",
                    filterMode = FilterMode.Point
                };
                smallReadTarget.Create();
                smallReadPixels = new Texture2D(SmallReadWidth, SmallReadHeight, TextureFormat.RGBA32, false) { name = "SplitScreen diagnostic pixels" };
            }
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, smallReadTarget);
                RenderTexture.active = smallReadTarget;
                smallReadPixels.ReadPixels(new Rect(0f, 0f, SmallReadWidth, SmallReadHeight), 0, 0, false);
            }
            finally { RenderTexture.active = previous; }
            return smallReadPixels.GetPixels32();
        }

        private static float MeanLuminance(Color32[] pixels)
        {
            if (pixels == null || pixels.Length == 0) return 0f;
            double sum = 0.0;
            foreach (Color32 c in pixels) sum += 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
            return (float)(sum / (255.0 * pixels.Length));
        }

        /// <summary>Remember how bright each rendered view was just before the pause menu existed.</summary>
        internal static void ArmPauseOverlayCheck()
        {
            try
            {
                for (int i = 0; i < 4; i++) { pauseCheckAt[i] = -1f; pauseLuminanceBefore[i] = -1f; }
                foreach (int number in renderedCameraNumbers)
                {
                    if (number < 0 || number >= 4) continue;
                    CameraListener listener = cameraListeners[number];
                    if (listener?.lastFrame == null || !listener.lastFrame.IsCreated()) continue;
                    pauseLuminanceBefore[number] = MeanLuminance(ReadSmall(listener.lastFrame));
                    pauseCheckAt[number] = Time.realtimeSinceStartup + 0.8f;
                }
            }
            catch (Exception error) { LogHookError("ArmPauseOverlayCheck", error); }
        }

        /// <summary>Called from a world camera's OnPostRender, after its frame was copied to lastFrame.</summary>
        internal static void PauseOverlayTick(CameraListener listener)
        {
            try
            {
                int number = Array.IndexOf(cameraListeners, listener);
                if (number < 0 || number >= 4 || pauseCheckAt[number] < 0f ||
                    Time.realtimeSinceStartup < pauseCheckAt[number]) return;
                pauseCheckAt[number] = -1f;
                RainWorldGame game = rainworldGameObject?.processManager?.currentMainLoop as RainWorldGame;
                if (game?.pauseMenu == null || listener.lastFrame == null)
                {
                    sLogger?.LogInfo($"[Pause] frame={Time.frameCount} overlay check cam={number}: the menu closed before it could be measured");
                    return;
                }
                float before = pauseLuminanceBefore[number];
                float after = MeanLuminance(ReadSmall(listener.lastFrame));
                string verdict = before < 0.03f ? "view too dark to tell"
                    : after <= before * 0.9f ? "the menu's dark overlay IS drawn over this view"
                    : "NOT darkened: the pause menu is missing from this view or drawn behind the world";
                sLogger?.LogInfo($"[Pause] frame={Time.frameCount} overlay check cam={number} display={(listener.display == Display.main ? 0 : 1)}: " +
                    $"luminance before={before:0.000} after={after:0.000} ratio={(before > 0f ? after / before : 0f):0.00} -> {verdict}; " +
                    $"render queues by stage (higher draws later)=[{StageQueuesForLog()}]");
            }
            catch (Exception error) { LogHookError("PauseOverlayTick", error); }
        }

        /// <summary>The render queue range of every stage's live layers: what Unity actually sorts one camera's draws by.</summary>
        internal static string StageQueuesForLog()
        {
            var parts = new List<string>();
            for (int i = 0; i < Futile.GetStageCount(); i++)
            {
                FStage stage = Futile.GetStageAt(i);
                List<FFacetRenderLayer> layers = stage?.renderer?._liveLayers;
                if (layers == null || layers.Count == 0) continue;
                int low = int.MaxValue, high = int.MinValue;
                foreach (FFacetRenderLayer layer in layers)
                {
                    int queue = layer?._material != null ? layer._material.renderQueue : -1;
                    if (queue < low) low = queue;
                    if (queue > high) high = queue;
                }
                parts.Add($"{(stage == Futile.stage ? "root" : stage.name)}={low}..{high}");
            }
            return string.Join(", ", parts);
        }

        /// <summary>
        /// Which devices each player's control setup owns, logged at game start. A
        /// player whose Rewired player has no controller cannot move; player 1 with the
        /// "any" preference owns the keyboard and every joystick at once.
        /// </summary>
        internal static void LogInputSetups(RainWorldGame game, string reason)
        {
            try
            {
                global::Options options = game?.rainWorld?.options;
                if (options?.controls == null) return;
                int players = game.session?.Players?.Count ?? 0;
                var parts = new List<string>();
                for (int i = 0; i < options.controls.Length && i < 4; i++)
                {
                    global::Options.ControlSetup setup = options.controls[i];
                    if (setup == null) { parts.Add($"p{i}=null"); continue; }
                    string devices = "none";
                    if (setup.player != null)
                    {
                        var names = new List<string>();
                        foreach (Rewired.Controller controller in setup.player.controllers.Controllers)
                            if (controller.type != Rewired.ControllerType.Mouse) names.Add(controller.type + ":" + controller.name);
                        if (names.Count > 0) devices = string.Join("|", names);
                    }
                    parts.Add($"p{i}(active={setup.GetActive()} preference={setup.GetControlPreference()} gamePad={setup.gamePadNumber} " +
                        $"guid={(string.IsNullOrEmpty(setup.gamePadGuid) ? "-" : setup.gamePadGuid)} preset={setup.GetActivePreset()} devices=[{devices}])");
                }
                sLogger?.LogInfo($"[Input] frame={Time.frameCount} reason={reason}; players={players}; selfSufficientCoop={selfSufficientCoop}; " +
                    $"jolly={ModManager.JollyCoop}; multiplayerContext={game.rainWorld?.processManager?.IsGameInMultiplayerContext()}; dualDisplays={dualDisplays}; {string.Join("; ", parts)}");
            }
            catch (Exception error) { LogHookError("LogInputSetups", error); }
        }

        /// <summary>
        /// Self-sufficient co-op signs players 2-4 in with no joystick. If that leaves a
        /// control setup active but without any device (its preference was never
        /// applied, or the configured pad was not found at load), re-apply the
        /// preference configured in Input Settings, defaulting to vanilla's "specific
        /// gamepad" for players 2-4. Player 1 keeps whatever it has: with the default
        /// "any" preference it owns the keyboard and every joystick, which is why a
        /// pad meant for player 2 also moves player 1 until Input Settings say
        /// otherwise.
        /// </summary>
        internal static void EnsurePlayerControllers(RainWorldGame game)
        {
            if (!selfSufficientCoop) return;
            try
            {
                global::Options options = game?.rainWorld?.options;
                int players = game?.session?.Players?.Count ?? 0;
                if (options?.controls == null) return;
                for (int i = 1; i < players && i < options.controls.Length; i++)
                {
                    global::Options.ControlSetup setup = options.controls[i];
                    if (setup == null) continue;
                    if (!setup.GetActive()) setup.SetActive(true);
                    if (setup.player == null) setup.InitRewiredObjects();
                    if (setup.player == null) { sLogger?.LogWarning($"[Input] player {i} has no Rewired player"); continue; }
                    if (setup.player.controllers.joystickCount > 0 || setup.player.controllers.hasKeyboard) continue;
                    global::Options.ControlSetup.ControlToUse preference = setup.GetControlPreference();
                    if (preference == global::Options.ControlSetup.ControlToUse.UNDEFINED)
                        preference = global::Options.ControlSetup.ControlToUse.SPECIFIC_GAMEPAD;
                    setup.UpdateControlPreference(preference, forceUpdate: true);
                    sLogger?.LogInfo($"[Input] frame={Time.frameCount} player {i} had no device; re-applied preference {preference} " +
                        $"(gamePad {setup.gamePadNumber}); joysticks now={setup.player.controllers.joystickCount} keyboard={setup.player.controllers.hasKeyboard}");
                }
            }
            catch (Exception error) { LogHookError("EnsurePlayerControllers", error); }
        }

        /// <summary>
        /// One line per heartbeat with the numbers behind "it lags": the median, average,
        /// 95th and 99th percentile and worst of every unpaused frame since the last
        /// heartbeat, the garbage collections in that time and the managed allocation
        /// rate that causes them (each collection stops the game), which cameras
        /// rendered, how many rooms are realized (every one of them updates every tick)
        /// against the realizer budget, and each camera's sprite leaser count (a room's
        /// object count; growing without bound means a leak).
        /// </summary>
        private void LogPerformance(RainWorldGame game)
        {
            float windowSeconds = Mathf.Max(0.001f, Time.realtimeSinceStartup - perfWindowStart);
            float megabytes = 1024f * 1024f;
            string memory = $"windowS={windowSeconds:0.0} gcCollections={perfGcCollections} gcPerMin={perfGcCollections * 60f / windowSeconds:0.0} " +
                $"allocMBps={allocationMeter.Bytes / megabytes / windowSeconds:0.00} heapMB={GC.GetTotalMemory(false) / megabytes:0} " +
                $"pausedFrames={perfPausedFrames} pausedMaxMs={perfPausedMaxSeconds * 1000f:0}";
            if (perfWindow.Frames == 0)
            {
                Logger.LogInfo($"[Perf] frame={Time.frameCount} paused for the whole window; {memory}");
                ResetPerfWindow();
                return;
            }
            float average = perfWindow.Average;
            float median = perfWindow.Percentile(0.5f);
            float p95 = perfWindow.Percentile(0.95f);
            float p99 = perfWindow.Percentile(0.99f);
            float worst = perfWindow.Max;
            typicalFrameSeconds = Mathf.Clamp(median, 1f / 1000f, 0.1f);
            int turns = frameRenderCamera >= 0 ? Mathf.Max(1, renderedCameraNumbers.Count) : 1;
            int realized = 0;
            var names = new List<string>(8);
            if (game?.world?.activeRooms != null)
                foreach (Room room in game.world.activeRooms)
                {
                    if (room?.abstractRoom == null || room.abstractRoom.offScreenDen) continue;
                    realized++;
                    if (names.Count < 12) names.Add(room.abstractRoom.name);
                }
            var leasers = new List<string>(4);
            if (game?.cameras != null)
                foreach (RoomCamera camera in game.cameras)
                    leasers.Add((camera?.spriteLeasers?.Count ?? -1).ToString());
            float budget = game?.roomRealizer?.performanceBudget ?? 0f;
            Logger.LogInfo($"[Perf] frame={Time.frameCount} frames={perfWindow.Frames} medianMs={median * 1000f:0.0} avgMs={average * 1000f:0.0} p95Ms={p95 * 1000f:0.0} p99Ms={p99 * 1000f:0.0} maxMs={worst * 1000f:0} fps={1f / Mathf.Max(0.0001f, average):0} fpsPerView={1f / Mathf.Max(0.0001f, average) / turns:0} frameLimit={Application.targetFrameRate} vsync={QualitySettings.vSyncCount} {memory} rendered=[{string.Join(",", renderedCameraNumbers)}] alternate={alternateFrames} realizedRooms={realized} [{string.Join(",", names)}] budget={budget:0} tickMs={(perfTickSum > 0 ? perfUpdateMsSum / perfTickSum : 0):0.0} ticksPerFrame={(perfFrameSum > 0 ? (double)perfTickSum / perfFrameSum : 0):0.00} modTickMs={(perfFrameSum > 0 ? perfModTickMsSum / perfFrameSum : 0):0.0} grafMs={(perfFrameSum > 0 ? perfGrafMsSum / perfFrameSum : 0):0.0} rainWorldMs={(perfFrameSum > 0 ? perfRainWorldMsSum / perfFrameSum : 0):0.0} futileMs={(perfFrameSum > 0 ? perfFutileMsSum / perfFrameSum : 0):0.0} renderMs={(perfFrameSum > 0 ? perfRenderMsSum / perfFrameSum : 0):0.0} replayMs={(perfFrameSum > 0 ? perfReplayMsSum / perfFrameSum : 0):0.00} replaysPerFrame={(perfFrameSum > 0 ? (double)perfReplaySum / perfFrameSum : 0):0.0} leasers=[{string.Join(",", leasers)}] maskSources={placedMaskSources.Count}");
            ResetPerfWindow();
            // Resources.FindObjectsOfTypeAll walks every loaded object: once a minute, not every heartbeat.
            if (++heartbeatsSinceTextureCensus >= 6)
            {
                heartbeatsSinceTextureCensus = 0;
                LogRenderTextures();
            }
        }

        /// <summary>
        /// Render texture census, every heartbeat. A count that climbs across
        /// rooms is a leak; the memory total says whether the GPU is being pushed
        /// into the paging that shows up as multi-second frames and garbage in
        /// grab-based shaders. Names also reveal Unity's grab textures.
        /// </summary>
        private void LogRenderTextures()
        {
            try
            {
                RenderTexture[] textures = Resources.FindObjectsOfTypeAll<RenderTexture>();
                var byName = new Dictionary<string, int>();
                double megabytes = 0;
                foreach (RenderTexture texture in textures)
                {
                    if (texture == null) continue;
                    megabytes += (double)texture.width * texture.height * 4 / (1024 * 1024);
                    string name = string.IsNullOrEmpty(texture.name) ? "(unnamed)" : texture.name;
                    int n;
                    byName.TryGetValue(name, out n);
                    byName[name] = n + 1;
                }
                var top = new List<KeyValuePair<string, int>>(byName);
                top.Sort((a, b) => b.Value.CompareTo(a.Value));
                var parts = new List<string>(8);
                for (int i = 0; i < top.Count && i < 8; i++) parts.Add(top[i].Key + "x" + top[i].Value);
                Logger.LogInfo($"[Perf] frame={Time.frameCount} renderTextures={textures.Length} approxMB={megabytes:0} top=[{string.Join(", ", parts)}]");
            }
            catch (Exception error) { LogHookError("LogRenderTextures", error); }
        }

        private static string RenderTargetState(CameraListener listener)
        {
            RenderTexture texture = listener?.fcamera?.targetTexture;
            if (texture == null) return "null";
            return $"{texture.name}:{texture.width}x{texture.height}:created={texture.IsCreated()}";
        }

        /// <summary>Everything the [CameraState] key says, hashed without allocating.</summary>
        private static long CameraStateSignature(RainWorldGame game, RoomCamera roomCamera, Creature followed,
            Camera unityCamera, CameraListener listener, bool turns, bool zoomed)
        {
            unchecked
            {
                long h = (long)CurrentSplitMode;
                h = h * 31 + (game.world?.name?.GetHashCode() ?? 0);
                h = h * 31 + RoomSignature(roomCamera?.room);
                h = h * 31 + RoomSignature(roomCamera?.loadingRoom);
                h = h * 31 + (roomCamera?.currentCameraPosition ?? -2);
                h = h * 31 + PlayerNumber(roomCamera?.followAbstractCreature);
                h = h * 31 + RoomSignature(followed?.room);
                h = h * 31 + (turns ? 2 : unityCamera == null ? 3 : unityCamera.enabled ? 1 : 0);
                h = h * 31 + (listener == null ? 3 : listener.direct ? 1 : 0);
                RenderTexture target = listener?.fcamera?.targetTexture;
                h = h * 31 + (target == null ? 0 : target.GetInstanceID());
                h = h * 31 + (target == null ? 0 : target.width * 8191 + target.height);
                h = h * 31 + (target != null && target.IsCreated() ? 1 : 0);
                h = h * 31 + (zoomed ? 1 : 0);
                return h;
            }
        }

        private static int RoomSignature(Room room)
        {
            if (room == null) return -1;
            unchecked { return (room.world?.name?.GetHashCode() ?? 0) * 997 + (room.abstractRoom?.index ?? -1); }
        }

        private void ReconcileCameraRoom(RoomCamera camera, AbstractCreature player, bool immediate, string reason)
        {
            if (!ValidCameraNumber(camera) || player == null) return;
            int cameraNumber = camera.cameraNumber;
            if (cameraRoomResyncInProgress[cameraNumber]) return;
            Room desiredRoom = player.realizedCreature?.room ?? player.Room?.realizedRoom;
            // A load counts as in progress only while something will still apply it.
            // WarpMoveCameraActual on a camera that was never precast sets loadingRoom
            // and nothing else; treating that as "already on its way" kept two cameras
            // at room=null for a whole session.
            bool loadPending = camera.loadingRoom == desiredRoom &&
                (camera.applyPosChangeWhenTextureIsLoaded || (camera.useWarpMove && camera.warpApplyPosChangeWhenTextureIsLoaded));
            bool stuckLoad = camera.loadingRoom == desiredRoom && !loadPending;
            if (desiredRoom == null || camera.room == desiredRoom || loadPending)
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
            if (camera.AboutToSwitchRoom && !wrongWorld && !stuckLoad && mismatchAge < RoomMismatchRecoveryFrames * 4) return;

            int node = player.pos.abstractNode;
            int viewingNode = desiredRoom.CameraViewingNode(node >= 0 ? node : 0);
            Logger.LogWarning($"[CameraHealth] frame={Time.frameCount} cam={cameraNumber} forcing room resync after {mismatchAge} frames; from={RoomName(camera.room)} to={RoomName(desiredRoom)} loading={RoomName(camera.loadingRoom)} player={PlayerNumber(player)} node={node}; reason={reason}");
            try
            {
                cameraRoomResyncInProgress[cameraNumber] = true;
                camera.MoveCamera(desiredRoom, viewingNode);
                cameraListeners[cameraNumber]?.PrepareForRendering();
                roomMismatchSinceFrames[cameraNumber] = -1;
                lastCameraStateSignatures[cameraNumber] = long.MinValue;
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
