using System;
using System.Linq;
using UnityEngine;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        /// <summary>
        /// Give every additional player an independent room realizer. Sharing the
        /// primary realizer's mutable lists caused duplicate mutation and freezes.
        /// </summary>
        private void MakeRealizer2(RainWorldGame game)
        {
            additionalRealizers.Clear();
            realizer2 = null;
            if (game?.roomRealizer == null || game.session?.Players == null) return;

            foreach (AbstractCreature player in game.session.Players.Where(p => p != game.roomRealizer.followCreature))
            {
                var realizer = new RoomRealizer(player, game.world);
                additionalRealizers.Add(realizer);
            }
            realizer2 = additionalRealizers.FirstOrDefault();
            // Every realizer now measures the whole world's realized rooms against
            // one budget (see RoomRealizer_CurrentPerformanceEstimation), so the
            // budget grows with the player count instead of each realizer silently
            // owning a full vanilla budget of its own.
            // Vanilla keeps 1500 worth of rooms realized around one player. Every extra
            // player adds a share for their own surroundings; the Remix slider trades
            // room-load hitches (low) against rooms updating every tick (high).
            float perExtraPlayer = Options != null ? Options.ExtraRealizerBudget.Value : 750f;
            float budget = 1500f + perExtraPlayer * additionalRealizers.Count;
            game.roomRealizer.performanceBudget = budget;
            foreach (RoomRealizer realizer in additionalRealizers) realizer.performanceBudget = budget;
            Logger.LogInfo($"Created {additionalRealizers.Count} additional room realizer(s); shared performance budget={budget}");
        }

        /// <summary>
        /// Vanilla sums only the rooms this realizer tracks. With one realizer per
        /// player that let N realizers each fill a vanilla budget, so the number of
        /// realized rooms, and the creatures updating in them every tick, scaled with
        /// the player count. Measure everything that is actually realized instead.
        /// </summary>
        public float RoomRealizer_CurrentPerformanceEstimation(On.RoomRealizer.orig_CurrentPerformanceEstimation orig, RoomRealizer self)
        {
            float own = orig(self);
            if (additionalRealizers.Count == 0 || self?.world?.activeRooms == null) return own;
            float total = 0f;
            for (int i = 0; i < self.world.activeRooms.Count; i++)
            {
                Room room = self.world.activeRooms[i];
                if (room?.abstractRoom != null && !room.abstractRoom.offScreenDen)
                    total += self.RoomPerformanceEstimation(room.abstractRoom);
            }
            return Mathf.Max(own, total);
        }

        private void TrackShortcutDestination(World world, AbstractRoom room)
        {
            if (world == null || room == null) return;
            world.ActivateRoom(room);
            RainWorldGame game = world.game;
            if (game?.roomRealizer?.world == world)
                game.roomRealizer.AddNewTrackedRoom(room, true);
            foreach (RoomRealizer realizer in additionalRealizers)
                if (realizer?.world == world)
                    realizer.AddNewTrackedRoom(room, false);
            Logger.LogInfo($"[CameraPreload] frame={Time.frameCount} destination={room.name}; realized={room.realizedRoom != null}; trackedRealizers={additionalRealizers.Count + (game?.roomRealizer == null ? 0 : 1)}");
        }

        public void OverWorld_WorldLoaded(On.OverWorld.orig_WorldLoaded orig, OverWorld self, bool warpUsed)
        {
            bool rebuild = additionalRealizers.Count > 0;
            additionalRealizers.Clear();
            realizer2 = null;
            orig(self, warpUsed);
            if (rebuild || self.game?.session?.Players?.Count > 1) MakeRealizer2(self.game);
            ClearStaleWorldCameras(self.game);
            EnsureStableCameraAssignments(self.game);
            RefreshActiveCameraRendering(self.game, "world loaded");
            LogCameraSnapshot(self.game, "world loaded", true);
            ConsiderColapsing(self.game, true);
        }

        /// <summary>
        /// One player through a gate unloads the old world for everybody, but the
        /// cameras of the players left behind keep their room and every sprite
        /// leaser of it. Nothing draws those leasers usefully again, and the ones
        /// holding Unity objects (rot spore renderers, mask meshes) turn into
        /// per-frame exceptions once the objects are destroyed. Clean them the way
        /// ChangeRoom would; the camera is re-populated when its player is moved.
        /// </summary>
        private void ClearStaleWorldCameras(RainWorldGame game)
        {
            if (game?.cameras == null || game.world == null) return;
            foreach (RoomCamera camera in game.cameras)
            {
                if (camera?.room == null || camera.room.world == game.world || camera.spriteLeasers == null) continue;
                // A camera already heading for a room of the new world (every camera
                // during a warp: WarpMoveCameraActual runs before WorldLoaded finishes)
                // is on its way, not left behind. Clearing it here tore the warp's
                // ripple and hold-frame mask sources out from under the camera the
                // players were watching and left its colours broken, and emptied the
                // other two cells until their move completed.
                if (camera.loadingRoom != null) continue;
                int count = camera.spriteLeasers.Count;
                for (int i = 0; i < count; i++)
                {
                    try { camera.spriteLeasers[i].CleanSpritesAndRemove(); }
                    catch (Exception error) { LogHookError("ClearStaleWorldCameras", error); }
                }
                camera.spriteLeasers.Clear();
                Logger.LogInfo($"[CameraMove] frame={Time.frameCount} cam={camera.cameraNumber} cleared {count} sprite leasers left in unloaded world {camera.room.world.name}/{camera.room.abstractRoom?.name}");
            }
        }

        /// <summary>
        /// Vanilla always copies camera zero's target into a realizer. Temporarily
        /// expose this realizer's own target there so each instance remains stable.
        /// </summary>
        public void RoomRealizer_Update(On.RoomRealizer.orig_Update orig, RoomRealizer self)
        {
            RainWorldGame game = self?.world?.game;
            if (game?.cameras == null || game.cameras.Length == 0 || self == game.roomRealizer)
            {
                orig(self);
                return;
            }

            AbstractCreature previous = game.cameras[0].followAbstractCreature;
            try
            {
                if (self.followCreature != null) game.cameras[0].followAbstractCreature = self.followCreature;
                orig(self);
            }
            finally
            {
                game.cameras[0].followAbstractCreature = previous;
            }
        }

        private int lastKillRoomBlockFrame = -10000;

        /// <summary>
        /// RemoveNotVisitedRooms abstractizes straight through KillRoom without ever
        /// consulting CanAbstractizeRoom, so guarding that method alone is not enough:
        /// whenever one player walks into a new room, their realizer drops every room
        /// it has not personally visited, which includes the room the other player is
        /// standing in. Abstractizing it destroys that player's realized creature, and
        /// if they are mid-shortcut it is worse still - ShortcutHandler only advances a
        /// vessel while vessel.room.realizedRoom is non-null, so the player freezes in
        /// the pipe permanently and the layout then reports them dead.
        /// </summary>
        public void RoomRealizer_KillRoom(On.RoomRealizer.orig_KillRoom orig,
            RoomRealizer self, AbstractRoom room)
        {
            HangMarker = "RoomRealizer_KillRoom";
            if (room != null && (RoomIsInUseByAnyPlayer(self?.world?.game, room) || RoomIsHeldByAnotherRealizer(self, room)))
            {
                if (Time.frameCount - lastKillRoomBlockFrame > 200)
                {
                    Logger.LogInfo($"[RoomRealizer] frame={Time.frameCount} refused to abstractize {room.name}; a player, their camera, their realizer or a shortcut vessel still needs it");
                    lastKillRoomBlockFrame = Time.frameCount;
                }
                return;
            }
            orig(self, room);
        }

        /// <summary>
        /// A realizer only ever consults its own trackers before killing a room, so
        /// realizer B would drop the neighbour rooms realizer A had just loaded for
        /// player A, and A loaded them again on its next tick. Each reload is a full
        /// room load on the main thread. Refuse while any other realizer still counts
        /// the room as too fresh or too close to abstractize.
        /// </summary>
        private static bool RoomIsHeldByAnotherRealizer(RoomRealizer self, AbstractRoom room)
        {
            RainWorldGame game = self?.world?.game;
            if (game == null) return false;
            if (game.roomRealizer != null && game.roomRealizer != self && RealizerNeedsRoom(game.roomRealizer, room)) return true;
            foreach (RoomRealizer other in additionalRealizers)
                if (other != null && other != self && RealizerNeedsRoom(other, room)) return true;
            return false;
        }

        private static bool RealizerNeedsRoom(RoomRealizer realizer, AbstractRoom room)
        {
            if (realizer.followCreature == null || realizer.realizedRooms == null) return false;
            for (int i = 0; i < realizer.realizedRooms.Count; i++)
            {
                var tracker = realizer.realizedRooms[i];
                if (tracker?.room != room) continue;
                try { return !realizer.CanAbstractizeRoom(tracker); }
                catch (Exception) { return false; }
            }
            return false;
        }

        private static bool RoomIsInUseByAnyPlayer(RainWorldGame game, AbstractRoom room)
        {
            if (game == null || room == null) return false;

            if (game.session?.Players != null)
                foreach (AbstractCreature player in game.session.Players)
                {
                    if (player == null) continue;
                    if ((player.state as PlayerState)?.permaDead == true) continue;
                    if (player.Room == room) return true;
                    if (player.realizedCreature?.room?.abstractRoom == room) return true;
                }

            if (game.cameras != null)
                foreach (RoomCamera camera in game.cameras)
                {
                    if (camera == null) continue;
                    if (camera.room?.abstractRoom == room) return true;
                    if (camera.loadingRoom?.abstractRoom == room) return true;
                }

            ShortcutHandler shortcuts = game.shortcuts;
            if (shortcuts != null)
            {
                if (shortcuts.transportVessels != null)
                    foreach (ShortcutHandler.ShortCutVessel vessel in shortcuts.transportVessels)
                        if (vessel?.creature is Player && vessel.room == room) return true;
                if (shortcuts.betweenRoomsWaitingLobby != null)
                    foreach (ShortcutHandler.Vessel vessel in shortcuts.betweenRoomsWaitingLobby)
                        if (vessel?.creature is Player && vessel.room == room) return true;
                if (shortcuts.borderTravelVessels != null)
                    foreach (ShortcutHandler.BorderVessel vessel in shortcuts.borderTravelVessels)
                        if (vessel?.creature is Player && vessel.room == room) return true;
            }
            return false;
        }

        public bool rrNestedLock;

        /// <summary>
        /// A room may abstract only when every player realizer agrees it is safe.
        /// </summary>
        public bool RoomRealizer_CanAbstractizeRoom(On.RoomRealizer.orig_CanAbstractizeRoom orig,
            RoomRealizer self, RoomRealizer.RealizedRoomTracker tracker)
        {
            bool result = orig(self, tracker);
            if (!result || rrNestedLock) return result;

            try
            {
                rrNestedLock = true;
                RoomRealizer primary = self?.world?.game?.roomRealizer;
                if (primary != null && primary != self && primary.followCreature != null)
                    result &= primary.CanAbstractizeRoom(tracker);
                foreach (RoomRealizer other in additionalRealizers)
                    if (other != null && other != self && other.followCreature != null)
                        result &= other.CanAbstractizeRoom(tracker);
                return result;
            }
            finally
            {
                rrNestedLock = false;
            }
        }
    }
}
