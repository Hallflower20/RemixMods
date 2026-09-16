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
            Logger.LogInfo($"Created {additionalRealizers.Count} additional room realizer(s)");
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
            EnsureStableCameraAssignments(self.game);
            RefreshActiveCameraRendering(self.game, "world loaded");
            LogCameraSnapshot(self.game, "world loaded", true);
            ConsiderColapsing(self.game, true);
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
