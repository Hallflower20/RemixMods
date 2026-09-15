using System;
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
                int lastCompletedFrame = listener.direct ? listener.lastPostRenderFrame : listener.lastCompositeFrame;
                int newestRenderFrame = Math.Max(listener.renderingExpectedSinceFrame, lastCompletedFrame);
                int renderAge = Time.frameCount - newestRenderFrame;
                if (renderAge <= RenderStallFrames || Time.frameCount - listener.lastRecoveryFrame <= RenderRecoveryCooldown) continue;

                Logger.LogWarning($"[CameraHealth] frame={Time.frameCount} cam={i} stopped rendering/compositing for {renderAge} frames; enabled={unityCamera.enabled}; active={unityCamera.gameObject.activeInHierarchy}; direct={listener.direct}; target={RenderTargetState(listener)}; roomUpdateAge={FrameAge(lastRoomCameraUpdateFrames[i])}; roomDrawAge={FrameAge(lastRoomCameraDrawFrames[i])}; preRenderAge={FrameAge(listener.lastPreRenderFrame)}; postRenderAge={FrameAge(listener.lastPostRenderFrame)}; compositeAge={FrameAge(listener.lastCompositeFrame)}; attempting render-target recovery");
                listener.RecoverRendering();
                unityCamera.enabled = true;
                lastCameraStateKeys[i] = null;
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
                Logger.LogInfo($"[CameraState] frame={Time.frameCount} reason={reason} cam={i} {key}; cameraPos={roomCamera?.pos}; playerPos={followedCreature?.mainBodyChunk?.pos}; inShortcut={followedCreature?.inShortcut}; mapVisible={roomCamera?.hud?.map?.visible}; roomUpdateAge={FrameAge(lastRoomCameraUpdateFrames[i])}; roomDrawAge={FrameAge(lastRoomCameraDrawFrames[i])}; preRenderAge={FrameAge(listener?.lastPreRenderFrame ?? -1)}; postRenderAge={FrameAge(listener?.lastPostRenderFrame ?? -1)}; compositeAge={FrameAge(listener?.lastCompositeFrame ?? -1)}");
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
